using DvmConsole.Desktop;
using DvmConsole.FneClient;
using System.Diagnostics;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ReceivePresentationControllerTests
{
    [Fact]
    public async Task BackgroundTrafficIsBatchedAndUiTrafficIsImmediate()
    {
        await using var system = new SystemViewModel(
            new FneConnectionOptions("Test", "Console", "127.0.0.1", 62031, 1, null, false, null),
            "Test",
            "127.0.0.1:62031");
        var posted = new Queue<Action>();
        var diagnosticsFlags = new List<bool>();
        var controller = new ReceivePresentationController(
            () => false,
            () => false,
            posted.Enqueue,
            (_, _, publishDiagnostics) => diagnosticsFlags.Add(publishDiagnostics),
            getTimestamp: () => Stopwatch.Frequency);

        for (int index = 0; index < 65; index++)
            controller.Present(system, default);

        Assert.Single(posted);
        posted.Dequeue()();
        Assert.Equal(64, diagnosticsFlags.Count);
        Assert.All(diagnosticsFlags, Assert.False);
        Assert.Single(posted);
        posted.Dequeue()();
        Assert.Equal(65, diagnosticsFlags.Count);

        var immediate = new ReceivePresentationController(
            () => false,
            () => true,
            _ => throw new InvalidOperationException("UI traffic must not be posted."),
            (_, _, publishDiagnostics) => diagnosticsFlags.Add(publishDiagnostics));
        immediate.Present(system, default);

        Assert.True(diagnosticsFlags[^1]);
    }

    [Fact]
    public async Task BackgroundTrafficYieldsAtTimeBudgetAndPreservesOrder()
    {
        await using var system = CreateSystem();
        var posted = new Queue<Action>();
        var presented = new List<ushort>();
        long timestamp = Stopwatch.Frequency;
        long twoMilliseconds = Stopwatch.Frequency / 500;
        var controller = new ReceivePresentationController(
            () => false,
            () => false,
            posted.Enqueue,
            (_, workItem, _) =>
            {
                presented.Add(workItem.Traffic.PacketSequence);
                timestamp += twoMilliseconds;
            },
            maximumBatchDuration: TimeSpan.FromMilliseconds(4),
            getTimestamp: () => timestamp);

        for (ushort sequence = 0; sequence < 5; sequence++)
            controller.Present(system, CreateWorkItem(sequence));

        Assert.Single(posted);
        posted.Dequeue()();
        Assert.Equal([(ushort)0, (ushort)1], presented);
        Assert.Single(posted);

        posted.Dequeue()();
        Assert.Equal([(ushort)0, (ushort)1, (ushort)2, (ushort)3], presented);
        Assert.Single(posted);

        posted.Dequeue()();
        Assert.Equal([(ushort)0, (ushort)1, (ushort)2, (ushort)3, (ushort)4], presented);
        Assert.Empty(posted);
    }

    [Fact]
    public async Task TimeSlicedDrainRetainsExistingBacklogDropPolicyAndFifoOrder()
    {
        await using var system = CreateSystem();
        var posted = new Queue<Action>();
        var presented = new List<ushort>();
        long timestamp = Stopwatch.Frequency;
        long fourMilliseconds = Stopwatch.Frequency / 250;
        var controller = new ReceivePresentationController(
            () => false,
            () => false,
            posted.Enqueue,
            (_, workItem, _) =>
            {
                presented.Add(workItem.Traffic.PacketSequence);
                timestamp += fourMilliseconds;
            },
            maximumBatchDuration: TimeSpan.FromMilliseconds(4),
            getTimestamp: () => timestamp);

        for (ushort sequence = 0; sequence < 300; sequence++)
            controller.Present(system, CreateWorkItem(sequence));

        while (posted.TryDequeue(out Action? drain))
            drain();

        Assert.Equal(
            Enumerable.Range(44, 256).Select(value => (ushort)value),
            presented);
        Assert.Contains("UI backlog drops 44", system.ConnectionHealthText);
    }

    private static SystemViewModel CreateSystem()
        => new(
            new FneConnectionOptions("Test", "Console", "127.0.0.1", 62031, 1, null, false, null),
            "Test",
            "127.0.0.1:62031");

    private static SystemTrafficWorkItem CreateWorkItem(ushort sequence)
        => new(
            new FneTrafficFrame(
                FneTrafficProtocol.Dmr,
                peerId: 1,
                sourceId: 2,
                destinationId: 100,
                slot: 1,
                callType: "GROUP",
                frameType: "VOICE",
                subtype: "VOICE",
                packetSequence: sequence,
                streamId: 99,
                payload: []),
            DateTimeOffset.UnixEpoch,
            0,
            [],
            []);
}
