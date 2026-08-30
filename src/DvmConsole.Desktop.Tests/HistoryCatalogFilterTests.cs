using DvmConsole.Desktop;
using DvmConsole.FneClient;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class HistoryCatalogFilterTests
{
    [Fact]
    public void UnfilteredMatchDoesNotMaterializeRecordingSearchText()
    {
        var entry = new CallHistoryEntry(
            DateTimeOffset.UnixEpoch,
            "Alpha",
            "Dispatch",
            42,
            101,
            FneTrafficProtocol.P25,
            17,
            "Medic 42");
        entry.SetRecording(new CallRecordingMetadata
        {
            FileName = "alpha-dispatch.wav",
            PeakAmplitude = 9000,
            OriginalSampleCount = 8000,
            ActiveSampleCount = 4000
        });
        var filter = new HistoryCatalogFilter();
        Assert.True(filter.IsUnfiltered);
        Assert.True(filter.Matches(entry));
        long before = GC.GetAllocatedBytesForCurrentThread();

        bool matches = true;
        for (int index = 0; index < 1_000; index++)
            matches &= filter.Matches(entry);

        Assert.True(matches);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void StructuredFilterWithoutSearchSkipsRecordingDetailFormatting()
    {
        var entry = new CallHistoryEntry(
            DateTimeOffset.UnixEpoch,
            "Alpha",
            "Dispatch",
            42,
            101,
            FneTrafficProtocol.P25,
            17,
            "Medic 42");
        entry.SetRecording(new CallRecordingMetadata
        {
            PeakAmplitude = 9000,
            OriginalSampleCount = 8000,
            ActiveSampleCount = 4000
        });
        var filter = new HistoryCatalogFilter(Direction: "RX");
        Assert.False(filter.IsUnfiltered);
        Assert.True(filter.Matches(entry));
        long before = GC.GetAllocatedBytesForCurrentThread();

        bool matches = true;
        for (int index = 0; index < 1_000; index++)
            matches &= filter.Matches(entry);

        Assert.True(matches);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void SearchesCallAndRecordingDetailsThroughOnePrimaryQuery()
    {
        var recording = new CallRecordingMetadata
        {
            Direction = "RX",
            Protocol = "P25",
            SystemName = "Alpha",
            ChannelName = "Dispatch",
            SubscriberId = 42,
            SubscriberAlias = "Medic 42",
            TalkgroupId = 101,
            FileName = "alpha-dispatch.wav",
            PeakAmplitude = 9000
        };
        var entry = new CallHistoryEntry(
            DateTimeOffset.UnixEpoch,
            "Alpha",
            "Dispatch",
            42,
            101,
            FneTrafficProtocol.P25,
            17,
            "Medic 42");
        entry.SetRecording(recording);

        Assert.True(new HistoryCatalogFilter(SearchText: "alpha-dispatch.wav").Matches(entry));
        Assert.True(new HistoryCatalogFilter(SearchText: "Medic 42").Matches(entry));
        Assert.True(new HistoryCatalogFilter(SearchText: "peak 9000").Matches(entry));
        Assert.True(new HistoryCatalogFilter(SearchText: "Alpha Medic 101 peak").Matches(entry));
        Assert.False(new HistoryCatalogFilter(SearchText: "Alpha Oakland").Matches(entry));
        Assert.False(new HistoryCatalogFilter(SearchText: "missing").Matches(entry));
    }

    [Fact]
    public void AdvancedFiltersApplyToCallsAndRecordingOnlyRows()
    {
        var recording = new CallRecordingMetadata
        {
            Direction = "TX",
            Protocol = "DMR",
            SystemName = "Bravo",
            ChannelName = "Ops",
            SubscriberId = 7,
            SubscriberAlias = "Console",
            TalkgroupId = 200,
            UtcStartTime = new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero),
            UtcEndTime = new DateTimeOffset(2026, 8, 17, 12, 0, 1, TimeSpan.Zero)
        };
        CallHistoryEntry entry = CallHistoryEntry.CreateRecordingOnly(recording);

        Assert.True(new HistoryCatalogFilter(
            Direction: "TX",
            Protocol: "DMR",
            Encryption: "Clear",
            System: "bra",
            Channel: "ops",
            Talkgroup: "200",
            Subscriber: "7",
            Alias: "console",
            StartDate: new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero),
            EndDate: new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero)).Matches(entry));
        Assert.False(new HistoryCatalogFilter(Direction: "RX").Matches(entry));
        Assert.False(new HistoryCatalogFilter(Encryption: "Encrypted").Matches(entry));
    }

    [Fact]
    public void ClearEncryptionFilterExcludesCallsWhoseEncryptionStateIsUnknown()
    {
        var entry = new CallHistoryEntry(
            DateTimeOffset.UtcNow,
            "System",
            "Channel",
            sourceId: 1,
            destinationId: 2,
            protocol: FneTrafficProtocol.P25,
            streamId: 3);

        Assert.Equal("Unknown", entry.EncryptionText);
        Assert.False(new HistoryCatalogFilter(Encryption: "Clear").Matches(entry));
        Assert.False(new HistoryCatalogFilter(Encryption: "Encrypted").Matches(entry));
        Assert.True(new HistoryCatalogFilter(Encryption: "All").Matches(entry));
    }
}
