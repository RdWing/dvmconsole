using DvmConsole.Desktop;
using DvmConsole.FneClient;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class HistoryCatalogFilterTests
{
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
}
