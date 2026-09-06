// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Configuration;
using DvmConsole.Core.Runtime;
using DvmConsole.Application;
using DvmConsole.Desktop;
using DvmConsole.FneClient;
using System.Diagnostics;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ChannelReceiveWorkQueueTests
{
    [Fact]
    public async Task IdleBurstsReuseOneWorkerUntilTheChannelStops()
    {
        var firstProcessed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondProcessed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = CreateChannel("Dispatch", "100");
        int processed = 0;
        await using var queue = new ChannelReceiveWorkQueue((_, _) =>
        {
            if (Interlocked.Increment(ref processed) == 1)
                firstProcessed.TrySetResult();
            else
                secondProcessed.TrySetResult();
            return Task.CompletedTask;
        });

        queue.Enqueue(channel, CreateTraffic(1));
        await firstProcessed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(20);
        queue.Enqueue(channel, CreateTraffic(2));
        await secondProcessed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, queue.GetDiagnostics(channel).WorkerStarts);
        await queue.StopAsync(channel);
    }

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
            if (channel == new ChannelId(first.SessionId))
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
        var channelId = new ChannelId(channel.SessionId);
        var port = new TestReceiveEpisodeCompletionPort(
            (id, streamIds, continuation) =>
            {
                Assert.Equal(channelId, id);
                return queue.RunAfterStreamsAsync(channel, streamIds, continuation);
            },
            (id, episodeId) =>
            {
                Assert.Equal(channelId, id);
                lock (events)
                    events.Add($"playback {episodeId}");
                return Task.CompletedTask;
            },
            (id, episodeId) =>
            {
                Assert.Equal(channelId, id);
                lock (events)
                    events.Add($"recording {episodeId}");
            });
        var coordinator = new ReceiveEpisodeCompletionCoordinator(port);
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
            EncryptionSnapshot.Unknown);

        queue.Enqueue(channel, CreateTraffic(1, streamId: 100));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        queue.Enqueue(channel, CreateTraffic(2, streamId: 200));
        Task completion = coordinator.CompleteAsync(
            new ReceiveEpisodeCompletion(
                episode.EpisodeId,
                episode.PrimaryStreamId,
                episode.StreamIds),
            [channelId]);

        Assert.False(completion.IsCompleted);
        releaseFirst.TrySetResult();
        await completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(
            ["packet 100", "packet 200", "playback 900", "recording 900"],
            events);
    }

    [Fact]
    public async Task MeasuresIngressQueueAndProcessingLatency()
    {
        var scheduler = new ManualReceiveWorkQueueScheduler();
        var observed = new TaskCompletionSource<ReceiveWorkItemTiming>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = CreateChannel("Dispatch", "100");
        await using var queue = new ChannelReceiveWorkQueue(
            (_, _) =>
            {
                scheduler.Advance(TimeSpan.FromMilliseconds(15));
                return Task.CompletedTask;
            },
            timingObserver: (_, timing) => observed.TrySetResult(timing),
            scheduler: scheduler);
        long ingressTimestamp = scheduler.GetTimestamp() - (scheduler.TimestampFrequency / 20);

        Assert.True(queue.Enqueue(
            channel,
            CreateTraffic(1),
            ingressTimestamp,
            out bool dropped));
        Assert.False(dropped);
        ReceiveWorkItemTiming timing = await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        ReceiveWorkQueueDiagnostics diagnostics = queue.GetDiagnostics(channel);

        Assert.Equal(TimeSpan.FromMilliseconds(50), timing.IngressToQueueDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(15), timing.ProcessingDuration);
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
    public async Task CarriesParsedEncryptionFactsToTheOrderedProcessor()
    {
        var observed = new TaskCompletionSource<RadioFrameEncryption?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = CreateChannel("Dispatch", "100");
        var expected = new RadioFrameEncryption(true, AlgorithmId: 5, KeyId: 42);
        await using ChannelReceiveWorkQueue queue =
            ChannelReceiveWorkQueue.CreateWithIngressTiming((_, ingress, _) =>
            {
                observed.TrySetResult(ingress.Encryption);
                return Task.FromResult(default(ReceiveProcessingStageTiming));
            });
        var ingress = new RadioMediaIngressFrame(
            CreateTraffic(1),
            Stopwatch.GetTimestamp(),
            encryption: expected);

        Assert.True(queue.Enqueue(channel, ingress));

        Assert.Equal(expected, await observed.Task.WaitAsync(TimeSpan.FromSeconds(2)));
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
            timing.TransportToApplicationBoundaryDelay,
            TimeSpan.FromMilliseconds(45),
            TimeSpan.FromMilliseconds(55));
        Assert.Equal(
            timing.TransportToApplicationBoundaryDelay,
            queue.GetDiagnostics(channel).MaximumTransportToApplicationBoundaryDelay);
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
                if (traffic.Protocol == RadioMediaProtocol.P25)
                    return Task.CompletedTask;

                Interlocked.Increment(ref readyFramesProcessed);
                processedSignal.Release();
                return Task.CompletedTask;
            },
            maxPendingFramesPerChannel: batchSize * 2,
            timingObserver: (_, timing) =>
            {
                if (timing.Traffic.Protocol == RadioMediaProtocol.P25)
                    delayedFrameObserved.TrySetResult();
            },
            getJitterBufferProfile: (_, protocol) =>
                protocol == RadioMediaProtocol.P25
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
        Assert.True(diagnostics.WakeWaits >= 1);
        Assert.Equal(1, diagnostics.WakeTimeouts);
        Assert.Equal(1, diagnostics.WorkerStarts);
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

    [Fact]
    public async Task FinalDisposalDiscardsQueuedVoiceButPreservesTerminatorAndCompletion()
    {
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completionRan = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var processed = new List<ushort>();
        var channel = CreateChannel("Dispatch", "100");
        var queue = new ChannelReceiveWorkQueue(async (_, traffic) =>
        {
            lock (processed)
                processed.Add(traffic.PacketSequence);
            if (traffic.PacketSequence == 1)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
            }
        });

        queue.Enqueue(channel, CreateTraffic(1));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        queue.Enqueue(channel, CreateTraffic(2));
        queue.Enqueue(channel, CreateTraffic(3));
        queue.Enqueue(channel, CreateTraffic(4, terminator: true));
        Task streamCompletion = queue.RunAfterStreamAsync(
            channel,
            streamId: 99,
            () =>
            {
                completionRan.TrySetResult();
                return Task.CompletedTask;
            });

        ValueTask disposal = queue.DisposeAsync();
        Assert.Equal(1, queue.CaptureHealth().CurrentDepth);
        releaseFirst.TrySetResult();
        await disposal.AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await streamCompletion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal([(ushort)1, (ushort)4], processed);
        Assert.True(completionRan.Task.IsCompletedSuccessfully);
        Assert.Equal(0, queue.CaptureHealth().CurrentDepth);
    }

    [Fact]
    public async Task ConcurrentDisposeCallersShareCompletion()
    {
        var processingStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = CreateChannel("Dispatch", "100");
        var queue = new ChannelReceiveWorkQueue(
            async (_, _) =>
            {
                processingStarted.TrySetResult();
                await release.Task;
            },
            shutdownDrainTimeout: TimeSpan.FromSeconds(5));
        queue.Enqueue(channel, CreateTraffic(1));
        await processingStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task first = queue.DisposeAsync().AsTask();
        Task second = queue.DisposeAsync().AsTask();

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        release.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task DirectContinuationDoesNotRunItsSynchronousPrefixUnderTheQueueLock()
    {
        var continuationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim();
        var channel = CreateChannel("Dispatch", "100");
        await using var queue = new ChannelReceiveWorkQueue(static (_, _) => Task.CompletedTask);

        Task continuation = queue.RunAfterStreamAsync(channel, 99, () =>
        {
            continuationEntered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(2));
            return Task.CompletedTask;
        });
        await continuationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task queueOperation = Task.Run(() => queue.Start(channel));
        await queueOperation.WaitAsync(TimeSpan.FromMilliseconds(500));
        release.Set();
        await continuation.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task FinalDisposalCancelsAStalledProcessorAndReportsItsIdentity()
    {
        var processingStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnostics = new List<ReceiveWorkerShutdownDiagnostic>();
        var channel = CreateChannel("Dispatch", "100");
        var queue = new ChannelReceiveWorkQueue(
            async (_, _, cancellationToken) =>
            {
                processingStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
            shutdownDelayObserver: observed => diagnostics.AddRange(observed),
            shutdownDrainTimeout: TimeSpan.FromMilliseconds(25),
            cancellationAcknowledgementTimeout: TimeSpan.FromMilliseconds(250));

        queue.Enqueue(channel, CreateTraffic(1));
        await processingStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await queue.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        ReceiveWorkerShutdownDiagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(new ChannelId(channel.SessionId), diagnostic.ChannelId);
        Assert.Equal(99u, diagnostic.StreamId);
        Assert.Equal(ReceiveWorkerActivity.ProcessingFrame, diagnostic.Activity);
        Assert.False(diagnostic.CancellationAcknowledgementTimedOut);
    }

    [Fact]
    public async Task ChannelRetirementCancelsAStalledProcessorWithinItsBound()
    {
        var processingStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnostics = new List<ReceiveWorkerShutdownDiagnostic>();
        var channel = CreateChannel("Dispatch", "100");
        await using var queue = new ChannelReceiveWorkQueue(
            async (_, _, cancellationToken) =>
            {
                processingStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
            shutdownDelayObserver: observed => diagnostics.AddRange(observed),
            shutdownDrainTimeout: TimeSpan.FromMilliseconds(25),
            cancellationAcknowledgementTimeout: TimeSpan.FromMilliseconds(250));

        queue.Enqueue(channel, CreateTraffic(1));
        await processingStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await queue.StopAsync(channel).WaitAsync(TimeSpan.FromSeconds(1));

        ReceiveWorkerShutdownDiagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(new ChannelId(channel.SessionId), diagnostic.ChannelId);
        Assert.False(diagnostic.CancellationAcknowledgementTimedOut);
    }

    [Fact]
    public async Task ChannelRetirementDefersAProcessorThatIgnoresCancellation()
    {
        var processingStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProcessor = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = CreateChannel("Dispatch", "100");
        var queue = new ChannelReceiveWorkQueue(
            async (_, _, _) =>
            {
                processingStarted.TrySetResult();
                await releaseProcessor.Task;
            },
            shutdownDrainTimeout: TimeSpan.FromMilliseconds(25),
            cancellationAcknowledgementTimeout: TimeSpan.FromMilliseconds(25));

        queue.Enqueue(channel, CreateTraffic(1));
        await processingStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            queue.StopAsync(channel).WaitAsync(TimeSpan.FromSeconds(1)));
        releaseProcessor.TrySetResult();
        await queue.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task FinalDisposalCancelsAStalledStreamContinuation()
    {
        var packetProcessed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var continuationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnostics = new List<ReceiveWorkerShutdownDiagnostic>();
        var channel = CreateChannel("Dispatch", "100");
        var queue = new ChannelReceiveWorkQueue(
            (_, _, _) =>
            {
                packetProcessed.TrySetResult();
                return Task.CompletedTask;
            },
            shutdownDelayObserver: observed => diagnostics.AddRange(observed),
            shutdownDrainTimeout: TimeSpan.FromMilliseconds(25),
            cancellationAcknowledgementTimeout: TimeSpan.FromMilliseconds(250));

        queue.Enqueue(channel, CreateTraffic(1));
        await packetProcessed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task continuation = queue.RunAfterStreamAsync(
            channel,
            streamId: 99,
            async cancellationToken =>
            {
                continuationStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
        await continuationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await queue.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => continuation);
        ReceiveWorkerShutdownDiagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(99u, diagnostic.StreamId);
        Assert.Equal(ReceiveWorkerActivity.RunningContinuation, diagnostic.Activity);
    }

    [Fact]
    public async Task FinalDisposalStopsWaitingWhenAProcessorIgnoresCancellation()
    {
        var processingStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProcessor = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnostics = new List<ReceiveWorkerShutdownDiagnostic>();
        var channel = CreateChannel("Dispatch", "100");
        var queue = new ChannelReceiveWorkQueue(
            async (_, _, _) =>
            {
                processingStarted.TrySetResult();
                await releaseProcessor.Task;
            },
            shutdownDelayObserver: observed => diagnostics.AddRange(observed),
            shutdownDrainTimeout: TimeSpan.FromMilliseconds(25),
            cancellationAcknowledgementTimeout: TimeSpan.FromMilliseconds(25));

        queue.Enqueue(channel, CreateTraffic(1));
        await processingStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        TimeoutException exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            queue.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Contains("activity=ProcessingFrame", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, diagnostics.Count);
        Assert.False(diagnostics[0].CancellationAcknowledgementTimedOut);
        Assert.True(diagnostics[1].CancellationAcknowledgementTimedOut);
        releaseProcessor.TrySetResult();
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

        public long TimestampFrequency => Stopwatch.Frequency;

        public void Advance(TimeSpan elapsed)
            => Interlocked.Add(ref timestamp, (long)(elapsed.TotalSeconds * TimestampFrequency));

        public TimeSpan GetElapsedTime(long startTimestamp, long endTimestamp)
            => Stopwatch.GetElapsedTime(startTimestamp, endTimestamp);

        public ValueTask<bool> WaitAsync(
            CoalescingWakeSignal signal,
            TimeSpan timeout)
        {
            if (signal.TryConsume())
                return ValueTask.FromResult(true);
            if (timeout == Timeout.InfiniteTimeSpan)
                return signal.WaitAsync(timeout);

            long elapsedTicks = Math.Max(
                1,
                (long)Math.Ceiling(timeout.TotalSeconds * Stopwatch.Frequency));
            Interlocked.Add(ref timestamp, elapsedTicks);
            Interlocked.Add(ref timedOutTicks, elapsedTicks);
            return ValueTask.FromResult(false);
        }

        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default)
            => Task.Delay(delay, cancellationToken);
    }

    private sealed class TestReceiveEpisodeCompletionPort(
        Func<ChannelId, IReadOnlyCollection<uint>, Func<Task>, Task> runAfterStreams,
        Func<ChannelId, long, Task> completePlayback,
        Action<ChannelId, long> stopRecording) : IReceiveEpisodeCompletionPort
    {
        public Task RunAfterStreamsAsync(
            ChannelId channelId,
            IReadOnlyCollection<uint> streamIds,
            Func<Task> continuation)
            => runAfterStreams(channelId, streamIds, continuation);

        public Task CompletePlaybackAsync(ChannelId channelId, long episodeId)
            => completePlayback(channelId, episodeId);

        public ChannelId? ResolveRecordingTarget(ChannelId channelId)
            => channelId;

        public void StopRecording(ChannelId channelId, long episodeId)
            => stopRecording(channelId, episodeId);
    }
}
