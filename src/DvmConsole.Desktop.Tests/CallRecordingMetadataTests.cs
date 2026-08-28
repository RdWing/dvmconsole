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

    [Fact]
    public void RecordingOnlyHistoryRestoresExactEncryptionIdentifiers()
    {
        var metadata = new CallRecordingMetadata
        {
            SchemaVersion = CallRecordingMetadata.CurrentSchemaVersion,
            Protocol = "DMR",
            IsEncrypted = true,
            EncryptionState = CallRecordingEncryptionState.Secure,
            EncryptionAlgorithmId = DvmConsole.Media.DmrPrivacyAlgorithms.DesOfb,
            EncryptionAlgorithm = "DES-OFB",
            EncryptionKeyIdValue = 0x50,
            EncryptionKeyId = "0x50"
        };

        CallHistoryEntry entry = CallHistoryEntry.CreateRecordingOnly(metadata);

        Assert.True(entry.EncryptionKnown);
        Assert.True(entry.Encrypted);
        Assert.Equal(DvmConsole.Media.DmrPrivacyAlgorithms.DesOfb, entry.EncryptionAlgorithmId);
        Assert.Equal((ushort)0x50, entry.EncryptionKeyId);
        Assert.Equal("Secure · DES", entry.EncryptionText);
    }

    [Fact]
    public void CurrentSchemaDistinguishesUnknownFromClear()
    {
        var unknown = new CallRecordingMetadata
        {
            SchemaVersion = CallRecordingMetadata.CurrentSchemaVersion,
            EncryptionState = CallRecordingEncryptionState.Unknown
        };
        var clear = new CallRecordingMetadata
        {
            SchemaVersion = CallRecordingMetadata.CurrentSchemaVersion,
            EncryptionState = CallRecordingEncryptionState.Clear
        };

        Assert.Equal("Unknown", unknown.EncryptionText);
        Assert.False(unknown.IsEncryptionKnown);
        Assert.Equal("Clear", clear.EncryptionText);
        Assert.True(clear.IsEncryptionKnown);
    }

    [Fact]
    public void HistoryEntryExposesPlaybackActionStateForItsTarButton()
    {
        CallHistoryEntry entry = CallHistoryEntry.CreateRecordingOnly(new CallRecordingMetadata
        {
            DurationMs = 1_000,
            PlaybackValidated = true,
            FilePath = "/recordings/call.opus",
            FileSizeBytes = 1_000,
            ActiveSampleCount = 8_000,
            PeakAmplitude = 100
        });
        var changed = new List<string?>();
        entry.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        entry.SetRecordingPlaying(true);

        Assert.True(entry.IsRecordingPlaying);
        Assert.Equal("Stop TAR recording playback", entry.RecordingPlaybackToolTip);
        Assert.Contains(nameof(CallHistoryEntry.IsRecordingPlaying), changed);
        Assert.Contains(nameof(CallHistoryEntry.RecordingPlaybackToolTip), changed);

        entry.SetRecordingPlaying(false);
        Assert.False(entry.IsRecordingPlaying);
        Assert.Equal("Play validated TAR recording", entry.RecordingPlaybackToolTip);
    }
}
