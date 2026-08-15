using System.Collections.ObjectModel;
using System.ComponentModel;
using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

/// <summary>
/// One inbound voice stream recorded by the dispatch shell.
/// </summary>
public sealed class CallHistoryEntry : INotifyPropertyChanged
{
    private DateTimeOffset? endTimestamp;
    private bool encrypted;
    private byte? encryptionAlgorithmId;
    private ushort? encryptionKeyId;

    public CallHistoryEntry(
        DateTimeOffset timestamp,
        string systemName,
        string channelName,
        uint sourceId,
        uint destinationId,
        FneTrafficProtocol protocol,
        uint streamId,
        string? callerText = null,
        bool encrypted = false)
    {
        Timestamp = timestamp;
        SystemName = systemName;
        ChannelName = channelName;
        SourceId = sourceId;
        DestinationId = destinationId;
        Protocol = protocol;
        StreamId = streamId;
        CallerText = string.IsNullOrWhiteSpace(callerText) ? sourceId.ToString() : callerText.Trim();
        this.encrypted = encrypted;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public DateTimeOffset Timestamp { get; }
    public DateTimeOffset? EndTimestamp => endTimestamp;
    public string SystemName { get; }
    public string ChannelName { get; }
    public uint SourceId { get; }
    public uint DestinationId { get; }
    public FneTrafficProtocol Protocol { get; }
    public uint StreamId { get; }
    public string CallerText { get; }
    public bool Encrypted => encrypted;
    public string TimestampText => Timestamp.ToLocalTime().ToString("HH:mm:ss");
    public string ProtocolText => Protocol.ToString().ToUpperInvariant();
    public string RouteText => $"{CallerText} → TG {DestinationId}";
    public string StreamText => $"{ProtocolText} · stream {StreamId}";
    public bool IsActive => endTimestamp is null;
    public TimeSpan? Duration => endTimestamp - Timestamp;
    public string DurationText => Duration is TimeSpan duration
        ? $"{duration.TotalSeconds:0.0}s"
        : "Active";
    public byte? EncryptionAlgorithmId => encryptionAlgorithmId;
    public ushort? EncryptionKeyId => encryptionKeyId;
    public string EncryptionText
    {
        get
        {
            if (!Encrypted)
                return "Clear";
            if (encryptionAlgorithmId is not byte algorithmId || encryptionKeyId is not ushort keyId)
                return "Encrypted";
            return $"Encrypted (alg 0x{algorithmId:X2}, key 0x{keyId:X})";
        }
    }

    public void Complete(DateTimeOffset timestamp)
    {
        if (endTimestamp is not null)
            return;
        endTimestamp = timestamp < Timestamp ? Timestamp : timestamp;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EndTimestamp)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Duration)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DurationText)));
    }

    public bool UpdateEncryption(bool value)
        => UpdateEncryption(value, null, null);

    public bool UpdateEncryption(bool value, byte? algorithmId, ushort? keyId)
    {
        if (encrypted == value &&
            encryptionAlgorithmId == algorithmId &&
            encryptionKeyId == keyId)
            return false;

        encrypted = value;
        encryptionAlgorithmId = value ? algorithmId : null;
        encryptionKeyId = value ? keyId : null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Encrypted)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncryptionAlgorithmId)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncryptionKeyId)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncryptionText)));
        return true;
    }
}

/// <summary>
/// Bounded newest-first call history for the Avalonia shell.
/// </summary>
public sealed class CallHistoryStore
{
    public const int DefaultMaxEntries = 100;

    private readonly int maxEntries;

    public CallHistoryStore(int maxEntries = DefaultMaxEntries)
    {
        if (maxEntries < 1)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        this.maxEntries = maxEntries;
    }

    public ObservableCollection<CallHistoryEntry> Entries { get; } = [];

    public void Add(CallHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Entries.Insert(0, entry);
        while (Entries.Count > maxEntries)
            Entries.RemoveAt(Entries.Count - 1);
    }

    public bool Complete(
        string systemName,
        FneTrafficProtocol protocol,
        uint streamId,
        DateTimeOffset timestamp)
    {
        CallHistoryEntry? entry = Entries.FirstOrDefault(candidate =>
            candidate.IsActive &&
            candidate.StreamId == streamId &&
            candidate.Protocol == protocol &&
            candidate.SystemName.Equals(systemName, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return false;
        entry.Complete(timestamp);
        return true;
    }

    public bool UpdateEncryption(
        string systemName,
        FneTrafficProtocol protocol,
        uint streamId,
        bool encrypted)
        => UpdateEncryption(systemName, protocol, streamId, encrypted, null, null);

    public bool UpdateEncryption(
        string systemName,
        FneTrafficProtocol protocol,
        uint streamId,
        bool encrypted,
        byte? algorithmId,
        ushort? keyId)
    {
        CallHistoryEntry? entry = Entries.FirstOrDefault(candidate =>
            candidate.IsActive &&
            candidate.StreamId == streamId &&
            candidate.Protocol == protocol &&
            candidate.SystemName.Equals(systemName, StringComparison.OrdinalIgnoreCase));
        return entry?.UpdateEncryption(encrypted, algorithmId, keyId) == true;
    }

    public void Clear() => Entries.Clear();
}
