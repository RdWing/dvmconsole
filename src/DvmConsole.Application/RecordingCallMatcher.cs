// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>Capture identity used to join catalog media to an operational call.</summary>
public readonly record struct RecordingCallIdentity(DateTimeOffset StartedAt, string SystemName,
    string ChannelName, string Direction, string Protocol, uint? SourceId,
    uint? DestinationId, uint? StreamId, long? ReceiveEpisodeId, DateTimeOffset? EndedAt = null)
{
    public static RecordingCallIdentity FromCall(ConsoleCallHistoryRecord call)
        => new(call.StartedAt, call.SystemName, call.ChannelName,
            call.Direction switch { ConsoleCallDirection.Receive => "RX", ConsoleCallDirection.Transmit => "TX", _ => "EVENT" },
            call.Protocol.ToString(), call.SourceId, call.DestinationId, call.PrimaryStreamId, call.ReceiveEpisodeId, call.EndedAt);
}

public static class RecordingCallMatcher
{
    public static bool Matches(RecordingCallIdentity recording, RecordingCallIdentity call)
    {
        string direction = recording.Direction.Equals("TX", StringComparison.OrdinalIgnoreCase) ? "TX" : "RX";
        bool routeMatches = call.Direction == direction &&
            call.DestinationId == recording.DestinationId &&
            call.SystemName.Equals(recording.SystemName, StringComparison.OrdinalIgnoreCase) &&
            call.Protocol.Equals(recording.Protocol, StringComparison.OrdinalIgnoreCase) &&
            (recording.SourceId is null || call.SourceId == recording.SourceId);
        if (!routeMatches) return false;
        // An episode can span physical streams and duplicate channel views.
        if (recording.ReceiveEpisodeId is long episodeId)
            // Episode counters are session-local. A recording must still start
            // within this call's lifetime, allowing the existing clock tolerance.
            return call.ReceiveEpisodeId == episodeId &&
                (call.StartedAt - recording.StartedAt).TotalSeconds <= 5 &&
                (call.EndedAt is not { } ended || (recording.StartedAt - ended).TotalSeconds <= 5);
        return call.StreamId == recording.StreamId &&
            call.ChannelName.Equals(recording.ChannelName, StringComparison.OrdinalIgnoreCase) &&
            Math.Abs((call.StartedAt - recording.StartedAt).TotalSeconds) <= 5;
    }
}

/// <summary>Limits attachment searches to the same episode or legacy stream identity.</summary>
public sealed class RecordingCallIndex
{
    private readonly RecordingCallIndex<ConsoleCallHistoryRecord> index;

    public RecordingCallIndex(IEnumerable<ConsoleCallHistoryRecord> calls)
        => index = new(calls.Where(call => call.Direction != ConsoleCallDirection.Event), RecordingCallIdentity.FromCall);

    public CallId? FindBest(RecordingCallIdentity recording) => index.FindBest(recording)?.Id;
}

/// <summary>Indexes captured call identities while letting each host retain its own projection objects.</summary>
public sealed class RecordingCallIndex<T> where T : class
{
    private readonly record struct Key(string System, string Protocol, string Direction,
        uint? Destination, long? Episode, uint? Stream, string? Channel);
    private readonly Dictionary<Key, List<(T Call, RecordingCallIdentity Identity)>> candidates = [];

    public RecordingCallIndex(IEnumerable<T> calls, Func<T, RecordingCallIdentity> describe)
    {
        foreach (T call in calls)
        {
            var identity = describe(call);
            Add(For(identity, episode: false), call, identity);
            if (identity.ReceiveEpisodeId.HasValue) Add(For(identity, episode: true), call, identity);
        }
    }

    private void Add(Key key, T call, RecordingCallIdentity identity)
    {
        if (!candidates.TryGetValue(key, out var group)) candidates.Add(key, group = []);
        group.Add((call, identity));
    }

    public T? FindBest(RecordingCallIdentity recording) => FindBest(recording, out _);

    public T? FindBest(RecordingCallIdentity recording, out int candidateVisits)
    {
        candidateVisits = 0;
        if (!candidates.TryGetValue(For(recording, recording.ReceiveEpisodeId.HasValue), out var group)) return null;
        T? best = null;
        long closest = long.MaxValue;
        foreach (var candidate in group)
        {
            candidateVisits++;
            if (!RecordingCallMatcher.Matches(recording, candidate.Identity)) continue;
            long distance = Math.Abs((candidate.Identity.StartedAt - recording.StartedAt).Ticks);
            if (distance >= closest) continue;
            closest = distance;
            best = candidate.Call;
        }
        return best;
    }

    private static Key For(RecordingCallIdentity identity, bool episode)
        => new(identity.SystemName.ToUpperInvariant(), identity.Protocol.ToUpperInvariant(),
            identity.Direction.Equals("TX", StringComparison.OrdinalIgnoreCase) ? "TX" : "RX",
            identity.DestinationId, episode ? identity.ReceiveEpisodeId : null,
            episode ? null : identity.StreamId, episode ? null : identity.ChannelName.ToUpperInvariant());
}
