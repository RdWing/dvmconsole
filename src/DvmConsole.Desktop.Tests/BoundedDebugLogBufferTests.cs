using DvmConsole.Core.Diagnostics;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class BoundedDebugLogBufferTests
{
    [Fact]
    public void RetainsNewestSessionEntriesWithinCountLimit()
    {
        var buffer = new BoundedDebugLogBuffer(maximumEntries: 2, maximumBytes: 4_096);

        buffer.Add(Entry("first"));
        buffer.Add(Entry("second"));
        buffer.Add(Entry("third"));

        Assert.Equal(["third", "second"], buffer.Entries.Select(entry => entry.Message));
        Assert.Contains("oldest discarded 1", buffer.RetentionText);
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
