using DvmConsole.Core.Diagnostics;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class DebugLogDrainControllerTests
{
    [Fact]
    public void FirstUiThreadEntryPublishesImmediately()
    {
        var published = new List<DebugLogEntry>();
        var controller = new DebugLogDrainController(
            () => true,
            _ => throw new InvalidOperationException("The first UI-thread entry must not be posted."),
            batch => published.AddRange(batch));

        controller.Enqueue(Entry("immediate"));

        Assert.Equal("immediate", Assert.Single(published).Message);
    }

    [Fact]
    public void SustainedProducerCannotExtendOneUiDrainPastItsBatchLimit()
    {
        var posted = new Queue<Action>();
        var published = new List<IReadOnlyList<DebugLogEntry>>();
        DebugLogDrainController? controller = null;
        controller = new DebugLogDrainController(
            () => false,
            posted.Enqueue,
            batch =>
            {
                published.Add(batch.ToArray());
                for (int index = 0; index < 20; index++)
                    controller!.Enqueue(Entry($"continued {index}"));
            },
            maximumPendingEntries: 100,
            maximumBatchSize: 4);

        for (int index = 0; index < 10; index++)
            controller.Enqueue(Entry($"initial {index}"));

        Assert.Single(posted);
        posted.Dequeue()();

        Assert.Equal(4, Assert.Single(published).Count);
        Assert.Single(posted);
    }

    [Fact]
    public void BacklogIsPublishedInBoundedFifoBatches()
    {
        var posted = new Queue<Action>();
        var messages = new List<string>();
        var controller = new DebugLogDrainController(
            () => false,
            posted.Enqueue,
            batch => messages.AddRange(batch.Select(entry => entry.Message)),
            maximumPendingEntries: 20,
            maximumBatchSize: 3);

        for (int index = 0; index < 8; index++)
            controller.Enqueue(Entry(index.ToString()));

        Assert.Single(posted);
        posted.Dequeue()();
        Assert.Equal(["0", "1", "2"], messages);
        Assert.Single(posted);

        while (posted.TryDequeue(out Action? drain))
            drain();

        Assert.Equal(Enumerable.Range(0, 8).Select(index => index.ToString()), messages);
    }

    [Fact]
    public void PendingLimitDropsRoutineTrafficButRetainsIncomingWarnings()
    {
        var posted = new Queue<Action>();
        var published = new List<DebugLogEntry>();
        var controller = new DebugLogDrainController(
            () => false,
            posted.Enqueue,
            batch => published.AddRange(batch),
            getNow: () => DateTimeOffset.UnixEpoch.AddMinutes(1),
            maximumPendingEntries: 2,
            maximumBatchSize: 2);

        controller.Enqueue(Entry("first"));
        controller.Enqueue(Entry("second"));
        controller.Enqueue(Entry("routine overflow"));
        controller.Enqueue(Entry("important", DebugLogSeverity.Warning));
        posted.Dequeue()();

        Assert.DoesNotContain(published, entry => entry.Message == "first");
        Assert.DoesNotContain(published, entry => entry.Message == "routine overflow");
        Assert.Contains(published, entry => entry.Message == "second");
        Assert.Contains(published, entry => entry.Message == "important");
        DebugLogEntry summary = Assert.Single(
            published,
            entry => entry.Source == "LOG");
        Assert.Equal(DebugLogSeverity.Warning, summary.Severity);
        Assert.Contains("Discarded 2", summary.Message);
    }

    [Fact]
    public void TenThousandEntryBurstKeepsPendingMemoryAndEveryUiBatchBounded()
    {
        const int maximumPendingEntries = 2_048;
        const int maximumBatchSize = 64;
        var posted = new Queue<Action>();
        var published = new List<DebugLogEntry>();
        int largestBatch = 0;
        var controller = new DebugLogDrainController(
            () => false,
            posted.Enqueue,
            batch =>
            {
                largestBatch = Math.Max(largestBatch, batch.Count);
                published.AddRange(batch);
            },
            maximumPendingEntries: maximumPendingEntries,
            maximumBatchSize: maximumBatchSize);

        for (int index = 0; index < 10_000; index++)
            controller.Enqueue(Entry($"entry {index}"));

        Assert.Single(posted);
        while (posted.TryDequeue(out Action? drain))
            drain();

        Assert.Equal(maximumPendingEntries + 1, published.Count);
        Assert.InRange(largestBatch, 1, maximumBatchSize + 1);
        DebugLogEntry summary = Assert.Single(
            published,
            entry => entry.Source == "LOG");
        Assert.Contains("Discarded 7,952", summary.Message);
    }

    [Fact]
    public void StoppedControllerDiscardsPendingWorkWithoutPublishing()
    {
        var posted = new Queue<Action>();
        var published = new List<DebugLogEntry>();
        bool stopped = false;
        var controller = new DebugLogDrainController(
            () => false,
            posted.Enqueue,
            batch => published.AddRange(batch),
            () => stopped);

        controller.Enqueue(Entry("pending"));
        stopped = true;
        posted.Dequeue()();
        controller.Enqueue(Entry("late"));

        Assert.Empty(published);
        Assert.Empty(posted);
    }

    private static DebugLogEntry Entry(
        string message,
        DebugLogSeverity severity = DebugLogSeverity.Debug)
        => new(DateTimeOffset.UnixEpoch, "Test", severity, message);
}
