using DvmConsole.Core.Configuration;
using DvmConsole.Desktop;
using DvmConsole.FneClient;
using System.Diagnostics;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ChannelReceiveWorkQueueTests
{
    [Fact]
    public async Task AStalledChannelDoesNotDelayAnotherChannel()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = CreateChannel("First", "100");
        var second = CreateChannel("Second", "101");
        await using var queue = new ChannelReceiveWorkQueue(async (channel, _) =>
        {
            if (ReferenceEquals(channel, first))
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
            }
            else
            {
                secondProcessed.TrySetResult();
            }
        });

        queue.Enqueue(first, CreateTraffic(1));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        queue.Enqueue(second, CreateTraffic(1));

        await secondProcessed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseFirst.TrySetResult();
    }

    [Fact]
    public async Task BoundsPendingVoiceButRetainsTerminator()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processed = new List<ushort>();
        var channel = CreateChannel("Dispatch", "100");
        await using var queue = new ChannelReceiveWorkQueue(async (_, traffic) =>
        {
            lock (processed)
                processed.Add(traffic.PacketSequence);
            if (traffic.PacketSequence == 1)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
            }
        }, maxPendingFramesPerChannel: 2);

        queue.Enqueue(channel, CreateTraffic(1));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(queue.Enqueue(channel, CreateTraffic(2), out bool droppedSecond));
        Assert.False(droppedSecond);
        Assert.True(queue.Enqueue(channel, CreateTraffic(3), out bool droppedThird));
        Assert.False(droppedThird);
        Assert.True(queue.Enqueue(channel, CreateTraffic(4), out bool droppedFourth));
        Assert.True(droppedFourth);
        Assert.True(queue.Enqueue(channel, CreateTraffic(5, terminator: true), out bool droppedFifth));
        Assert.True(droppedFifth);
        releaseFirst.TrySetResult();
        await queue.StopAsync(channel);

        Assert.Equal(3, processed.Count);
        Assert.Equal((ushort)1, processed[0]);
        Assert.Contains((ushort)5, processed);
    }

    [Fact]
    public async Task TerminatorRetainsAQueuedVoiceFrameForItsShortStream()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processed = new List<(ushort Sequence, uint StreamId)>();
        var channel = CreateChannel("Dispatch", "100");
        await using var queue = new ChannelReceiveWorkQueue(async (_, traffic) =>
        {
            lock (processed)
                processed.Add((traffic.PacketSequence, traffic.StreamId));
            if (traffic.PacketSequence == 1)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
            }
        }, maxPendingFramesPerChannel: 2);

        queue.Enqueue(channel, CreateTraffic(1, streamId: 999));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        queue.Enqueue(channel, CreateTraffic(2, streamId: 100));
        queue.Enqueue(channel, CreateTraffic(3, streamId: 200));
        queue.Enqueue(channel, CreateTraffic(4, terminator: true, streamId: 100));
        releaseFirst.TrySetResult();
        await queue.StopAsync(channel);

        Assert.Contains(processed, item => item.Sequence == 2 && item.StreamId == 100);
        Assert.Contains(processed, item => item.Sequence == 4 && item.StreamId == 100);
        Assert.DoesNotContain(processed, item => item.Sequence == 3 && item.StreamId == 200);
    }

    [Fact]
    public async Task StreamContinuationRunsAfterBufferedJitterPackets()
    {
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<string>();
        var channel = CreateChannel("Dispatch", "100");
        await using var queue = new ChannelReceiveWorkQueue(async (_, traffic) =>
        {
            lock (events)
                events.Add($"packet {traffic.PacketSequence}");
            if (traffic.PacketSequence == 1)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
            }
        });

        queue.Enqueue(channel, CreateTraffic(1));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        queue.Enqueue(channel, CreateTraffic(2));
        Task completion = queue.RunAfterStreamAsync(channel, 99, () =>
        {
            lock (events)
                events.Add("complete");
            return Task.CompletedTask;
        });

        Assert.False(completion.IsCompleted);
        releaseFirst.TrySetResult();
        await completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["packet 1", "packet 2", "complete"], events);
    }

    [Fact]
    public async Task StreamContinuationDoesNotWaitForAnotherStreamsJitterDeadline()
    {
        var events = new List<string>();
        var channel = CreateChannel("Dispatch", "100");
        await using var queue = new ChannelReceiveWorkQueue(
            (_, traffic) =>
            {
                lock (events)
                    events.Add($"packet {traffic.StreamId}");
                return Task.CompletedTask;
            },
            getJitterBufferProfile: (_, _) => new ReceiveJitterBufferProfile(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1)));

        queue.Enqueue(channel, CreateTraffic(1, streamId: 200));
        Task completion = queue.RunAfterStreamAsync(channel, 100, () =>
        {
            lock (events)
                events.Add("complete 100");
            return Task.CompletedTask;
        });

        await completion.WaitAsync(TimeSpan.FromMilliseconds(500));

        Assert.Equal(["complete 100"], events);
    }

    [Fact]
    public async Task MultiStreamContinuationWaitsForEveryEpisodeStream()
    {
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<string>();
        var channel = CreateChannel("Dispatch", "100");
        await using var queue = new ChannelReceiveWorkQueue(async (_, traffic) =>
        {
            lock (events)
                events.Add($"packet {traffic.StreamId}");
            if (traffic.StreamId == 100)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
            }
        });

        queue.Enqueue(channel, CreateTraffic(1, streamId: 100));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        queue.Enqueue(channel, CreateTraffic(2, streamId: 200));
        Task completion = queue.RunAfterStreamsAsync(channel, [100, 200], () =>
        {
            lock (events)
                events.Add("complete");
            return Task.CompletedTask;
        });

        Assert.False(completion.IsCompleted);
        releaseFirst.TrySetResult();
        await completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["packet 100", "packet 200", "complete"], events);
    }

    [Fact]
    public async Task EpisodeCompletionKeepsPlaybackAndRecordingBehindQueuedStreams()
    {
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<string>();
        var channel = CreateChannel("Dispatch", "100");
        await using var queue = new ChannelReceiveWorkQueue(async (_, traffic) =>
        {
            lock (events)
                events.Add($"packet {traffic.StreamId}");
            if (traffic.StreamId == 100)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
            }
        });
        var coordinator = new ReceiveEpisodeCompletionCoordinator(
            queue,
            (_, episodeId) =>
            {
                lock (events)
                    events.Add($"playback {episodeId}");
                return Task.CompletedTask;
            },
            (_, streamId) =>
            {
                lock (events)
                    events.Add($"recording {streamId}");
            },
            candidate => candidate);
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        var episode = new ReceiveCallEpisodeSnapshot(
            900,
            "Test",
            FneTrafficProtocol.P25,
            42,
            100,
            null,
            "Group",
            100,
            [100, 200],
            now,
            now,
            now,
            null);

        queue.Enqueue(channel, CreateTraffic(1, streamId: 100));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        queue.Enqueue(channel, CreateTraffic(2, streamId: 200));
        Task completion = coordinator.CompleteAsync(episode, [channel]);

        Assert.False(completion.IsCompleted);
        releaseFirst.TrySetResult();
        await completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(
            ["packet 100", "packet 200", "playback 900", "recording 100"],
            events);
    }

    [Fact]
    public async Task MeasuresIngressQueueAndProcessingLatency()
    {
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource<ReceiveWorkItemTiming>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = CreateChannel("Dispatch", "100");
        await using var queue = new ChannelReceiveWorkQueue(
            async (_, _) =>
            {
                await Task.Delay(15);
                processed.TrySetResult();
            },
            timingObserver: (_, timing) => observed.TrySetResult(timing));
        long ingressTimestamp = Stopwatch.GetTimestamp() - (Stopwatch.Frequency / 20);

        Assert.True(queue.Enqueue(
            channel,
            CreateTraffic(1),
            ingressTimestamp,
            out bool dropped));
        Assert.False(dropped);
        await processed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        ReceiveWorkItemTiming timing = await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        ReceiveWorkQueueDiagnostics diagnostics = queue.GetDiagnostics(channel);

        Assert.True(timing.IngressToQueueDelay >= TimeSpan.FromMilliseconds(40));
        Assert.True(timing.ProcessingDuration >= TimeSpan.FromMilliseconds(10));
        Assert.True(timing.EndToEndDelay >= timing.IngressToQueueDelay);
        Assert.Equal(1, diagnostics.ProcessedFrames);
        Assert.Equal(timing.EndToEndDelay, diagnostics.MaximumEndToEndDelay);
    }

    [Fact]
    public async Task DecomposesJitterWorkerGateAndSessionTimingWithoutPerFrameState()
    {
        var scheduler = new ManualReceiveWorkQueueScheduler();
        var observed = new TaskCompletionSource<ReceiveWorkItemTiming>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = CreateChannel("Dispatch", "100");
        await using ChannelReceiveWorkQueue queue = ChannelReceiveWorkQueue.CreateWithTiming(
            (_, _) => Task.FromResult(new ReceiveProcessingStageTiming(
                SessionGateDelay: TimeSpan.FromMilliseconds(2),
                SessionProcessingDuration: TimeSpan.FromMilliseconds(3),
                EncryptedSessionProcessing: true)),
            timingObserver: (_, timing) => observed.TrySetResult(timing),
            getJitterBufferProfile: (_, _) => new ReceiveJitterBufferProfile(
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(40)),
            scheduler: scheduler);

        Assert.True(queue.Enqueue(
            channel,
            CreateTraffic(1, protocol: FneTrafficProtocol.P25),
            out bool dropped));
        Assert.False(dropped);
        ReceiveWorkItemTiming timing = await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        ReceiveWorkQueueDiagnostics diagnostics = queue.GetDiagnostics(channel);

        Assert.True(timing.HasQueueDelayBreakdown);
        Assert.InRange(
            timing.JitterBufferHoldDuration,
            TimeSpan.FromMilliseconds(39),
            TimeSpan.FromMilliseconds(41));
        Assert.Equal(TimeSpan.Zero, timing.WorkerBacklogDuration);
        Assert.True(timing.HasSessionProcessingBreakdown);
        Assert.Equal(TimeSpan.FromMilliseconds(2), timing.SessionGateDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(3), timing.SessionProcessingDuration);
        Assert.Equal(true, timing.EncryptedSessionProcessing);
        Assert.Equal(timing.JitterBufferHoldDuration, diagnostics.MaximumJitterBufferHoldDuration);
        Assert.Equal(timing.WorkerBacklogDuration, diagnostics.MaximumWorkerBacklogDuration);
        Assert.Equal(timing.SessionGateDelay, diagnostics.MaximumSessionGateDelay);
        Assert.Equal(timing.SessionProcessingDuration, diagnostics.MaximumSessionProcessingDuration);
    }

    [Fact]
    public async Task MeasuresFneInterArrivalDelayPerStream()
    {
        var channel = CreateChannel("Dispatch", "100");
        await using var queue = new ChannelReceiveWorkQueue((_, _) => Task.CompletedTask);
        long secondIngress = Stopwatch.GetTimestamp();
        long firstIngress = secondIngress - (Stopwatch.Frequency / 2);

        queue.Enqueue(channel, CreateTraffic(1), firstIngress, out _);
        queue.Enqueue(channel, CreateTraffic(2), secondIngress, out _);
        await queue.StopAsync(channel);

        Assert.True(
            queue.GetDiagnostics(channel).MaximumInterArrivalDelay >=
            TimeSpan.FromMilliseconds(450));
    }

    [Fact]
    public async Task AttributesTransportAndFneBoundaryDelaySeparately()
    {
        var observed = new TaskCompletionSource<ReceiveWorkItemTiming>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = CreateChannel("Dispatch", "100");
        await using var queue = new ChannelReceiveWorkQueue(
            (_, _) => Task.CompletedTask,
            timingObserver: (_, timing) => observed.TrySetResult(timing));
        long boundaryTimestamp = Stopwatch.GetTimestamp();
        long transportTimestamp = boundaryTimestamp - (Stopwatch.Frequency / 20);
        var traffic = new FneTrafficFrame(
            FneTrafficProtocol.Dmr,
            peerId: 1,
            sourceId: 2,
            destinationId: 100,
            slot: 1,
            callType: "GROUP",
            frameType: "VOICE",
            subtype: "VOICE",
            packetSequence: 1,
            streamId: 99,
            payload: [],
            fneBoundaryTimestamp: boundaryTimestamp,
            transportIngressTimestamp: transportTimestamp);

        Assert.True(queue.Enqueue(channel, traffic, boundaryTimestamp, out _));
        ReceiveWorkItemTiming timing = await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.InRange(
            timing.TransportToFneBoundaryDelay,
            TimeSpan.FromMilliseconds(45),
            TimeSpan.FromMilliseconds(55));
        Assert.Equal(
            timing.TransportToFneBoundaryDelay,
            queue.GetDiagnostics(channel).MaximumTransportToFneBoundaryDelay);
    }

    [Fact]
    public async Task ReordersPacketsThatArriveBeforeTheirPlayoutDeadline()
    {
        var processed = new List<ushort>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = CreateChannel("Dispatch", "100");
        await using var queue = new ChannelReceiveWorkQueue(
            (_, traffic) =>
            {
                lock (processed)
                {
                    processed.Add(traffic.PacketSequence);
                    if (processed.Count == 3)
                        completed.TrySetResult();
                }
                return Task.CompletedTask;
            },
            getJitterBufferProfile: (_, _) => new ReceiveJitterBufferProfile(
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(60)));

        queue.Enqueue(channel, CreateTraffic(10));
        queue.Enqueue(channel, CreateTraffic(12));
        queue.Enqueue(channel, CreateTraffic(11));

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await queue.StopAsync(channel);
        Assert.Equal([(ushort)10, (ushort)11, (ushort)12], processed);
        Assert.Equal(1, queue.GetDiagnostics(channel).JitterBufferReorderedPackets);
        Assert.Equal(1, queue.GetDiagnostics(channel, streamId: 99).JitterBufferReorderedPackets);
    }

    [Fact]
    public async Task ReleasesFuturePacketWhenMissingPacketMissesDeadline()
    {
        var processed = new List<ushort>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = CreateChannel("Dispatch", "100");
        await using var queue = new ChannelReceiveWorkQueue(
            (_, traffic) =>
            {
                lock (processed)
                {
                    processed.Add(traffic.PacketSequence);
                    if (processed.Count == 2)
                        completed.TrySetResult();
                }
                return Task.CompletedTask;
            },
            getJitterBufferProfile: (_, _) => new ReceiveJitterBufferProfile(
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(40)));

        queue.Enqueue(channel, CreateTraffic(20));
        queue.Enqueue(channel, CreateTraffic(22));

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await queue.StopAsync(channel);
        Assert.Equal([(ushort)20, (ushort)22], processed);
        Assert.Equal(1, queue.GetDiagnostics(channel).JitterBufferDeadlineMissedPackets);
    }

    [Fact]
    public async Task OneHundredThousandReadyFramesDoNotTurnIntoStaleDelayedFrameWakeups()
    {
        const int readyFrameCount = 100_000;
        const int batchSize = 256;
        var scheduler = new ManualReceiveWorkQueueScheduler();
        using var processedSignal = new SemaphoreSlim(0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var delayedFrameObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = CreateChannel("Dispatch", "100");
        int readyFramesProcessed = 0;
        int droppedReadyFrames = 0;
        await using var queue = new ChannelReceiveWorkQueue(
            (_, traffic) =>
            {
                if (traffic.Protocol == FneTrafficProtocol.P25)
                    return Task.CompletedTask;

                Interlocked.Increment(ref readyFramesProcessed);
                processedSignal.Release();
                return Task.CompletedTask;
            },
            maxPendingFramesPerChannel: batchSize * 2,
            timingObserver: (_, timing) =>
            {
                if (timing.Traffic.Protocol == FneTrafficProtocol.P25)
                    delayedFrameObserved.TrySetResult();
            },
            getJitterBufferProfile: (_, protocol) =>
                protocol == FneTrafficProtocol.P25
                    ? new ReceiveJitterBufferProfile(
                        TimeSpan.FromMilliseconds(20),
                        TimeSpan.FromMilliseconds(20))
                    : default,
            scheduler: scheduler);

        Task? futureFrameEnqueued = null;
        for (int batchStart = 0; batchStart < readyFrameCount; batchStart += batchSize)
        {
            int count = Math.Min(batchSize, readyFrameCount - batchStart);
            for (int offset = 0; offset < count; offset++)
            {
                int index = batchStart + offset;
                ushort sequence = (ushort)(index % ushort.MaxValue);
                Assert.True(queue.Enqueue(
                    channel,
                    CreateTraffic(sequence),
                    out bool droppedFrame));
                if (droppedFrame)
                    droppedReadyFrames++;
            }

            if (batchStart + count == readyFrameCount)
            {
                futureFrameEnqueued = queue.RunAfterStreamAsync(
                    channel,
                    streamId: 99,
                    () =>
                    {
                        Assert.True(queue.Enqueue(
                            channel,
                            CreateTraffic(
                                sequence: 1,
                                streamId: 100,
                                protocol: FneTrafficProtocol.P25)));
                        return Task.CompletedTask;
                    });
            }

            for (int offset = 0; offset < count; offset++)
                await processedSignal.WaitAsync(timeout.Token);
        }

        Assert.NotNull(futureFrameEnqueued);
        await futureFrameEnqueued.WaitAsync(timeout.Token);
        await delayedFrameObserved.Task.WaitAsync(timeout.Token);
        ReceiveWorkQueueDiagnostics diagnostics = queue.GetDiagnostics(channel);

        Assert.Equal(0, droppedReadyFrames);
        Assert.Equal(readyFrameCount, Volatile.Read(ref readyFramesProcessed));
        Assert.Equal(readyFrameCount + 1, diagnostics.ProcessedFrames);
        Assert.True(diagnostics.CoalescedWakeSignals > 0);
        Assert.Equal(1, diagnostics.WakeWaits);
        Assert.Equal(1, diagnostics.WakeTimeouts);
        Assert.InRange(diagnostics.PeakPendingFrames, 1, batchSize);
        Assert.InRange(
            scheduler.TimedOutDuration,
            TimeSpan.FromMilliseconds(19),
            TimeSpan.FromMilliseconds(21));
    }

    [Fact]
    public async Task RemovesPerStreamTimingAfterItsTerminatorIsReported()
    {
        var terminatorReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = CreateChannel("Dispatch", "100");
        await using var queue = new ChannelReceiveWorkQueue(
            (_, _) => Task.CompletedTask,
            timingObserver: (_, timing) =>
            {
                if (ReceiveTrafficClassifier.IsTerminator(timing.Traffic))
                    terminatorReported.TrySetResult();
            });

        queue.Enqueue(channel, CreateTraffic(1, streamId: 42));
        queue.Enqueue(channel, CreateTraffic(2, terminator: true, streamId: 42));
        Task streamDrained = queue.RunAfterStreamAsync(
            channel,
            streamId: 42,
            () => Task.CompletedTask);

        await terminatorReported.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await streamDrained.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(default, queue.GetDiagnostics(channel, streamId: 42));
    }

    [Fact]
    public async Task HealthSnapshotReportsCurrentAndPeakPendingPressure()
    {
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = CreateChannel("Dispatch", "100");
        await using var queue = new ChannelReceiveWorkQueue(async (_, traffic) =>
        {
            if (traffic.PacketSequence != 1)
                return;
            firstStarted.TrySetResult();
            await releaseFirst.Task;
        });

        queue.Enqueue(channel, CreateTraffic(1));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        queue.Enqueue(channel, CreateTraffic(2));

        DvmConsole.Operations.ReceiveQueueHealth active = queue.CaptureHealth();
        Assert.Equal(1, active.CurrentDepth);
        Assert.True(active.PeakDepth >= 1);

        releaseFirst.TrySetResult();
        await queue.StopAsync(channel);
        DvmConsole.Operations.ReceiveQueueHealth drained = queue.CaptureHealth();
        Assert.Equal(0, drained.CurrentDepth);
        Assert.True(drained.PeakDepth >= 1);
    }

    private static ChannelViewModel CreateChannel(string name, string tgid)
        => new(new ChannelConfiguration
        {
            Name = name,
            System = "System 1",
            Tgid = tgid,
            Mode = "dmr",
            Slot = 1
        });

    private static FneTrafficFrame CreateTraffic(
        ushort sequence,
        bool terminator = false,
        uint streamId = 99,
        FneTrafficProtocol protocol = FneTrafficProtocol.Dmr)
        => new(
            protocol,
            peerId: 1,
            sourceId: 2,
            destinationId: 100,
            slot: 1,
            callType: "GROUP",
            frameType: terminator ? "TERMINATOR" : "VOICE",
            subtype: terminator ? "TERMINATOR_WITH_LC" : "VOICE",
            packetSequence: sequence,
            streamId: streamId,
            payload: []);

    private sealed class ManualReceiveWorkQueueScheduler : IReceiveWorkQueueScheduler
    {
        private long timestamp = Stopwatch.Frequency;
        private long timedOutTicks;

        public TimeSpan TimedOutDuration
            => Stopwatch.GetElapsedTime(0, Interlocked.Read(ref timedOutTicks));

        public long GetTimestamp()
            => Interlocked.Read(ref timestamp);

        public ValueTask<bool> WaitAsync(
            CoalescingWakeSignal signal,
            TimeSpan timeout)
        {
            if (signal.TryConsume())
                return ValueTask.FromResult(true);
            if (timeout == Timeout.InfiniteTimeSpan)
                throw new InvalidOperationException("The deterministic queue unexpectedly waited indefinitely.");

            long elapsedTicks = Math.Max(
                1,
                (long)Math.Ceiling(timeout.TotalSeconds * Stopwatch.Frequency));
            Interlocked.Add(ref timestamp, elapsedTicks);
            Interlocked.Add(ref timedOutTicks, elapsedTicks);
            return ValueTask.FromResult(false);
        }
    }
}
