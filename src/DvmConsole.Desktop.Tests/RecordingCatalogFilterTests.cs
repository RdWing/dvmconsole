using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class RecordingCatalogFilterTests
{
    [Fact]
    public void AppliesDirectionProtocolEncryptionAndIdentityFilters()
    {
        var metadata = new CallRecordingMetadata
        {
            Direction = "RX",
            Protocol = "P25",
            IsEncrypted = true,
            SystemName = "Alpha",
            ChannelName = "Dispatch",
            TalkgroupId = 101,
            SubscriberId = 42,
            FileName = "dispatch.wav"
        };

        Assert.True(new RecordingCatalogFilter(
            Direction: "RX",
            Protocol: "P25",
            Encryption: "Encrypted",
            System: "alp",
            Channel: "patch",
            Talkgroup: "101",
            Subscriber: "42").Matches(metadata));
        Assert.False(new RecordingCatalogFilter(Direction: "TX").Matches(metadata));
        Assert.False(new RecordingCatalogFilter(Encryption: "Clear").Matches(metadata));
        Assert.False(new RecordingCatalogFilter(SearchText: "missing").Matches(metadata));
    }
}
