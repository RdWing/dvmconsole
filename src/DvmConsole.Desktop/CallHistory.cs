using System.Collections.ObjectModel;
using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

/// <summary>
/// One inbound voice stream recorded by the dispatch shell.
/// </summary>
public sealed record CallHistoryEntry(
    DateTimeOffset Timestamp,
    string SystemName,
    string ChannelName,
    uint SourceId,
    uint DestinationId,
    FneTrafficProtocol Protocol,
    uint StreamId)
{
    public string TimestampText => Timestamp.ToLocalTime().ToString("HH:mm:ss");
    public string ProtocolText => Protocol.ToString().ToUpperInvariant();
    public string RouteText => $"SRC {SourceId} → TG {DestinationId}";
    public string StreamText => $"{ProtocolText} · stream {StreamId}";
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

    public void Clear() => Entries.Clear();
}
