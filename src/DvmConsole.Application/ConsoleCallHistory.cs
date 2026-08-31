using DvmConsole.Core.Runtime;

namespace DvmConsole.Application;

public enum ConsoleCallDirection
{
    Receive,
    Transmit,
    Event
}

public sealed record ConsoleCallHistoryRecord(
    CallId Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    SystemId SystemId,
    string SystemName,
    ChannelId? ChannelId,
    string ChannelName,
    RadioMediaProtocol Protocol,
    uint SourceId,
    uint DestinationId,
    uint PrimaryStreamId,
    IReadOnlyList<uint> StreamIds,
    long? ReceiveEpisodeId,
    string Caller,
    ConsoleCallDirection Direction,
    RecordingEncryptionDescriptor Encryption,
    string EventSource,
    string EventMessage,
    string EventRid,
    string EventTalkgroup)
{
    public bool IsActive => Direction != ConsoleCallDirection.Event && EndedAt is null;
}

/// <summary>
/// Bounded application-owned operational history. Presentation-specific text,
/// filtering, and recording catalog attachment remain projections of these
/// protocol-neutral records.
/// </summary>
public sealed class ConsoleCallHistory
{
    public const int DefaultMaximumEntries = 5_000;
    public const int MaximumStreamsPerCall = 32;

    private readonly object sync = new();
    private readonly int maximumEntries;
    private readonly List<ConsoleCallHistoryRecord> entries = [];

    public ConsoleCallHistory(int maximumEntries = DefaultMaximumEntries)
    {
        if (maximumEntries < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        this.maximumEntries = maximumEntries;
    }

    public IReadOnlyList<ConsoleCallHistoryRecord> Snapshot
    {
        get
        {
            lock (sync)
                return entries.ToArray();
        }
    }

    public void Add(ConsoleCallHistoryRecord entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (sync)
        {
            int existing = entries.FindIndex(candidate => candidate.Id == entry.Id);
            if (existing >= 0)
                entries.RemoveAt(existing);
            int index = entries.FindIndex(candidate => candidate.StartedAt <= entry.StartedAt);
            entries.Insert(index < 0 ? entries.Count : index, entry);
            if (entries.Count > maximumEntries)
                entries.RemoveRange(maximumEntries, entries.Count - maximumEntries);
        }
    }

    public CallId? FindActiveReceive(
        string systemName,
        RadioMediaProtocol protocol,
        uint primaryStreamId,
        string? channelName = null,
        uint? destinationId = null,
        long? receiveEpisodeId = null)
    {
        lock (sync)
        {
            return entries.FirstOrDefault(candidate =>
                candidate.IsActive &&
                candidate.Direction == ConsoleCallDirection.Receive &&
                candidate.PrimaryStreamId == primaryStreamId &&
                (receiveEpisodeId is null || candidate.ReceiveEpisodeId == receiveEpisodeId) &&
                candidate.Protocol == protocol &&
                (channelName is null || candidate.ChannelName.Equals(channelName, StringComparison.OrdinalIgnoreCase)) &&
                (destinationId is null || candidate.DestinationId == destinationId) &&
                candidate.SystemName.Equals(systemName, StringComparison.OrdinalIgnoreCase))?.Id;
        }
    }

    public CallId? FindActiveTransmit(
        string systemName,
        RadioMediaProtocol protocol,
        uint primaryStreamId,
        string? channelName = null,
        uint? destinationId = null)
    {
        lock (sync)
        {
            return entries.FirstOrDefault(candidate =>
                candidate.IsActive &&
                candidate.Direction == ConsoleCallDirection.Transmit &&
                candidate.PrimaryStreamId == primaryStreamId &&
                candidate.Protocol == protocol &&
                (channelName is null || candidate.ChannelName.Equals(channelName, StringComparison.OrdinalIgnoreCase)) &&
                (destinationId is null || candidate.DestinationId == destinationId) &&
                candidate.SystemName.Equals(systemName, StringComparison.OrdinalIgnoreCase))?.Id;
        }
    }

    public bool ObserveStream(CallId callId, uint streamId)
    {
        if (streamId == 0)
            return false;

        lock (sync)
        {
            int index = entries.FindIndex(candidate => candidate.Id == callId);
            if (index < 0 || entries[index].StreamIds.Contains(streamId))
                return false;

            var streams = entries[index].StreamIds.ToList();
            if (streams.Count >= MaximumStreamsPerCall)
                streams.RemoveAt(Math.Min(1, streams.Count - 1));
            streams.Add(streamId);
            entries[index] = entries[index] with { StreamIds = streams };
            return true;
        }
    }

    public bool Complete(CallId callId, DateTimeOffset timestamp)
    {
        lock (sync)
        {
            int index = entries.FindIndex(candidate => candidate.Id == callId);
            if (index < 0 || entries[index].EndedAt is not null)
                return false;
            ConsoleCallHistoryRecord entry = entries[index];
            entries[index] = entry with
            {
                EndedAt = timestamp < entry.StartedAt ? entry.StartedAt : timestamp
            };
            return true;
        }
    }

    public bool UpdateEncryption(
        CallId callId,
        RecordingEncryptionDescriptor encryption)
    {
        lock (sync)
        {
            int index = entries.FindIndex(candidate => candidate.Id == callId);
            if (index < 0 || entries[index].Encryption == encryption)
                return false;
            entries[index] = entries[index] with { Encryption = encryption };
            return true;
        }
    }

    public bool Remove(CallId callId)
    {
        lock (sync)
        {
            int index = entries.FindIndex(candidate => candidate.Id == callId);
            if (index < 0)
                return false;
            entries.RemoveAt(index);
            return true;
        }
    }

    public void Clear()
    {
        lock (sync)
            entries.Clear();
    }
}
