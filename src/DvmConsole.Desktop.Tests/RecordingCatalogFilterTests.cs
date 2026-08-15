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
            SubscriberAlias = "Medic 42",
            UtcStartTime = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero),
            FileName = "dispatch.wav"
        };

        Assert.True(new RecordingCatalogFilter(
            Direction: "RX",
            Protocol: "P25",
            Encryption: "Encrypted",
            System: "alp",
            Channel: "patch",
            Talkgroup: "101",
            Subscriber: "42",
            Alias: "medic",
            StartDate: new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero),
            EndDate: new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero)).Matches(metadata));
        Assert.False(new RecordingCatalogFilter(Direction: "TX").Matches(metadata));
        Assert.False(new RecordingCatalogFilter(Encryption: "Clear").Matches(metadata));
        Assert.False(new RecordingCatalogFilter(SearchText: "missing").Matches(metadata));
        Assert.False(new RecordingCatalogFilter(Alias: "fire").Matches(metadata));
        Assert.False(new RecordingCatalogFilter(StartDate: new DateTimeOffset(2026, 8, 16, 0, 0, 0, TimeSpan.Zero)).Matches(metadata));
        Assert.False(new RecordingCatalogFilter(EndDate: new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero)).Matches(metadata));
    }

    [Fact]
    public void ExposesLegacyTarColumnDiagnostics()
    {
        var metadata = new CallRecordingMetadata
        {
            Protocol = "P25",
            TalkgroupId = 101,
            SubscriberId = 42,
            SubscriberAlias = "Medic 42",
            IsEncrypted = true,
            EncryptionAlgorithm = "AES",
            EncryptionKeyId = "0042",
            SampleRate = 8000,
            BitsPerSample = 16,
            ChannelCount = 1,
            FileSizeBytes = 32000
        };

        Assert.Equal("101", metadata.TalkgroupText);
        Assert.Equal("42", metadata.SubscriberText);
        Assert.Equal("AES / 0042", metadata.EncryptionText);
        Assert.Contains("8,000 Hz · 16-bit · 1 ch · 32,000 B", metadata.TechnicalDetailsText, StringComparison.Ordinal);
        Assert.Contains("alias Medic 42", metadata.DetailText, StringComparison.Ordinal);
    }
}
