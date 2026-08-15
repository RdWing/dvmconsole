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
