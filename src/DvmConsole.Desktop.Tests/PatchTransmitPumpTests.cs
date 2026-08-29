using System.Collections.Concurrent;
using DvmConsole.Core.Runtime;
using DvmConsole.Media;
using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class PatchTransmitPumpTests
{
    [Fact]
    public async Task EmptyPatchSourceDoesNotCreateAnOutboundCall()
    {
        var sent = new ConcurrentQueue<byte[]>();
        using var session = new PatchTransmitSession(
            new ChannelRuntimeDefinition("DMR", "FNE", "dmr", 99, slot: 0),
            sourceId: 890,
            streamId: 101,
            new FakeVocoderSession(),
            (payload, _, _) => sent.Enqueue(payload.ToArray()));
        var pump = new PatchTransmitPump(session);

        pump.Complete();
        Assert.False(await pump.Started);
        await pump.Completion;

        Assert.Empty(sent);
        Assert.Null(pump.Failure);
    }

    [Fact]
    public async Task ReleasesOnePcmFramePerCadenceBeforeBuildingDmrPacket()
    {
        var sent = new ConcurrentQueue<byte[]>();
        var vocoder = new FakeVocoderSession();
        using var session = new PatchTransmitSession(
            new ChannelRuntimeDefinition("DMR", "FNE", "dmr", 99, slot: 0),
            sourceId: 890,
            streamId: 101,
            vocoder,
            (payload, _, _) => sent.Enqueue(payload.ToArray()));
        var time = new ManualTimeProvider();
        var delay = new ManualDelay(time);
        var pump = new PatchTransmitPump(
            session,
            delay: delay.WaitAsync,
            timeProvider: time);

        Assert.True(pump.Enqueue(new short[VocoderFrameSizes.PcmSamplesPerFrame * 3]));
        await WaitUntilAsync(() => delay.WaitCount == 1);
        Assert.Single(sent);
        Assert.Equal(0, vocoder.EncodeCount);

        delay.Release();
        await WaitUntilAsync(() => delay.WaitCount == 2);
        Assert.Single(sent);
        Assert.Equal(1, vocoder.EncodeCount);

        delay.Release();
        await WaitUntilAsync(() => delay.WaitCount == 3);
        Assert.Single(sent);
        Assert.Equal(2, vocoder.EncodeCount);

        delay.Release();
        await WaitUntilAsync(() => sent.Count == 2);
        pump.Complete();
        await pump.Completion;

        Assert.Null(pump.Failure);
    }

    [Fact]
    public async Task BacklogLimitEndsThePatchInsteadOfGrowingWithoutBound()
    {
        using var session = new PatchTransmitSession(
            new ChannelRuntimeDefinition("DMR", "FNE", "dmr", 99, slot: 0),
            sourceId: 890,
            streamId: 101,
            new FakeVocoderSession(),
            (_, _, _) => { });
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pump = new PatchTransmitPump(session, start.Task, capacity: 2);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            pump.Enqueue(new short[VocoderFrameSizes.PcmSamplesPerFrame * 3]));
        start.SetResult();
        await pump.Completion;

        Assert.Contains("safety limit", failure.Message, StringComparison.Ordinal);
        Assert.Same(failure, pump.Failure);
        Assert.Equal(2, pump.CaptureHealth().Capacity);
        Assert.Equal(2, pump.CaptureHealth().PeakDepth);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(5, timeout.Token);
    }

    private sealed class ManualDelay(ManualTimeProvider time)
    {
        private readonly SemaphoreSlim releases = new(0);
        private int waitCount;

        public int WaitCount => Volatile.Read(ref waitCount);

        public async ValueTask WaitAsync(
            TimeSpan duration,
            CancellationToken cancellationToken)
        {
            Assert.True(duration > TimeSpan.Zero);
            Interlocked.Increment(ref waitCount);
            await releases.WaitAsync(cancellationToken);
            time.Advance(duration);
        }

        public void Release() => releases.Release();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public override DateTimeOffset GetUtcNow()
            => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(timestamp);

        public void Advance(TimeSpan duration) => timestamp += duration.Ticks;
    }

    private sealed class FakeVocoderSession : IVocoderSession
    {
        private int encodeCount;

        public int EncodeCount => Volatile.Read(ref encodeCount);

        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword)
        {
            Interlocked.Increment(ref encodeCount);
            codeword.Fill(0x5A);
            return codeword.Length;
        }

        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples) => 0;
        public int FlushEncode(Span<byte> codeword) => 0;
        public void Dispose() { }
    }
}
