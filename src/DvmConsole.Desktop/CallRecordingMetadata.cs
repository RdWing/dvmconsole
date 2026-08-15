using System.Text.Json.Serialization;

namespace DvmConsole.Desktop;

/// <summary>
/// Portable metadata written beside each completed receive recording.
/// Encryption identifiers are descriptive only; key material is never stored.
/// </summary>
public sealed class CallRecordingMetadata
{
    public int SchemaVersion { get; set; } = 1;
    public string RecordingId { get; set; } = Guid.NewGuid().ToString("N");
    public string Direction { get; set; } = "RX";
    public string RecordingSourceType { get; set; } = "InboundRadio";
    public string Protocol { get; set; } = string.Empty;
    public DateTimeOffset UtcStartTime { get; set; }
    public DateTimeOffset UtcEndTime { get; set; }
    public long DurationMs { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public int SampleRate { get; set; }
    public int BitsPerSample { get; set; }
    public int ChannelCount { get; set; }
    public string SystemName { get; set; } = string.Empty;
    public string ChannelName { get; set; } = string.Empty;
    public uint? TalkgroupId { get; set; }
    public uint? SubscriberId { get; set; }
    public uint? StreamId { get; set; }
    public bool IsEncrypted { get; set; }
    public string EncryptionAlgorithm { get; set; } = string.Empty;
    public string? EncryptionKeyId { get; set; }
    public int? RetentionDaysAtRecordTime { get; set; }

    [JsonIgnore]
    public string SidecarPath => string.IsNullOrWhiteSpace(FilePath)
        ? string.Empty
        : Path.ChangeExtension(FilePath, ".json");

    [JsonIgnore]
    public string TimestampText => UtcStartTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    [JsonIgnore]
    public string DurationText => TimeSpan.FromMilliseconds(Math.Max(0, DurationMs)).ToString(
        DurationMs >= 3_600_000 ? "hh\\:mm\\:ss" : "mm\\:ss");

    [JsonIgnore]
    public string SummaryText => $"{SystemName} · {Protocol} · {TimestampText}";

    [JsonIgnore]
    public string DetailText => $"{RouteText} · {DurationText} · {FileName}";

    [JsonIgnore]
    public string RouteText => SubscriberId is uint subscriberId
        ? $"RID {subscriberId} → TG {TalkgroupId}"
        : $"TG {TalkgroupId}";
}
