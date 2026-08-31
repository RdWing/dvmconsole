using System.Buffers.Binary;
using DvmConsole.Application;
using DvmConsole.Core.Runtime;
using DvmConsole.Operations;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class CallRecordingServiceTests
{
    [Fact]
    public async Task ReceiveEpisodeUsesOneStoreHandleAndCommitsFinalizedWave()
    {
        var store = new FakeRecordingStore();
        var clock = new FixedClock(new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero));
        await using var service = new CallRecordingService(store, clock);
        ChannelRecordingDescriptor channel = CreateChannel(recordingEnabled: true);

        await service.WriteReceiveSamplesAsync(channel, episodeId: 17, sourceId: 101, new short[] { 1, 2 });
        await service.WriteReceiveSamplesAsync(
            channel,
            episodeStreamId: 17,
            physicalStreamId: 18,
            sourceId: 101,
            new short[] { 3, 4 },
            receiveEpisodeId: 17);
        await service.StopReceiveEpisodeAsync(channel.Id, episodeId: 17);

        FakeWriteHandle handle = Assert.Single(store.Handles);
        Assert.True(handle.Committed);
        Assert.Equal(TimeSpan.FromSeconds(4d / 8000d), handle.Duration);
        byte[] wave = handle.Content;
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wave, 0, 4));
        Assert.Equal((uint)8, BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(40, 4)));
        Assert.Equal((short)4, BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(50, 2)));
        Assert.Equal(clock.UtcNow, handle.StartedAt);
        Assert.Equal(channel.Id, handle.ChannelId);
        Assert.NotNull(handle.Context);
        Assert.Equal("RX", handle.Context.Direction);
        Assert.Equal("InboundRadio", handle.Context.RecordingSourceType);
        Assert.Equal(new uint[] { 17, 18 }, handle.Context.StreamIds);
        Assert.Equal((uint)101, handle.Context.SourceId);
        Assert.True(handle.Context.Encryption.IsKnown);
        Assert.False(handle.Context.Encryption.IsSecure);
        Assert.Equal(7, handle.Context.RetentionDays);
        Assert.Equal(17, handle.Context.ReceiveEpisodeId);
    }

    [Fact]
    public async Task NewTransmitStreamFinalizesPreviousCaptureBeforeWritingReplacement()
    {
        var store = new FakeRecordingStore();
        await using var service = new CallRecordingService(store);
        ChannelRecordingDescriptor channel = CreateChannel(recordingEnabled: true);

        await service.WriteTransmitSamplesAsync(channel, streamId: 10, new short[] { 10 });
        await service.WriteTransmitSamplesAsync(channel, streamId: 11, new short[] { 20 });
        await service.StopTransmitAsync(channel.Id);

        Assert.Equal(2, store.Handles.Count);
        Assert.All(store.Handles, handle => Assert.True(handle.Committed));
        Assert.Equal((short)10, BinaryPrimitives.ReadInt16LittleEndian(store.Handles[0].Content.AsSpan(44, 2)));
        Assert.Equal((short)20, BinaryPrimitives.ReadInt16LittleEndian(store.Handles[1].Content.AsSpan(44, 2)));
    }

    [Fact]
    public async Task DisabledRecordingDoesNotOpenStorage()
    {
        var store = new FakeRecordingStore();
        await using var service = new CallRecordingService(store);
        ChannelRecordingDescriptor channel = CreateChannel(recordingEnabled: false);

        await service.WriteReceiveSamplesAsync(channel, episodeId: 1, sourceId: 2, new short[] { 1 });
        await service.WriteTransmitSamplesAsync(channel, streamId: 3, new short[] { 1 });

        Assert.Empty(store.Handles);
    }

    [Fact]
    public async Task DisposalFinalizesEveryActiveDirection()
    {
        var store = new FakeRecordingStore();
        var service = new CallRecordingService(store);
        ChannelRecordingDescriptor channel = CreateChannel(recordingEnabled: true);

        await service.WriteReceiveSamplesAsync(channel, episodeId: 1, sourceId: 2, new short[] { 1 });
        await service.WriteTransmitSamplesAsync(channel, streamId: 3, new short[] { 2 });
        await service.DisposeAsync();

        Assert.Equal(2, store.Handles.Count);
        Assert.All(store.Handles, handle => Assert.True(handle.Committed));
    }

    private static ChannelRecordingDescriptor CreateChannel(bool recordingEnabled)
        => new(
            new ChannelId(new ChannelSessionId(
                "System",
                ChannelProtocol.Analog,
                100,
                0,
                "Dispatch")),
            new ChannelRuntimeDefinition(
                "Dispatch",
                "System",
                "analog",
                destinationId: 100,
                slot: 1,
                rxOnly: false),
            recordingEnabled,
            TransmitEncrypted: false);

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class FakeRecordingStore : IRecordingStore
    {
        public List<FakeWriteHandle> Handles { get; } = [];

        public ValueTask<IRecordingWriteHandle> CreateAsync(
            CallId callId,
            ChannelId channelId,
            DateTimeOffset startedAt,
            string mediaType,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var handle = new FakeWriteHandle(callId, channelId, startedAt, mediaType);
            Handles.Add(handle);
            return ValueTask.FromResult<IRecordingWriteHandle>(handle);
        }

        public ValueTask<Stream> OpenReadAsync(
            RecordingId id,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<RecordingDescriptor> ListAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FakeWriteHandle(
        CallId callId,
        ChannelId channelId,
        DateTimeOffset startedAt,
        string mediaType) : IRecordingWriteHandle
    {
        private readonly MemoryStream stream = new();
        public RecordingId Id { get; } = RecordingId.New();
        public CallId CallId { get; } = callId;
        public ChannelId ChannelId { get; } = channelId;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public string MediaType { get; } = mediaType;
        public Stream Stream => stream;
        public bool Committed { get; private set; }
        public TimeSpan Duration { get; private set; }
        public byte[] Content => stream.ToArray();
        public RecordingCaptureContext? Context { get; private set; }

        public void UpdateContext(RecordingCaptureContext context)
            => Context = context;

        public ValueTask CommitAsync(
            TimeSpan duration,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Committed = true;
            Duration = duration;
            return ValueTask.CompletedTask;
        }

        public ValueTask AbortAsync(
            string? fault,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
