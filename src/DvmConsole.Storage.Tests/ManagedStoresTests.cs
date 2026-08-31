using System.Text;
using DvmConsole.Application;
using DvmConsole.Operations;
using Xunit;

namespace DvmConsole.Storage.Tests;

public sealed class ManagedStoresTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dvmconsole-stores-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AssetImportOwnsACopyAndReturnsStreams()
    {
        var store = new ManagedAssetStore(Path.Combine(root, "assets"));
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes("alert"));
        AssetDescriptor imported = await store.ImportAsync("Alert", "audio/wav", source);
        source.SetLength(0);

        await using Stream read = await store.OpenReadAsync(imported.Id);
        using var reader = new StreamReader(read);
        Assert.Equal("alert", await reader.ReadToEndAsync());
        Assert.Single(await ReadAllAsync(store.ListAsync()));
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
}
