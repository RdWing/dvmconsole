using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class CallRecordingMetadataTests
{
    [Fact]
    public void ExposesTarIdentityEncryptionAndTechnicalDetails()
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
        Assert.Contains(
            "8,000 Hz · 16-bit · 1 ch · 32,000 B",
            metadata.TechnicalDetailsText,
            StringComparison.Ordinal);
        Assert.Contains("alias Medic 42", metadata.DetailText, StringComparison.Ordinal);
    }
}
