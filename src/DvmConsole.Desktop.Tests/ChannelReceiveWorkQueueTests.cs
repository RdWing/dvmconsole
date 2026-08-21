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
        uint streamId = 99)
        => new(
            FneTrafficProtocol.Dmr,
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
}
