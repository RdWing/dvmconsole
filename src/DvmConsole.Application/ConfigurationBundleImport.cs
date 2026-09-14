// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.IO.Compression;

namespace DvmConsole.Application;

/// <summary>Reads portable configuration bundles without extracting untrusted paths to disk.</summary>
public sealed class ConfigurationBundleImport : IImportDocumentSet, IDisposable
{
    private const int MaximumBytes = 64 * 1024 * 1024;
    private readonly Dictionary<string, Document> documents = new(StringComparer.OrdinalIgnoreCase);

    private ConfigurationBundleImport() { }

    public IReadableDocument Primary => documents["configuration.yaml"];

    public static async Task<ConfigurationBundleImport> ReadAsync(IReadableDocument source,
        CancellationToken cancellationToken = default)
    {
        var result = new ConfigurationBundleImport();
        try
        {
            await using Stream input = await source.OpenReadAsync(cancellationToken).ConfigureAwait(false);
            using var buffered = new MemoryStream();
            await CopyBoundedAsync(input, buffered, MaximumBytes, cancellationToken).ConfigureAwait(false);
            buffered.Position = 0;
            using var archive = new ZipArchive(buffered, ZipArchiveMode.Read);
            if (archive.Entries.Count > 256) throw new InvalidDataException("The bundle contains too many files.");
            int remaining = MaximumBytes;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string name = entry.FullName;
                if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(['/', '\\', ':', '\0']) >= 0)
                    throw new InvalidDataException("Bundle files must use plain filenames.");
                if (result.documents.ContainsKey(name)) throw new InvalidDataException("The bundle contains duplicate filenames.");
                if (entry.Length > remaining) throw new InvalidDataException("The expanded bundle exceeds 64 MB.");
                using Stream content = entry.Open();
                using var bytes = new MemoryStream();
                await CopyBoundedAsync(content, bytes, remaining, cancellationToken).ConfigureAwait(false);
                remaining -= checked((int)bytes.Length);
                result.documents.Add(name, new Document(name, source.OriginIdentity, bytes.ToArray()));
            }
            if (!result.documents.ContainsKey("configuration.yaml"))
                throw new InvalidDataException("The bundle must contain configuration.yaml.");
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    public ValueTask<IReadableDocument?> ResolveCompanionAsync(string relativeReference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string name = relativeReference.StartsWith("./", StringComparison.Ordinal) ? relativeReference[2..] : relativeReference;
        return ValueTask.FromResult<IReadableDocument?>(documents.GetValueOrDefault(name));
    }

    public void Dispose()
    {
        foreach (Document document in documents.Values) Array.Clear(document.Bytes);
        documents.Clear();
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination, int limit, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81920];
        int total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (read > limit - total) throw new InvalidDataException("The configuration bundle exceeds 64 MB.");
            total += read;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record Document(string DisplayName, string? OriginIdentity, byte[] Bytes) : IReadableDocument
    {
        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream(Bytes, writable: false));
        }
    }
}
