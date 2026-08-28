using System.Globalization;
using System.Text.Json.Serialization;

namespace DvmConsole.Desktop;

public enum CallRecordingEncryptionState
{
    Unknown,
    Clear,
    Secure
}

// Portable catalog metadata embedded directly in Opus recordings.
// Encryption identifiers are descriptive only; key material is never stored.
public sealed class CallRecordingMetadata
{
    public const int CurrentSchemaVersion = 4;

    private List<uint> streamIds = [];
    public int SchemaVersion { get; set; } = 1;
    public string RecordingId { get; set; } = Guid.NewGuid().ToString("N");
    public string Direction { get; set; } = "RX";
    public string RecordingSourceType { get; set; } = "InboundRadio";
    public string Protocol { get; set; } = string.Empty;
    public DateTimeOffset UtcStartTime { get; set; }
    public DateTimeOffset UtcEndTime { get; set; }
    public long DurationMs { get; set; }
    [JsonIgnore]
    public string FilePath { get; set; } = string.Empty;

    [JsonIgnore]
    public string FileName { get; set; } = string.Empty;

    [JsonIgnore]
    public long FileSizeBytes { get; set; }
    public int SampleRate { get; set; }
    public int BitsPerSample { get; set; }
    public int ChannelCount { get; set; }
    public long OriginalSampleCount { get; set; }
    public long ActiveSampleCount { get; set; }
    public int PeakAmplitude { get; set; }
    public int TrimLeadMs { get; set; }
    public int TrimTailMs { get; set; }
    public string SystemName { get; set; } = string.Empty;
    public string ChannelName { get; set; } = string.Empty;
    public uint? TalkgroupId { get; set; }
    public uint? SubscriberId { get; set; }
    public string SubscriberAlias { get; set; } = string.Empty;
    public uint? StreamId { get; set; }
    public List<uint> StreamIds
    {
        get => streamIds;
        set => streamIds = value ?? [];
    }

    [JsonIgnore]
    public int StreamFragmentCount => StreamIds.Count > 0
        ? StreamIds.Distinct().Count()
        : StreamId is null ? 0 : 1;
    public bool IsEncrypted { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter<CallRecordingEncryptionState>))]
    public CallRecordingEncryptionState EncryptionState { get; set; }
    public byte? EncryptionAlgorithmId { get; set; }
    public string EncryptionAlgorithm { get; set; } = string.Empty;
    public ushort? EncryptionKeyIdValue { get; set; }
    public string? EncryptionKeyId { get; set; }
    public int? RetentionDaysAtRecordTime { get; set; }
    public bool PlaybackValidated { get; set; }

    internal void NormalizeCompatibilityFields()
    {
        if (SchemaVersion < CurrentSchemaVersion)
            return;
        EncryptionSnapshotSchemaAdapter.ApplyToMetadata(
            this,
            EncryptionSnapshotSchemaAdapter.FromMetadata(this),
            EncryptionPresentation.ParseProtocol(Protocol));
    }

    [JsonIgnore]
    public bool IsPlayable =>
        PlaybackValidated &&
        DurationMs > 0 &&
        FileSizeBytes > 0 &&
        ActiveSampleCount > 0 &&
        PeakAmplitude > 0 &&
        !string.IsNullOrWhiteSpace(FilePath);

    [JsonIgnore]
    public string TimestampText => UtcStartTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    [JsonIgnore]
    public string DurationText => CallDurationTextFormatter.Format(
        TimeSpan.FromMilliseconds(Math.Max(0, DurationMs)));

    [JsonIgnore]
    public string SummaryText => $"{SystemName} · {Protocol} · {TimestampText}";

    [JsonIgnore]
    public string DetailText => $"{Direction} · {RecordingSourceType} · {RouteText} · {AliasText} · {DurationText} · {AudioAnalysisText} · {FileName}";

    [JsonIgnore]
    public string AliasText => string.IsNullOrWhiteSpace(SubscriberAlias)
        ? string.Empty
        : $"alias {SubscriberAlias}";

    [JsonIgnore]
    public string TalkgroupText => TalkgroupId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    [JsonIgnore]
    public string SubscriberText => SubscriberId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    [JsonIgnore]
    public CallRecordingEncryptionState EffectiveEncryptionState =>
        EncryptionSnapshotSchemaAdapter.FromMetadata(this).State;

    [JsonIgnore]
    public bool IsEncryptionKnown => EffectiveEncryptionState != CallRecordingEncryptionState.Unknown;

    [JsonIgnore]
    public string EffectiveEncryptionAlgorithm => !string.IsNullOrWhiteSpace(EncryptionAlgorithm)
        ? EncryptionAlgorithm.Trim()
        : EncryptionPresentation.AlgorithmDisplayName(
            EncryptionPresentation.ParseProtocol(Protocol),
            EncryptionSnapshotSchemaAdapter.FromMetadata(this).AlgorithmId);

    [JsonIgnore]
    public string EncryptionText => EffectiveEncryptionState switch
    {
        CallRecordingEncryptionState.Unknown => "Unknown",
        CallRecordingEncryptionState.Clear => "Clear",
        _ when string.IsNullOrWhiteSpace(EffectiveEncryptionAlgorithm) => "Encrypted",
        _ when string.IsNullOrWhiteSpace(EncryptionKeyId) => EffectiveEncryptionAlgorithm,
        _ => $"{EffectiveEncryptionAlgorithm} / {EncryptionKeyId}"
    };

    [JsonIgnore]
    public string TechnicalDetailsText
    {
        get
        {
            string format = SampleRate > 0 && BitsPerSample > 0 && ChannelCount > 0
                ? $"{SampleRate:N0} Hz · {BitsPerSample}-bit · {ChannelCount} ch"
                : "format n/a";
            string size = FileSizeBytes >= 0
                ? $"{FileSizeBytes:N0} B"
                : "size n/a";
            return $"{AudioAnalysisText} · {format} · {size}";
        }
    }

    [JsonIgnore]
    public string AudioAnalysisText
    {
        get
        {
            string activity = OriginalSampleCount > 0
                ? $"activity {(ActiveSampleCount * 100d / OriginalSampleCount):0.0}%"
                : "activity n/a";
            string trim = TrimLeadMs == 0 && TrimTailMs == 0
                ? "no trim"
                : $"trim -{TrimLeadMs}/+{TrimTailMs} ms";
            return $"peak {PeakAmplitude} · {activity} · {trim}";
        }
    }

    [JsonIgnore]
    public string RouteText => SubscriberId is uint subscriberId
        ? $"RID {subscriberId} → TG {TalkgroupId}"
        : $"TG {TalkgroupId}";
}
