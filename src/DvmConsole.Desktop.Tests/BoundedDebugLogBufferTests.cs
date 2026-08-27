using DvmConsole.Core.Diagnostics;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class BoundedDebugLogBufferTests
{
    [Fact]
    public void DefaultSessionLimitKeepsOneHundredMegabytesAndAddsAnEntrySafetyCeiling()
    {
        var buffer = new BoundedDebugLogBuffer();

        for (int index = 0; index < 5_001; index++)
            buffer.Add(Entry($"entry {index}"));

        Assert.Equal(5_001, buffer.Entries.Count);
        Assert.Contains("limit 50,000 entries / 100.0 MB", buffer.RetentionText);
    }

    [Fact]
    public void RetainsNewestSessionEntriesWithinCountLimit()
    {
        var buffer = new BoundedDebugLogBuffer(maximumEntries: 2, maximumBytes: 4_096);

        buffer.Add(Entry("first"));
        buffer.Add(Entry("second"));
        buffer.Add(Entry("third"));

        Assert.Equal(["second", "third"], buffer.Entries.Select(entry => entry.Message));
        Assert.Contains("oldest discarded 1", buffer.RetentionText);
    }

    [Fact]
    public void AddsOneChronologicalBatchWithOneCollectionNotification()
    {
        var buffer = new BoundedDebugLogBuffer(maximumEntries: 10, maximumBytes: 4_096);
        var changes = new List<System.Collections.Specialized.NotifyCollectionChangedEventArgs>();
        buffer.Entries.CollectionChanged += (_, change) => changes.Add(change);

        buffer.AddRange([Entry("first"), Entry("second"), Entry("third")]);

        var change = Assert.Single(changes);
        Assert.Equal(System.Collections.Specialized.NotifyCollectionChangedAction.Add, change.Action);
        Assert.Equal(3, change.NewItems!.Count);
        Assert.Equal(["first", "second", "third"], buffer.Entries.Select(entry => entry.Message));
    }

    [Fact]
    public void TruncatesOneOversizedMessageBeforeApplyingMemoryLimit()
    {
        var buffer = new BoundedDebugLogBuffer(maximumEntries: 10, maximumBytes: 64 * 1_024);

        buffer.Add(Entry(new string('x', BoundedDebugLogBuffer.MaximumMessageCharacters + 100)));

        DebugLogEntry retained = Assert.Single(buffer.Entries);
        Assert.StartsWith(new string('x', BoundedDebugLogBuffer.MaximumMessageCharacters), retained.Message);
        Assert.EndsWith("[message truncated to protect session log memory]", retained.Message);
    }

    [Fact]
    public void MemoryLimitEvictsOldestEntriesWithoutDiscardingTheSessionTail()
    {
        var buffer = new BoundedDebugLogBuffer(maximumEntries: 100, maximumBytes: 420);

        buffer.Add(Entry(new string('a', 50)));
        buffer.Add(Entry(new string('b', 50)));

        DebugLogEntry retained = Assert.Single(buffer.Entries);
        Assert.Equal(new string('b', 50), retained.Message);
        Assert.Contains("oldest discarded 1", buffer.RetentionText);
    }

    private static DebugLogEntry Entry(string message)
        => new(DateTimeOffset.UnixEpoch, "Test", DebugLogSeverity.Debug, message);
}
