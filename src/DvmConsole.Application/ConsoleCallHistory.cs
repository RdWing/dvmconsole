// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;

namespace DvmConsole.Application;

public sealed record ConsoleCallCompletion(ConsoleCallHistoryRecord Record, bool Removed);

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
    public bool HasRecording { get; init; }

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

    public int MaximumEntries => maximumEntries;

    public ConsoleCallHistoryRecord? Find(CallId id)
    {
        lock (sync)
            return entries.FirstOrDefault(entry => entry.Id == id);
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

    /// <summary>Records the admitted target rather than later presentation or operator changes.</summary>
    public ConsoleCallHistoryRecord BeginTransmit(DateTimeOffset timestamp, TransmitTarget target, uint streamId)
    {
        ArgumentNullException.ThrowIfNull(target);
        TransmitChannelDescriptor channel = target.Channel;
        bool secure = channel.Definition.IsEncrypted && channel.TransmitEncrypted;
        byte? algorithmId = null;
        ushort? keyId = null;
        if (secure && EncryptionProtocolLabels.TryParseConfiguredAlgorithm(channel.Definition,
                out byte algorithm, out ushort key))
        {
            algorithmId = algorithm;
            keyId = key;
        }
        return BeginTransmit(timestamp, target.System.Name, channel.Name, target.System.SourceId ?? 0,
            channel.Definition.DestinationId, EncryptionProtocolLabels.ParseProtocol(channel.Definition.Mode),
            streamId, "Console", secure, algorithmId, keyId, channel.Id);
    }

    /// <summary>Starts a transmit record before any host projects it into a view.</summary>
    public ConsoleCallHistoryRecord BeginTransmit(
        DateTimeOffset timestamp,
        string systemName,
        string channelName,
        uint sourceId,
        uint destinationId,
        RadioMediaProtocol protocol,
        uint streamId,
        string? callerText = null,
        bool encrypted = false,
        byte? encryptionAlgorithmId = null,
        ushort? encryptionKeyId = null,
        ChannelId? channelId = null)
    {
        var record = new ConsoleCallHistoryRecord(
            CallId.New(), timestamp, null, SystemId.FromName(systemName), systemName,
            channelId, channelName, protocol, sourceId, destinationId, streamId,
            streamId == 0 ? Array.Empty<uint>() : Array.AsReadOnly(new[] { streamId }),
            null, string.IsNullOrWhiteSpace(callerText) ? sourceId.ToString() : callerText.Trim(),
            ConsoleCallDirection.Transmit,
            encrypted
                ? RecordingEncryptionDescriptor.Secure(encryptionAlgorithmId, encryptionKeyId)
                : RecordingEncryptionDescriptor.Clear,
            string.Empty, string.Empty, string.Empty, string.Empty);
        Add(record);
        return record;
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

    /// <summary>Completes one receive call and removes sub-frame shells without a recording.</summary>
    public ConsoleCallCompletion? CompleteReceive(
        string systemName, RadioMediaProtocol protocol, uint streamId, DateTimeOffset timestamp,
        string? channelName = null, uint? destinationId = null,
        long? receiveEpisodeId = null)
    {
        lock (sync)
        {
            CallId? active = FindActiveReceive(systemName, protocol, streamId, channelName, destinationId, receiveEpisodeId);
            if (active is not { } id) return null;
            Complete(id, timestamp);
            ConsoleCallHistoryRecord record = Find(id)!;
            bool removed = !record.HasRecording && record.EndedAt - record.StartedAt < TimeSpan.FromMilliseconds(50);
            if (removed) Remove(id);
            return new(record, removed);
        }
    }

    /// <summary>Associates finalized media with the closest retained call, without creating history.</summary>
    public CallId? AttachRecording(RecordingCallIdentity recording)
    {
        lock (sync)
        {
            int best = -1;
            long closest = long.MaxValue;
            for (int index = 0; index < entries.Count; index++)
            {
                var call = entries[index];
                if (call.Direction == ConsoleCallDirection.Event ||
                    !RecordingCallMatcher.Matches(recording, RecordingCallIdentity.FromCall(call))) continue;
                long distance = Math.Abs((call.StartedAt - recording.StartedAt).Ticks);
                if (distance >= closest) continue;
                best = index;
                closest = distance;
            }
            if (best < 0) return null;
            entries[best] = entries[best] with { HasRecording = true };
            return entries[best].Id;
        }
    }

    internal void ReconcileRecordings(IReadOnlyList<RecordingArchiveEntry> recordings, Func<bool> isCurrent)
    {
        lock (sync)
        {
            // Check under the History lock so a later finalization notification
            // cannot be overwritten by an older catalog read.
            if (!isCurrent()) return;
            var index = new RecordingCallIndex(entries);
            var attached = new HashSet<CallId>();
            foreach (var recording in recordings)
                if (recording.CallIdentity is { } identity && index.FindBest(identity) is { } id)
                    attached.Add(id);
            for (int position = 0; position < entries.Count; position++)
            {
                var call = entries[position];
                bool hasRecording = attached.Contains(call.Id);
                if (call.HasRecording != hasRecording)
                    entries[position] = call with { HasRecording = hasRecording };
            }
        }
    }

    /// <summary>Publishes catalog attachment before presentation updates; missing calls stay retired.</summary>
    public bool SetRecordingAttached(CallId callId, bool attached)
    {
        lock (sync)
        {
            int index = entries.FindIndex(candidate => candidate.Id == callId);
            if (index < 0 || entries[index].HasRecording == attached)
                return false;
            entries[index] = entries[index] with { HasRecording = attached };
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
