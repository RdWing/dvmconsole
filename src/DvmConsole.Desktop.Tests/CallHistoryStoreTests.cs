using System.Collections.ObjectModel;
using DvmConsole.Desktop;
using DvmConsole.FneClient;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class CallHistoryStoreTests
{
    [Fact]
    public void SynchronizesSharedHistoryViewAtomicallyAcrossRefreshes()
    {
        CallHistoryEntry[] entries = Enumerable.Range(1, 12)
            .Select(streamId => CreateEntry((uint)streamId))
            .ToArray();
        var target = new ObservableCollection<CallHistoryEntry>(entries);
        CallHistoryEntry[] firstView = entries.Take(4).ToArray();
        CallHistoryEntry[] secondView = entries.Skip(4).Take(6).ToArray();

        Parallel.For(0, 2_000, index =>
            MainWindowViewModel.SynchronizeHistoryView(
                target,
                index % 2 == 0 ? firstView : secondView));

        Assert.True(
            target.SequenceEqual(firstView) || target.SequenceEqual(secondView),
            "The final view should match one complete refresh rather than interleaved mutations.");
    }

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
    public void RemovesSubFrameHistoryShellButRestoresAnyPlayableTarRecording()
    {
        var store = new CallHistoryStore();
        CallHistoryEntry entry = CreateEntry(42);
        store.Add(entry);

        Assert.True(store.Complete(
            "System 1",
            FneTrafficProtocol.Dmr,
            42,
            DateTimeOffset.UnixEpoch.AddMilliseconds(20)));
        Assert.Empty(store.Entries);

        CallRecordingMetadata recording = CreatePlayableRecording(42);
        CallHistoryEntry restored = store.AddOrAttachRecording(recording);

        Assert.Same(restored, Assert.Single(store.Entries));
        Assert.True(restored.IsRecordingOnly);
        Assert.Same(recording, restored.Recording);
    }

    [Fact]
    public void DetectsAnExistingActiveReceiveCallByFullRouteIdentity()
    {
        var store = new CallHistoryStore();
        store.Add(CreateEntry(42));

        Assert.True(store.HasActiveReceiveCall("System 1", FneTrafficProtocol.Dmr, 42, "Dispatch", 100));
        Assert.False(store.HasActiveReceiveCall("System 1", FneTrafficProtocol.Dmr, 43, "Dispatch", 100));
        Assert.False(store.HasActiveReceiveCall("System 1", FneTrafficProtocol.Dmr, 42, "Tactical", 100));
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

    [Theory]
    [InlineData(5_400, "5.4s")]
    [InlineData(65_400, "1m 05.4s")]
    [InlineData(3_723_400, "1h 02m 03.4s")]
    public void FormatsLiveAndRecordedCallDurationsConsistently(
        int durationMilliseconds,
        string expected)
    {
        var liveEntry = CreateEntry(42);
        liveEntry.Complete(DateTimeOffset.UnixEpoch.AddMilliseconds(durationMilliseconds));

        CallRecordingMetadata recording = CreatePlayableRecording(99);
        recording.DurationMs = durationMilliseconds;
        CallHistoryEntry recordedEntry = CallHistoryEntry.CreateRecordingOnly(recording);

        Assert.Equal(expected, liveEntry.DurationText);
        Assert.Equal(expected, recording.DurationText);
        Assert.Equal(expected, recordedEntry.DurationText);
    }

    [Fact]
    public void CompletesOnlyTheMatchingConcurrentConsoleTransmission()
    {
        var store = new CallHistoryStore();
        store.AddConsoleTransmission(DateTimeOffset.UnixEpoch, "System 1", "Dispatch A", 42, 100,
            FneTrafficProtocol.Dmr, 77);
        store.AddConsoleTransmission(DateTimeOffset.UnixEpoch, "System 1", "Dispatch B", 42, 200,
            FneTrafficProtocol.Dmr, 77);

        Assert.True(store.CompleteConsoleTransmission(
            "System 1", FneTrafficProtocol.Dmr, 77, DateTimeOffset.UnixEpoch.AddSeconds(1),
            "Dispatch B", 200));

        Assert.True(store.Entries.Single(entry => entry.ChannelName == "Dispatch A").IsActive);
        Assert.False(store.Entries.Single(entry => entry.ChannelName == "Dispatch B").IsActive);
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

    [Fact]
    public void AttachesCompletedRecordingToExistingCallWithoutAddingDuplicateRow()
    {
        var store = new CallHistoryStore();
        CallHistoryEntry entry = CreateEntry(42);
        store.Add(entry);
        CallRecordingMetadata recording = CreatePlayableRecording(streamId: 42);

        CallHistoryEntry attached = store.AddOrAttachRecording(recording);

        Assert.Same(entry, attached);
        Assert.Same(recording, entry.Recording);
        Assert.True(entry.HasPlayableRecording);
        Assert.Single(store.Entries);
    }

    [Fact]
    public void AddsOlderCompletedRecordingAsHistoryRowWithCatalogDetails()
    {
        var store = new CallHistoryStore();
        CallRecordingMetadata recording = CreatePlayableRecording(streamId: 99);
        recording.Direction = "TX";
        recording.SubscriberAlias = "Dispatch console";

        CallHistoryEntry entry = store.AddOrAttachRecording(recording);

        Assert.True(entry.IsRecordingOnly);
        Assert.True(entry.IsConsoleTransmission);
        Assert.Equal("Dispatch console → TG 100", entry.RouteText);
        Assert.Equal(recording.DurationText, entry.DurationText);
        Assert.Equal(recording.TechnicalDetailsText, entry.RecordingDetailsText);
        Assert.True(entry.HasPlayableRecording);
    }

    [Fact]
    public void RemovingRecordingKeepsLiveCallButRemovesRecordingOnlyRow()
    {
        var store = new CallHistoryStore();
        CallHistoryEntry liveCall = CreateEntry(42);
        store.Add(liveCall);
        CallRecordingMetadata liveRecording = CreatePlayableRecording(streamId: 42);
        store.AddOrAttachRecording(liveRecording);
        CallRecordingMetadata archivedRecording = CreatePlayableRecording(streamId: 99);
        CallHistoryEntry archived = store.AddOrAttachRecording(archivedRecording);

        store.RemoveRecording(liveRecording);
        store.RemoveRecording(archivedRecording);

        Assert.Contains(liveCall, store.Entries);
        Assert.Null(liveCall.Recording);
        Assert.DoesNotContain(archived, store.Entries);
    }

    [Fact]
    public void SessionLimitDoesNotDiscardRecordingCatalogRows()
    {
        var store = new CallHistoryStore(maxEntries: 1);
        CallHistoryEntry archived = store.AddOrAttachRecording(CreatePlayableRecording(streamId: 99));

        store.Add(CreateEntry(1));
        store.Add(CreateEntry(2));

        Assert.Contains(archived, store.Entries);
        Assert.Equal(2, store.Entries.Count);
        Assert.Equal((uint)2, store.Entries[0].StreamId);
    }

    [Fact]
    public void SameStreamIdOnConcurrentChannelsDoesNotCompleteOrAttachWrongCall()
    {
        var store = new CallHistoryStore();
        CallHistoryEntry dispatch = CreateEntry(42);
        var tactical = new CallHistoryEntry(
            DateTimeOffset.UnixEpoch,
            "System 1",
            "Tactical",
            84,
            200,
            FneTrafficProtocol.Dmr,
            42);
        store.Add(dispatch);
        store.Add(tactical);

        Assert.True(store.Complete(
            "System 1",
            FneTrafficProtocol.Dmr,
            42,
            DateTimeOffset.UnixEpoch.AddSeconds(1),
            channelName: "Tactical",
            destinationId: 200));
        CallRecordingMetadata recording = CreatePlayableRecording(42);
        CallHistoryEntry attached = store.AddOrAttachRecording(recording);

        Assert.True(dispatch.IsActive);
        Assert.False(tactical.IsActive);
        Assert.Same(dispatch, attached);
        Assert.Null(tactical.Recording);
    }

    [Fact]
    public void ClearingSessionKeepsAttachedRecordingsInCatalog()
    {
        var store = new CallHistoryStore();
        CallHistoryEntry call = CreateEntry(42);
        store.Add(call);
        CallRecordingMetadata recording = CreatePlayableRecording(42);
        store.AddOrAttachRecording(recording);

        store.Clear();

        CallHistoryEntry archived = Assert.Single(store.Entries);
        Assert.True(archived.IsRecordingOnly);
        Assert.Same(recording, archived.Recording);
    }

    [Fact]
    public void LiveCallMergesARecordingCatalogRowLoadedFirst()
    {
        var store = new CallHistoryStore();
        CallRecordingMetadata recording = CreatePlayableRecording(42);
        CallHistoryEntry archived = store.AddOrAttachRecording(recording);
        CallHistoryEntry live = CreateEntry(42);

        store.Add(live);

        Assert.Single(store.Entries);
        Assert.DoesNotContain(archived, store.Entries);
        Assert.Same(recording, live.Recording);
    }

    private static CallRecordingMetadata CreatePlayableRecording(uint streamId)
    {
        return new CallRecordingMetadata
        {
            RecordingId = $"recording-{streamId}",
            Direction = "RX",
            Protocol = "DMR",
            UtcStartTime = DateTimeOffset.UnixEpoch,
            UtcEndTime = DateTimeOffset.UnixEpoch.AddSeconds(10),
            DurationMs = 10_000,
            FilePath = $"/tmp/{streamId}.wav",
            FileName = $"{streamId}.wav",
            FileSizeBytes = 160_000,
            SampleRate = 8_000,
            BitsPerSample = 16,
            ChannelCount = 1,
            OriginalSampleCount = 80_000,
            ActiveSampleCount = 70_000,
            PeakAmplitude = 12_000,
            SystemName = "System 1",
            ChannelName = "Dispatch",
            TalkgroupId = 100,
            SubscriberId = 42,
            StreamId = streamId,
            PlaybackValidated = true
        };
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
