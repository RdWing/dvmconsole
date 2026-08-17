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
        Assert.Equal("Secure", entry.EncryptionText);
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
            algorithmId: DvmConsole.Media.DmrPrivacyAlgorithms.DesOfb,
            keyId: 0x0050));

        Assert.Equal(DvmConsole.Media.DmrPrivacyAlgorithms.DesOfb, entry.EncryptionAlgorithmId);
        Assert.Equal((ushort)0x0050, entry.EncryptionKeyId);
        Assert.Equal("Secure · DES", entry.EncryptionText);
    }

    [Fact]
    public void StoresEventRowsWithoutTreatingThemAsActiveCalls()
    {
        var store = new CallHistoryStore();

        store.AddEvent(
            DateTimeOffset.UnixEpoch,
            "FNE",
            "Alpha connected",
            ridText: "3100",
            tgidText: "127.0.0.1:62031");

        CallHistoryEntry entry = Assert.Single(store.Entries);
        Assert.True(entry.IsEvent);
        Assert.False(entry.IsActive);
        Assert.Equal("FNE", entry.DisplayChannelText);
        Assert.Equal("Alpha connected", entry.RouteText);
        Assert.Equal("3100", entry.DisplaySourceText);
        Assert.Equal("127.0.0.1:62031", entry.DisplayDestinationText);
        Assert.Equal("EVENT", entry.ProtocolText);
        Assert.Equal("—", entry.DurationText);
        Assert.False(store.Complete("FNE", FneTrafficProtocol.Dmr, 0, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void CompletesConsoleTransmissionSeparatelyFromReceiveCalls()
    {
        var store = new CallHistoryStore();
        store.AddConsoleTransmission(
            DateTimeOffset.UnixEpoch,
            "System 1",
            "Dispatch",
            42,
            100,
            FneTrafficProtocol.Dmr,
            77,
            callerText: "Console");

        CallHistoryEntry entry = Assert.Single(store.Entries);
        Assert.True(entry.IsConsoleTransmission);
        Assert.True(store.CompleteConsoleTransmission(
            "System 1",
            FneTrafficProtocol.Dmr,
            77,
            DateTimeOffset.UnixEpoch.AddSeconds(1.5)));
        Assert.False(entry.IsActive);
        Assert.Equal("1.5s", entry.DurationText);
    }

    [Fact]
    public void ExposesAnAttachedTarRecordingToHistoryViews()
    {
        CallHistoryEntry entry = CreateEntry(42);
        var metadata = new CallRecordingMetadata
        {
            StreamId = 42,
            FileName = "dispatch.wav"
        };

        Assert.False(entry.HasRecording);
        entry.SetRecording(metadata);

        Assert.True(entry.HasRecording);
        Assert.Same(metadata, entry.Recording);
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
