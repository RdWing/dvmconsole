// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Settings;
using System.Buffers.Binary;

namespace DvmConsole.Storage;

public sealed class ManagedAssetStore : IAssetStore
{
    internal const long MaximumAssetBytes = 25L * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string root;
    private readonly string catalogPath;
    private readonly string lockPath;

    public ManagedAssetStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        root = Path.GetFullPath(rootPath);
        catalogPath = Path.Combine(root, "catalog.json");
        lockPath = Path.Combine(root, ".store.lock");
        AppDataFileProtection.EnsureDirectory(root, repairExistingTree: true);
        AppDataFileProtection.EnsureDirectory(Path.Combine(root, "content"));
        using IDisposable processLock = CrossProcessStoreLock.Acquire(lockPath);
        AtomicJsonFile.Recover(catalogPath, StorageJsonContext.Default.ListAssetDescriptor);
        if (!File.Exists(catalogPath))
            WriteCatalog([]);
        RecoverContentTransactions();
    }

    public async ValueTask<AssetDescriptor> ImportAsync(
        string displayName,
        string mediaType,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        ArgumentNullException.ThrowIfNull(content);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? pending = null;
        string? destination = null;
        bool moved = false;
        try
        {
            using IDisposable processLock = CrossProcessStoreLock.Acquire(lockPath);
            AssetId id = AssetId.New();
            destination = ContentPath(id);
            pending = destination + ".pending";
            await using (var output = new FileStream(
                             pending,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous))
            {
                await CopyBoundedAsync(content, output, MaximumAssetBytes, cancellationToken)
                    .ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            AppDataFileProtection.EnsureFile(pending);
            ValidateContent(pending, mediaType);
            File.Move(pending, destination);
            moved = true;
            AppDataFileProtection.EnsureFile(destination);
            var descriptor = new AssetDescriptor(
                id,
                displayName.Trim(),
                mediaType.Trim(),
                new FileInfo(destination).Length);
            List<AssetDescriptor> catalog = ReadCatalog();
            catalog.Add(descriptor);
            WriteCatalog(catalog);
            return descriptor;
        }
        catch
        {
            if (moved && destination is not null && File.Exists(destination))
                File.Delete(destination);
            throw;
        }
        finally
        {
            if (pending is not null && File.Exists(pending))
                File.Delete(pending);
            gate.Release();
        }
    }

    public async ValueTask<Stream> OpenReadAsync(
        AssetId id,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using IDisposable processLock = CrossProcessStoreLock.Acquire(lockPath);
            if (!ReadCatalog().Any(asset => asset.Id == id))
                throw new KeyNotFoundException($"Asset '{id}' is not in the managed store.");
            return new FileStream(
                ContentPath(id),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous);
        }
        finally
        {
            gate.Release();
        }
    }

    public async IAsyncEnumerable<AssetDescriptor> ListAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AssetDescriptor[] snapshot;
        try
        {
            using IDisposable processLock = CrossProcessStoreLock.Acquire(lockPath);
            snapshot = ReadCatalog().OrderBy(asset => asset.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally
        {
            gate.Release();
        }
        foreach (AssetDescriptor descriptor in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return descriptor;
        }
    }

    public async ValueTask<bool> DeleteIfUnreferencedAsync(
        AssetId id,
        IReadOnlyCollection<AssetId> referencedAssets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(referencedAssets);
        if (referencedAssets.Contains(id))
            return false;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using IDisposable processLock = CrossProcessStoreLock.Acquire(lockPath);
            List<AssetDescriptor> catalog = ReadCatalog();
            AssetDescriptor? descriptor = catalog.FirstOrDefault(asset => asset.Id == id);
            if (descriptor is null || referencedAssets.Contains(id))
                return false;

            string path = ContentPath(id);
            string trash = path + ".deleting";
            if (File.Exists(path))
                File.Move(path, trash, overwrite: true);
            try
            {
                catalog.RemoveAll(asset => asset.Id == id);
                WriteCatalog(catalog);
                if (File.Exists(trash))
                    File.Delete(trash);
                return true;
            }
            catch
            {
                if (File.Exists(trash))
                    File.Move(trash, path, overwrite: true);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private void RecoverContentTransactions()
    {
        var referenced = ReadCatalog().Select(asset => asset.Id.Value.ToString("N"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string contentRoot = Path.Combine(root, "content");
        foreach (string path in Directory.EnumerateFiles(contentRoot))
        {
            string name = Path.GetFileName(path);
            string identity = name.Split('.', 2)[0];
            if (!Guid.TryParseExact(identity, "N", out _))
                continue;
            string committed = Path.Combine(contentRoot, identity + ".asset");
            if (name.EndsWith(".asset.deleting", StringComparison.Ordinal))
            {
                if (referenced.Contains(identity) && !File.Exists(committed))
                    File.Move(path, committed);
                else
                    File.Delete(path);
            }
            else if (name.EndsWith(".asset.pending", StringComparison.Ordinal) ||
                     (name.EndsWith(".asset", StringComparison.Ordinal) && !referenced.Contains(identity)))
            {
                string quarantine = Path.Combine(root, "quarantine");
                AppDataFileProtection.EnsureDirectory(quarantine);
                File.Move(path, Path.Combine(quarantine, name + "." + Guid.NewGuid().ToString("N")));
            }
        }
    }

    private string ContentPath(AssetId id) => Path.Combine(root, "content", id.Value.ToString("N") + ".asset");

    private List<AssetDescriptor> ReadCatalog()
        => AtomicJsonFile.Read(catalogPath, StorageJsonContext.Default.ListAssetDescriptor);

    private void WriteCatalog(List<AssetDescriptor> catalog)
        => AtomicJsonFile.Write(catalogPath, catalog, StorageJsonContext.Default.ListAssetDescriptor);

    private static void ValidateContent(string path, string mediaType)
    {
        Span<byte> header = stackalloc byte[40];
        using FileStream stream = File.OpenRead(path);
        int length = stream.Read(header);
        ReadOnlySpan<byte> value = header[..length];
        string normalized = mediaType.Trim().ToLowerInvariant();
        bool valid = normalized switch
        {
            "image/png" => IsPng(value, stream.Length),
            "image/jpeg" => IsJpeg(value, stream),
            "image/gif" => IsGif(value, stream.Length),
            "image/bmp" => IsBitmap(value, stream.Length),
            "image/webp" => IsWebP(value, stream.Length),
            "audio/wav" or "audio/x-wav" => IsWave(stream),
            "audio/ogg" or "audio/opus" => IsOgg(stream),
            "audio/mpeg" => IsMpeg(value, stream.Length),
            "application/octet-stream" => true,
            _ when normalized.StartsWith("image/", StringComparison.Ordinal) ||
                normalized.StartsWith("audio/", StringComparison.Ordinal) => false,
            _ => true,
        };
        if (!valid)
            throw new InvalidDataException($"The imported content is not valid '{mediaType}' media.");
    }

    private static bool IsPng(ReadOnlySpan<byte> header, long length)
        => length >= 33 &&
           header.Length >= 33 &&
           header.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) &&
           BinaryPrimitives.ReadUInt32BigEndian(header[8..12]) == 13 &&
           header[12..16].SequenceEqual("IHDR"u8) &&
           BinaryPrimitives.ReadUInt32BigEndian(header[16..20]) > 0 &&
           BinaryPrimitives.ReadUInt32BigEndian(header[20..24]) > 0;

    private static bool IsJpeg(ReadOnlySpan<byte> header, FileStream stream)
    {
        if (stream.Length < 4 || header.Length < 3 ||
            !header[..3].SequenceEqual(new byte[] { 0xFF, 0xD8, 0xFF }))
            return false;
        Span<byte> trailer = stackalloc byte[2];
        stream.Position = stream.Length - trailer.Length;
        return stream.Read(trailer) == trailer.Length && trailer.SequenceEqual(new byte[] { 0xFF, 0xD9 });
    }

    private static bool IsGif(ReadOnlySpan<byte> header, long length)
        => length >= 13 &&
           header.Length >= 13 &&
           (header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8)) &&
           BinaryPrimitives.ReadUInt16LittleEndian(header[6..8]) > 0 &&
           BinaryPrimitives.ReadUInt16LittleEndian(header[8..10]) > 0;

    private static bool IsBitmap(ReadOnlySpan<byte> header, long length)
        => length >= 26 &&
           header.Length >= 26 &&
           header.StartsWith("BM"u8) &&
           BinaryPrimitives.ReadUInt32LittleEndian(header[2..6]) <= length &&
           BinaryPrimitives.ReadUInt32LittleEndian(header[10..14]) < length;

    private static bool IsWebP(ReadOnlySpan<byte> header, long length)
        => length >= 20 &&
           header.Length >= 20 &&
           header.StartsWith("RIFF"u8) &&
           header[8..12].SequenceEqual("WEBP"u8) &&
           BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]) + 8UL <= (ulong)length;

    private static bool IsWave(FileStream stream)
    {
        if (stream.Length < 44)
            return false;
        stream.Position = 0;
        Span<byte> riff = stackalloc byte[12];
        if (stream.Read(riff) != riff.Length ||
            !riff[..4].SequenceEqual("RIFF"u8) ||
            !riff[8..12].SequenceEqual("WAVE"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(riff[4..8]) + 8UL > (ulong)stream.Length)
            return false;

        bool hasFormat = false;
        bool hasData = false;
        Span<byte> chunk = stackalloc byte[8];
        while (stream.Position + chunk.Length <= stream.Length)
        {
            if (stream.Read(chunk) != chunk.Length)
                return false;
            uint chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..8]);
            long next = stream.Position + chunkLength + (chunkLength & 1);
            if (next > stream.Length)
                return false;
            if (chunk[..4].SequenceEqual("fmt "u8))
                hasFormat = chunkLength >= 16;
            else if (chunk[..4].SequenceEqual("data"u8))
                hasData = chunkLength > 0;
            stream.Position = next;
            if (hasFormat && hasData)
                return true;
        }
        return false;
    }

    private static bool IsOgg(FileStream stream)
    {
        if (stream.Length < 28)
            return false;
        stream.Position = 0;
        Span<byte> pageHeader = stackalloc byte[27];
        if (stream.Read(pageHeader) != pageHeader.Length ||
            !pageHeader.StartsWith("OggS"u8) || pageHeader[4] != 0)
            return false;
        int segmentCount = pageHeader[26];
        if (27 + segmentCount > stream.Length)
            return false;
        Span<byte> segments = stackalloc byte[255];
        if (stream.Read(segments[..segmentCount]) != segmentCount)
            return false;
        int payloadLength = 0;
        for (int index = 0; index < segmentCount; index++)
            payloadLength += segments[index];
        return payloadLength > 0 && 27L + segmentCount + payloadLength <= stream.Length;
    }

    private static bool IsMpeg(ReadOnlySpan<byte> header, long length)
    {
        if (length < 4 || header.Length < 4)
            return false;
        if (header.StartsWith("ID3"u8))
        {
            if (length < 10 || header.Length < 10 || header[6] > 0x7F || header[7] > 0x7F ||
                header[8] > 0x7F || header[9] > 0x7F)
                return false;
            int tagLength = (header[6] << 21) | (header[7] << 14) | (header[8] << 7) | header[9];
            return 10L + tagLength < length;
        }
        return header[0] == 0xFF && (header[1] & 0xE0) == 0xE0;
    }

    private static async Task CopyBoundedAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return;
            total = checked(total + read);
            if (total > maximumBytes)
                throw new InvalidDataException($"Managed assets cannot exceed {maximumBytes / (1024 * 1024)} MiB.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }
}
