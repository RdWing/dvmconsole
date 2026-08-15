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

    [Fact]
    public void UpdatesEncryptionWhenProtocolMetadataArrivesLater()
    {
        var store = new CallHistoryStore();
        CallHistoryEntry entry = CreateEntry(42);
        store.Add(entry);

        Assert.True(store.UpdateEncryption("System 1", FneTrafficProtocol.Dmr, 42, encrypted: true));
        Assert.True(entry.Encrypted);
        Assert.Equal("Encrypted", entry.EncryptionText);
        Assert.False(store.UpdateEncryption("System 1", FneTrafficProtocol.Dmr, 42, encrypted: true));
    }

    [Fact]
    public void IncludesProtocolEncryptionIdentifiersWhenAvailable()
    {
        var store = new CallHistoryStore();
        CallHistoryEntry entry = CreateEntry(42);
        store.Add(entry);

        Assert.True(store.UpdateEncryption(
            "System 1",
            FneTrafficProtocol.Dmr,
            42,
            encrypted: true,
            algorithmId: 0x81,
            keyId: 0x0050));

        Assert.Equal((byte)0x81, entry.EncryptionAlgorithmId);
        Assert.Equal((ushort)0x0050, entry.EncryptionKeyId);
        Assert.Equal("Encrypted (alg 0x81, key 0x50)", entry.EncryptionText);
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
