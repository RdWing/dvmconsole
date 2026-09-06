// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text;
using DvmConsole.Application;
using DvmConsole.Operations;
using Xunit;

namespace DvmConsole.Storage.Tests;

public sealed class ManagedStoresTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dvmconsole-stores-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RecoveryRestoresUncommittedDeletionAndQuarantinesUncommittedImport()
    {
        string assetRoot = Path.Combine(root, "assets");
        var store = new ManagedAssetStore(assetRoot);
        AssetDescriptor imported = await store.ImportAsync("Retain", "application/octet-stream", new MemoryStream([1, 2, 3]));
        string content = Path.Combine(assetRoot, "content");
        string committed = Path.Combine(content, imported.Id.Value.ToString("N") + ".asset");
        File.Move(committed, committed + ".deleting");
        string pending = Path.Combine(content, Guid.NewGuid().ToString("N") + ".asset.pending");
        File.WriteAllBytes(pending, [4, 5]);
        string deleted = Path.Combine(content, Guid.NewGuid().ToString("N") + ".asset.deleting");
        File.WriteAllBytes(deleted, [6]);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            store = new ManagedAssetStore(assetRoot);
            await using Stream restored = await store.OpenReadAsync(imported.Id);
            Assert.Equal(3, restored.Length);
            Assert.False(File.Exists(committed + ".deleting"));
            Assert.False(File.Exists(pending));
            Assert.False(File.Exists(deleted));
            Assert.Single(Directory.EnumerateFiles(Path.Combine(assetRoot, "quarantine")));
        }
    }

    [Fact]
    public async Task AssetImportOwnsACopyAndReturnsStreams()
    {
        var store = new ManagedAssetStore(Path.Combine(root, "assets"));
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes("alert"));
        AssetDescriptor imported = await store.ImportAsync("Alert", "application/octet-stream", source);
        source.SetLength(0);

        await using Stream read = await store.OpenReadAsync(imported.Id);
        using var reader = new StreamReader(read);
        Assert.Equal("alert", await reader.ReadToEndAsync());
        Assert.Single(await ReadAllAsync(store.ListAsync()));
    }

    [Fact]
    public async Task ConcurrentAssetStoreInstancesMergeCatalogUpdates()
    {
        string assetRoot = Path.Combine(root, "assets");
        var first = new ManagedAssetStore(assetRoot);
        var second = new ManagedAssetStore(assetRoot);

        await Task.WhenAll(
            first.ImportAsync("First", "application/octet-stream", new MemoryStream([1])).AsTask(),
            second.ImportAsync("Second", "application/octet-stream", new MemoryStream([2])).AsTask());

        List<AssetDescriptor> assets = await ReadAllAsync(first.ListAsync());
        Assert.Equal(2, assets.Count);
        Assert.Contains(assets, asset => asset.DisplayName == "First");
        Assert.Contains(assets, asset => asset.DisplayName == "Second");
    }

    [Fact]
    public async Task InvalidDeclaredMediaIsRejectedBeforeCatalogCommit()
    {
        string assetRoot = Path.Combine(root, "assets");
        var store = new ManagedAssetStore(assetRoot);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.ImportAsync("Not audio", "audio/wav", new MemoryStream("not a wave"u8.ToArray())));

        Assert.Contains("not valid", error.Message, StringComparison.Ordinal);
        Assert.Empty(await ReadAllAsync(store.ListAsync()));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(assetRoot, "content")));
    }

    [Fact]
    public async Task TruncatedMediaSignaturesAreRejectedBeforeCatalogCommit()
    {
        string assetRoot = Path.Combine(root, "assets");
        var store = new ManagedAssetStore(assetRoot);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.ImportAsync(
                "Truncated image",
                "image/png",
                new MemoryStream([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.ImportAsync(
                "Truncated audio",
                "audio/wav",
                new MemoryStream("RIFF\x04\0\0\0WAVE"u8.ToArray())));

        Assert.Empty(await ReadAllAsync(store.ListAsync()));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(assetRoot, "content")));
    }

    [Fact]
    public async Task AssetDeletionRetainsReferencedContentAndRemovesUnreferencedContent()
    {
        string assetRoot = Path.Combine(root, "assets");
        var store = new ManagedAssetStore(assetRoot);
        AssetDescriptor imported = await store.ImportAsync(
            "Shared",
            "application/octet-stream",
            new MemoryStream([1, 2, 3]));

        Assert.False(await store.DeleteIfUnreferencedAsync(imported.Id, [imported.Id]));
        Assert.Single(await ReadAllAsync(store.ListAsync()));
        Assert.True(await store.DeleteIfUnreferencedAsync(imported.Id, []));
        Assert.Empty(await ReadAllAsync(store.ListAsync()));
        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            await store.OpenReadAsync(imported.Id));
    }

    [Fact]
    public async Task OversizedAssetImportIsRejectedWithoutLeavingPendingContent()
    {
        string assetRoot = Path.Combine(root, "assets");
        var store = new ManagedAssetStore(assetRoot);
        await using var source = new RepeatingByteStream((25L * 1024 * 1024) + 1);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.ImportAsync("Oversized", "audio/wav", source));

        Assert.Contains("25 MiB", error.Message, StringComparison.Ordinal);
        Assert.Empty(await ReadAllAsync(store.ListAsync()));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(assetRoot, "content")));
    }

    [Fact]
    public async Task CorruptAssetCatalogRecoversTheLastDurableBackup()
    {
        string assetRoot = Path.Combine(root, "assets");
        var store = new ManagedAssetStore(assetRoot);
        await store.ImportAsync("First", "application/octet-stream", new MemoryStream([1]));
        await store.ImportAsync("Second", "application/octet-stream", new MemoryStream([2]));
        string catalogPath = Path.Combine(assetRoot, "catalog.json");
        File.WriteAllText(catalogPath, "not json");

        var recovered = new ManagedAssetStore(assetRoot);

        AssetDescriptor descriptor = Assert.Single(await ReadAllAsync(recovered.ListAsync()));
        Assert.Equal("First", descriptor.DisplayName);
    }

    [Fact]
    public async Task ManagedStoresUseOwnerOnlyUnixPermissions()
    {
        if (OperatingSystem.IsWindows())
            return;

        string assetRoot = Path.Combine(root, "assets");
        var store = new ManagedAssetStore(assetRoot);
        AssetDescriptor descriptor = await store.ImportAsync(
            "Alert",
            "application/octet-stream",
            new MemoryStream([1, 2, 3]));

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(assetRoot));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(Path.Combine(assetRoot, "catalog.json")));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(Path.Combine(assetRoot, "content", descriptor.Id.Value.ToString("N") + ".asset")));
    }

    [Fact]
    public async Task RecordingIsInvisibleUntilHandleCommits()
    {
        var store = new ManagedRecordingStore(Path.Combine(root, "recordings"));
        ChannelId channelId = new(new ChannelSessionId(
            "system",
            DvmConsole.Core.Runtime.ChannelProtocol.P25,
            1,
            0,
            "channel"));
        await using IRecordingWriteHandle handle = await store.CreateAsync(
            CallId.New(), channelId, DateTimeOffset.UtcNow, "audio/ogg");
        await handle.Stream.WriteAsync(Encoding.UTF8.GetBytes("recording"));
        Assert.Empty(await ReadAllAsync(store.ListAsync()));

        await handle.CommitAsync(TimeSpan.FromSeconds(1));

        RecordingDescriptor descriptor = Assert.Single(await ReadAllAsync(store.ListAsync()));
        Assert.True(descriptor.IsFinalized);
        await using Stream read = await store.OpenReadAsync(descriptor.Id);
        using var reader = new StreamReader(read);
        Assert.Equal("recording", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task ConcurrentRecordingStoreInstancesMergeCatalogUpdates()
    {
        string recordingRoot = Path.Combine(root, "recordings");
        var first = new ManagedRecordingStore(recordingRoot);
        var second = new ManagedRecordingStore(recordingRoot);
        ChannelId channelId = new(new ChannelSessionId(
            "system",
            DvmConsole.Core.Runtime.ChannelProtocol.P25,
            1,
            0,
            "channel"));
        await using IRecordingWriteHandle firstHandle = await first.CreateAsync(
            CallId.New(), channelId, DateTimeOffset.UtcNow, "audio/ogg");
        await using IRecordingWriteHandle secondHandle = await second.CreateAsync(
            CallId.New(), channelId, DateTimeOffset.UtcNow, "audio/ogg");
        await firstHandle.Stream.WriteAsync(new byte[] { 1 });
        await secondHandle.Stream.WriteAsync(new byte[] { 2 });

        await Task.WhenAll(
            firstHandle.CommitAsync(TimeSpan.FromSeconds(1)).AsTask(),
            secondHandle.CommitAsync(TimeSpan.FromSeconds(1)).AsTask());

        Assert.Equal(2, (await ReadAllAsync(first.ListAsync())).Count);
    }

    [Fact]
    public void OversizedManagedCatalogIsRejectedBeforeJsonParsing()
    {
        string assetRoot = Path.Combine(root, "assets");
        _ = new ManagedAssetStore(assetRoot);
        string catalogPath = Path.Combine(assetRoot, "catalog.json");
        using (FileStream stream = new(catalogPath, FileMode.Create, FileAccess.Write, FileShare.None))
            stream.SetLength((16L * 1024 * 1024) + 1);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            _ = new ManagedAssetStore(assetRoot));

        Assert.Contains("16 MiB", error.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static async Task<List<T>> ReadAllAsync<T>(IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (T value in source)
            result.Add(value);
        return result;
    }

    private sealed class RepeatingByteStream(long length) : Stream
    {
        private long position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = (int)Math.Min(count, length - position);
            if (read <= 0)
                return 0;
            buffer.AsSpan(offset, read).Fill(0x42);
            position += read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = (int)Math.Min(buffer.Length, length - position);
            if (read <= 0)
                return ValueTask.FromResult(0);
            buffer.Span[..read].Fill(0x42);
            position += read;
            return ValueTask.FromResult(read);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
