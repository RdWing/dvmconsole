using DvmConsole.Media;

namespace DvmConsole.Desktop;

internal sealed class RecordingMetadataFactory
{
    public CallRecordingMetadata Create(
        RecordingFinalizationDescriptor descriptor,
        PcmWavTrimResult trim,
        string recordingPath)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingPath);
        var metadata = new CallRecordingMetadata
        {
            SchemaVersion = CallRecordingMetadata.CurrentSchemaVersion,
            Protocol = descriptor.ProtocolText,
            Direction = descriptor.Direction,
            RecordingSourceType = descriptor.RecordingSourceType,
            UtcStartTime = descriptor.UtcStartTime,
            UtcEndTime = descriptor.UtcEndTime,
            DurationMs = (long)Math.Round(
                trim.OutputSamples * 1000d / descriptor.Format.SampleRate,
                MidpointRounding.AwayFromZero),
            FilePath = recordingPath,
            FileName = Path.GetFileName(recordingPath),
            FileSizeBytes = 0,
            SampleRate = descriptor.Format.SampleRate,
            BitsPerSample = descriptor.Format.BitsPerSample,
            ChannelCount = descriptor.Format.Channels,
            OriginalSampleCount = trim.OriginalSamples,
            ActiveSampleCount = trim.ActiveSampleCount,
            PeakAmplitude = trim.PeakAmplitude,
            TrimLeadMs = trim.TrimLeadMs,
            TrimTailMs = trim.TrimTailMs,
            SystemName = descriptor.SystemName,
            ChannelName = descriptor.ChannelName,
            TalkgroupId = descriptor.TalkgroupId,
            SubscriberId = descriptor.SourceId,
            SubscriberAlias = descriptor.SubscriberAlias,
            StreamId = descriptor.StreamId,
            ReceiveEpisodeId = descriptor.ReceiveEpisodeId,
            StreamIds = descriptor.StreamIds.ToList(),
            RetentionDaysAtRecordTime = descriptor.RetentionDays,
            PlaybackValidated = true
        };
        EncryptionSnapshotSchemaAdapter.ApplyToMetadata(
            metadata,
            descriptor.Encryption,
            descriptor.Protocol);
        return metadata;
    }
}
