// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using DvmConsole.Application;
using DvmConsole.Core.Runtime;
using DvmConsole.Media;
using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class PatchTransmitPumpTests
{
    [Theory]
    [InlineData("dmr")]
    [InlineData("p25")]
    [InlineData("nxdn")]
    public async Task WaitingCallExpiresWithoutKeyingOrLeakingItsVocoder(string mode)
    {
        var time = new PatchTestTimeProvider();
        var predecessor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new ConcurrentQueue<byte[]>();
        var vocoder = new FakeVocoderSession();
        var session = new PatchTransmitSession(new ChannelRuntimeDefinition("Target", "FNE", mode, 99, slot: 0),
            890, 101, vocoder, (payload, _, _) => sent.Enqueue(payload.ToArray()));
        var pump = new PatchTransmitPump(session, predecessor.Task, timeProvider: time,
            maximumQueuedAge: PatchForwardingCoordinator.MaximumQueuedAudioAge);
        pump.Enqueue(new short[160 * 9]);
        pump.Complete();
        time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(pump.Completion.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));
        await pump.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.IsType<PatchBacklogException>(pump.Failure);
        Assert.False(await pump.Started);
        Assert.Empty(sent);
        Assert.Equal(0, vocoder.EncodeCount);
        Assert.Equal(1, vocoder.DisposeCount);
        Assert.Equal(0, pump.CaptureHealth().Depth);
        predecessor.SetResult();
        pump.DiscardPendingAudioAndComplete();
    }

    [Theory]
    [InlineData("dmr")]
    [InlineData("p25")]
    [InlineData("nxdn")]
    public async Task ActiveCallExpiresPendingAudioAndStillEmitsItsProtocolTerminator(string mode)
    {
        var time = new PatchTestTimeProvider();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new ConcurrentQueue<byte[]>();
        var vocoder = new FakeVocoderSession();
        var session = new PatchTransmitSession(new ChannelRuntimeDefinition("Target", "FNE", mode, 99, slot: 0),
            890, 101, vocoder, (payload, _, _) => sent.Enqueue(payload.ToArray()));
        var pump = new PatchTransmitPump(session, timeProvider: time,
            delay: async (_, token) =>
            {
                waiting.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }, maximumQueuedAge: PatchForwardingCoordinator.MaximumQueuedAudioAge);
        pump.Enqueue(new short[160 * 9]);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(await pump.Started);
        time.Advance(TimeSpan.FromSeconds(1));
        await pump.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsType<PatchBacklogException>(pump.Failure);
        Assert.Equal(0, vocoder.EncodeCount);
        Assert.Equal(1, vocoder.DisposeCount);
        Assert.True(session.IsEnded);
        Assert.Single(sent, payload => mode switch
        {
            "dmr" => (payload[15] & 0x2f) == 0x22,
            "p25" => payload[22] == P25DfsiFrameCodec.TduDuid && (payload[14] & 0x80) == 0,
            _ => payload[4] == NxdnVoicePacketCodec.TransmitReleaseMessageType
        });
        Assert.Equal(0, pump.CaptureHealth().Depth);
    }

    [Theory]
    [InlineData("dmr", false)]
    [InlineData("dmr", true)]
    [InlineData("p25", false)]
    [InlineData("p25", true)]
    [InlineData("nxdn", false)]
    [InlineData("nxdn", true)]
    public async Task FinalShutdownCancelsQueuedCallWithoutWaitingForItsPredecessor(
        string mode, bool alreadyCompleting)
    {
        var sent = new ConcurrentQueue<byte[]>();
        var vocoder = new FakeVocoderSession();
        var session = new PatchTransmitSession(
            new ChannelRuntimeDefinition("Target", "FNE", mode, 99, slot: 0),
            890, 101, vocoder, (payload, _, _) => sent.Enqueue(payload.ToArray()));
        var predecessor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pump = new PatchTransmitPump(session, predecessor.Task);
        Assert.True(pump.Enqueue(new short[160 * 20]));
        if (alreadyCompleting)
            pump.Complete();

        pump.DiscardPendingAudioAndComplete();
        await pump.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        pump.DiscardPendingAudioAndComplete();
        Assert.False(await pump.Started);
        Assert.False(predecessor.Task.IsCompleted);
        Assert.Empty(sent);
        Assert.Equal(0, vocoder.EncodeCount);
        Assert.Equal(1, vocoder.DisposeCount);
        Assert.Equal(0, pump.CaptureHealth().Depth);
        Assert.Null(pump.Failure);
        Assert.False(pump.Enqueue(new short[160]));
    }

    [Fact]
    public async Task OverloadDoesNotHideATerminatorSendFailure()
    {
        var time = new PatchTestTimeProvider();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vocoder = new FakeVocoderSession();
        var session = new PatchTransmitSession(new ChannelRuntimeDefinition("Target", "FNE", "dmr", 99, slot: 0),
            890, 101, vocoder, (payload, _, _) =>
            {
                if ((payload.Span[15] & 0x2f) == 0x22)
                    throw new IOException("Terminator send failed.");
            });
        var pump = new PatchTransmitPump(session, timeProvider: time,
            delay: async (_, token) =>
            {
                waiting.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }, maximumQueuedAge: PatchForwardingCoordinator.MaximumQueuedAudioAge);
        pump.Enqueue(new short[160 * 9]);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(1));
        await pump.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(pump.WasOverloaded);
        AggregateException failure = Assert.IsType<AggregateException>(pump.Failure);
        Assert.Contains(failure.Flatten().InnerExceptions, error => error is IOException);
        Assert.Contains(failure.Flatten().InnerExceptions, error => error is PatchBacklogException);
        Assert.Equal(1, vocoder.DisposeCount);
    }

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

        PatchBacklogException failure = Assert.Throws<PatchBacklogException>(() =>
            pump.Enqueue(new short[VocoderFrameSizes.PcmSamplesPerFrame * 3]));
        start.SetResult();
        await pump.Completion;

        Assert.Contains("safety limit", failure.Message, StringComparison.Ordinal);
        Assert.Same(failure, pump.Failure);
        Assert.True(pump.WasOverloaded);
        Assert.Equal(2, pump.CaptureHealth().Capacity);
        Assert.Equal(2, pump.CaptureHealth().PeakDepth);
    }

    [Fact]
    public async Task WarmProducerReusesPcmBuffersAcrossTenThousandFrames()
    {
        const int warmupFrames = 200;
        const int measuredFrames = 10_000;
        var vocoder = new FakeVocoderSession();
        var time = new ManualTimeProvider();
        using var session = new PatchTransmitSession(
            new ChannelRuntimeDefinition("DMR", "FNE", "dmr", 99, slot: 0),
            sourceId: 890,
            streamId: 101,
            vocoder,
            (_, _, _) => { },
            timeProvider: time);
        var pump = new PatchTransmitPump(
            session,
            delay: (duration, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                time.Advance(duration);
                return ValueTask.CompletedTask;
            },
            timeProvider: time,
            capacity: 64);
        var frame = new short[VocoderFrameSizes.PcmSamplesPerFrame];

        for (int index = 1; index <= warmupFrames; index++)
        {
            Assert.True(pump.Enqueue(frame));
            WaitForEncode(vocoder, index);
        }

        bool allAccepted = true;
        long allocated = 0;
        for (int index = 1; index <= measuredFrames; index++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            allAccepted &= pump.Enqueue(frame);
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            WaitForEncode(vocoder, warmupFrames + index);
        }

        pump.Complete();
        await pump.Completion;

        Assert.Null(pump.Failure);
        Assert.True(allAccepted);
        Assert.True(
            allocated <= 1_024,
            $"Expected the warm producer path to reuse PCM frames; observed " +
            $"{allocated / (double)measuredFrames:F1} bytes per frame.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(5, timeout.Token);
    }

    private static void WaitForEncode(FakeVocoderSession vocoder, int expected)
    {
        long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        var spinner = new SpinWait();
        while (vocoder.EncodeCount < expected)
        {
            if (Stopwatch.GetTimestamp() >= deadline)
                throw new TimeoutException("Patch transmit worker did not process the frame.");
            spinner.SpinOnce();
        }
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
        public override long GetTimestamp() => Volatile.Read(ref timestamp);
        public override DateTimeOffset GetUtcNow()
            => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(timestamp);

        public void Advance(TimeSpan duration) => Interlocked.Add(ref timestamp, duration.Ticks);

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
            => new AdvancingTimer(this, callback, state, dueTime, period);

        private sealed class AdvancingTimer : ITimer
        {
            private readonly ManualTimeProvider timeProvider;
            private readonly TimerCallback callback;
            private readonly object? state;
            private int disposed;

            public AdvancingTimer(
                ManualTimeProvider timeProvider,
                TimerCallback callback,
                object? state,
                TimeSpan dueTime,
                TimeSpan period)
            {
                this.timeProvider = timeProvider;
                this.callback = callback;
                this.state = state;
                _ = Change(dueTime, period);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (Volatile.Read(ref disposed) != 0)
                    return false;
                if (dueTime == Timeout.InfiniteTimeSpan)
                    return true;

                ThreadPool.QueueUserWorkItem(_ =>
                {
                    if (Volatile.Read(ref disposed) != 0)
                        return;
                    timeProvider.Advance(dueTime);
                    callback(state);
                });
                return true;
            }

            public void Dispose() => Interlocked.Exchange(ref disposed, 1);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeVocoderSession : IVocoderSession
    {
        private int encodeCount;
        private int disposeCount;

        public int EncodeCount => Volatile.Read(ref encodeCount);
        public int DisposeCount => Volatile.Read(ref disposeCount);

        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword)
        {
            Interlocked.Increment(ref encodeCount);
            codeword.Fill(0x5A);
            return codeword.Length;
        }

        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples) => 0;
        public int FlushEncode(Span<byte> codeword) => 0;
        public void Dispose() => Interlocked.Increment(ref disposeCount);
    }
}
