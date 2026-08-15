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
        Encrypted = encrypted;
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
    public bool Encrypted { get; }
    public string TimestampText => Timestamp.ToLocalTime().ToString("HH:mm:ss");
    public string ProtocolText => Protocol.ToString().ToUpperInvariant();
    public string RouteText => $"{CallerText} → TG {DestinationId}";
    public string StreamText => $"{ProtocolText} · stream {StreamId}";
    public bool IsActive => endTimestamp is null;
    public TimeSpan? Duration => endTimestamp - Timestamp;
    public string DurationText => Duration is TimeSpan duration
        ? $"{duration.TotalSeconds:0.0}s"
        : "Active";
    public string EncryptionText => Encrypted ? "Encrypted" : "Clear";

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

    public void Clear() => Entries.Clear();
}
