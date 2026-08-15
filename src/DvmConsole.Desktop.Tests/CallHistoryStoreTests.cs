using DvmConsole.Desktop;
using DvmConsole.FneClient;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class CallHistoryStoreTests
{
    [Fact]
    public void AddsNewestCallsFirstAndTrimsOldEntries()
    {
        var store = new CallHistoryStore(maxEntries: 2);
        CallHistoryEntry first = CreateEntry(1);
        CallHistoryEntry second = CreateEntry(2);
        CallHistoryEntry third = CreateEntry(3);

        store.Add(first);
        store.Add(second);
        store.Add(third);

        Assert.Equal([third, second], store.Entries);
    }

    [Fact]
    public void CompletesMatchingActiveStreamWithDuration()
    {
        var store = new CallHistoryStore();
        CallHistoryEntry entry = CreateEntry(42);
        store.Add(entry);

        bool completed = store.Complete(
            "System 1",
            FneTrafficProtocol.Dmr,
            42,
            DateTimeOffset.UnixEpoch.AddSeconds(2.25));

        Assert.True(completed);
        Assert.False(entry.IsActive);
        Assert.Equal(TimeSpan.FromSeconds(2.25), entry.Duration);
        Assert.Equal("2.3s", entry.DurationText);
        Assert.False(store.Complete("Other", FneTrafficProtocol.Dmr, 42, DateTimeOffset.UtcNow));
    }

    private static CallHistoryEntry CreateEntry(uint streamId)
    {
        return new CallHistoryEntry(
            DateTimeOffset.UnixEpoch,
            "System 1",
            "Dispatch",
            42,
            100,
            FneTrafficProtocol.Dmr,
            streamId);
    }
}
