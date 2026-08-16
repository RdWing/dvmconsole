namespace DvmConsole.Core.Runtime;

// Stable system/talkgroup identity used by patch routing. The router does not
// know about UI controls or protocol encoders; the host supplies those at the
// begin/end/audio callback boundary.
public sealed record PatchMemberAddress
{
    public PatchMemberAddress(string systemName, uint destinationId)
    {
        SystemName = string.IsNullOrWhiteSpace(systemName)
            ? throw new ArgumentException("A patch member system is required.", nameof(systemName))
            : systemName.Trim();
        if (destinationId == 0)
            throw new ArgumentOutOfRangeException(nameof(destinationId));
        DestinationId = destinationId;
    }

    public string SystemName { get; }
    public uint DestinationId { get; }
    public string Key => $"{SystemName.ToLowerInvariant()}|{DestinationId}";
}

// Protocol-independent patch membership and active-call state machine.
// Membership changes stop active target calls before the new membership is
// committed. Audio forwarding remains callback-driven so each host can choose
// the appropriate DMR, P25, or analog packetizer.
public sealed class PatchRoutingTable
{
    private static readonly TimeSpan LatePacketSuppressWindow = TimeSpan.FromSeconds(2);

    private readonly object sync = new();
    private readonly Func<PatchMemberAddress, uint, uint> beginCall;
    private readonly Action<PatchMemberAddress, uint, uint> endCall;
    private readonly Action<PatchMemberAddress, uint, ReadOnlyMemory<short>, uint> sendAudio;
    private readonly Func<PatchMemberAddress, uint> fallbackSourceId;
    private readonly Dictionary<string, GroupState> groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> outboundStreams = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> recentlyEndedOutboundStreams = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> activeTargetKeys = new(StringComparer.OrdinalIgnoreCase);
    private bool sourceIdPassthrough;
    private int membershipGeneration;

    public PatchRoutingTable(
        Func<PatchMemberAddress, uint, uint> beginCall,
        Action<PatchMemberAddress, uint, uint> endCall,
        Action<PatchMemberAddress, uint, ReadOnlyMemory<short>, uint> sendAudio,
        Func<PatchMemberAddress, uint> fallbackSourceId)
    {
        this.beginCall = beginCall ?? throw new ArgumentNullException(nameof(beginCall));
        this.endCall = endCall ?? throw new ArgumentNullException(nameof(endCall));
        this.sendAudio = sendAudio ?? throw new ArgumentNullException(nameof(sendAudio));
        this.fallbackSourceId = fallbackSourceId ?? throw new ArgumentNullException(nameof(fallbackSourceId));
    }

    public bool SourceIdPassthrough
    {
        get
        {
            lock (sync)
                return sourceIdPassthrough;
        }
        set
        {
            lock (sync)
                sourceIdPassthrough = value;
        }
    }

    public IReadOnlyList<string> GroupNames
    {
        get
        {
            lock (sync)
                return groups.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public void ApplyMemberships(
        IReadOnlyDictionary<string, IReadOnlyList<PatchMemberAddress>> memberships,
        IReadOnlyDictionary<string, bool>? oneWayModes = null)
    {
        Dictionary<string, GroupState> incoming = NormalizeMemberships(memberships, oneWayModes);
        List<ForwardTarget> stops;

        lock (sync)
        {
            if (MembershipsEqual(incoming))
                return;

            membershipGeneration++;
            stops = [];
            foreach (string groupName in groups.Keys
                .Where(name => !incoming.ContainsKey(name) ||
                              !MembersEqual(groups[name].Members, incoming[name].Members) ||
                              groups[name].OneWay != incoming[name].OneWay)
                .ToArray())
            {
                CollectAndClearStops(groups[groupName], stops);
                groups.Remove(groupName);
            }

            foreach ((string groupName, GroupState state) in incoming)
            {
                if (!groups.ContainsKey(groupName))
                    groups[groupName] = state;
            }
        }

        EndTargets(stops);
    }

    public void HandleCallStart(PatchMemberAddress source, uint streamId, uint sourceId)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (streamId == 0 || IsPatchedTransmitStream(source, streamId))
            return;

        List<StartRequest> starts = [];
        List<ForwardTarget> stops = [];
        lock (sync)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (GroupState group in groups.Values.Where(group => IsEligibleSource(group, source)))
            {
                if (group.Source is not null)
                {
                    if (group.Source.SourceKey == source.Key && group.Source.StreamId == streamId)
                    {
                        group.Source.LastActivityUtc = now;
                        continue;
                    }

                    if (!IsSourceStale(group.Source, now))
                        continue;

                    CollectAndClearStops(group, stops);
                    group.Source = null;
                }

                group.Source = new ActiveSource(source.Key, streamId, sourceId, sourceId != 0, now);
                AddStartRequests(group, starts);
            }
        }

        EndTargets(stops);
        BeginTargets(starts);
    }

    public void HandleAudio(
        PatchMemberAddress source,
        uint streamId,
        uint sourceId,
        ReadOnlyMemory<short> samples)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (streamId == 0 || samples.IsEmpty || IsPatchedTransmitStream(source, streamId))
            return;

        List<StartRequest> starts = [];
        List<ForwardTarget> stops = [];
        lock (sync)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (GroupState group in groups.Values.Where(group => IsEligibleSource(group, source)))
            {
                if (group.Source is null ||
                    (group.Source.SourceKey != source.Key || group.Source.StreamId != streamId) &&
                    IsSourceStale(group.Source, now))
                {
                    if (group.Source is not null)
                        CollectAndClearStops(group, stops);

                    group.Source = new ActiveSource(source.Key, streamId, sourceId, sourceId != 0, now);
                    AddStartRequests(group, starts);
                }

                if (group.Source.SourceKey != source.Key || group.Source.StreamId != streamId)
                    continue;

                group.Source.LastActivityUtc = now;
                if (sourceIdPassthrough && !group.Source.SourceIdLatched && sourceId != 0)
                {
                    group.Source.SourceId = sourceId;
                    group.Source.SourceIdLatched = true;
                    foreach (ForwardTarget target in group.ActiveTargets.Values)
                        target.OutboundSourceId = sourceId;
                }

                if (!sourceIdPassthrough || group.Source.SourceIdLatched)
                    AddStartRequests(group, starts);
            }
        }

        EndTargets(stops);
        BeginTargets(starts);

        List<ForwardTarget> audioTargets = [];
        lock (sync)
        {
            foreach (GroupState group in groups.Values.Where(group =>
                         group.Source?.SourceKey == source.Key &&
                         group.Source.StreamId == streamId))
            {
                audioTargets.AddRange(group.ActiveTargets.Values);
            }
        }

        foreach (ForwardTarget target in audioTargets)
            sendAudio(target.Member, target.StreamId, samples, target.OutboundSourceId);
    }

    public void HandleCallEnd(PatchMemberAddress source, uint streamId)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (streamId == 0)
            return;

        List<ForwardTarget> stops = [];
        lock (sync)
        {
            foreach (GroupState group in groups.Values)
            {
                if (group.Source?.SourceKey != source.Key || group.Source.StreamId != streamId)
                    continue;

                CollectAndClearStops(group, stops);
                group.Source = null;
            }
        }

        EndTargets(stops);
    }

    public bool IsPatchedTransmitStream(PatchMemberAddress member, uint streamId)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (streamId == 0)
            return false;

        lock (sync)
        {
            CleanupExpiredSuppression();
            string key = BuildStreamKey(member, streamId);
            return outboundStreams.Contains(key) || recentlyEndedOutboundStreams.ContainsKey(key);
        }
    }

    public bool IsForwardTargetActive(PatchMemberAddress member)
    {
        ArgumentNullException.ThrowIfNull(member);
        lock (sync)
            return activeTargetKeys.Contains(member.Key);
    }

    public int CleanupStaleSources()
    {
        List<ForwardTarget> stops = [];
        int cleaned = 0;
        lock (sync)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (GroupState group in groups.Values.Where(group =>
                         group.Source is not null && IsSourceStale(group.Source, now)))
            {
                CollectAndClearStops(group, stops);
                group.Source = null;
                cleaned++;
            }
        }

        EndTargets(stops);
        return cleaned;
    }

    private void BeginTargets(List<StartRequest> starts)
    {
        foreach (StartRequest start in starts)
        {
            uint outboundSourceId = SourceIdPassthrough && start.SourceId != 0
                ? start.SourceId
                : fallbackSourceId(start.Member);
            if (outboundSourceId == 0)
                continue;

            uint streamId = beginCall(start.Member, outboundSourceId);
            if (streamId == 0)
                continue;

            bool accepted = false;
            lock (sync)
            {
                if (membershipGeneration == start.Generation &&
                    groups.TryGetValue(start.GroupName, out GroupState? group) &&
                    group.Source?.SourceKey == start.SourceKey &&
                    group.Source.StreamId == start.SourceStreamId &&
                    group.Members.Any(member => member.Key == start.Member.Key) &&
                    !group.ActiveTargets.ContainsKey(start.Member.Key))
                {
                    group.ActiveTargets[start.Member.Key] = new ForwardTarget(
                        start.Member,
                        streamId,
                        outboundSourceId);
                    outboundStreams.Add(BuildStreamKey(start.Member, streamId));
                    recentlyEndedOutboundStreams.Remove(BuildStreamKey(start.Member, streamId));
                    activeTargetKeys.Add(start.Member.Key);
                    accepted = true;
                }
            }

            if (!accepted)
                endCall(start.Member, streamId, outboundSourceId);
        }
    }

    private void EndTargets(List<ForwardTarget> stops)
    {
        foreach (ForwardTarget target in stops)
            endCall(target.Member, target.StreamId, target.OutboundSourceId);
    }

    private void AddStartRequests(GroupState group, List<StartRequest> starts)
    {
        if (sourceIdPassthrough && group.Source is { SourceIdLatched: false })
            return;

        foreach (PatchMemberAddress member in group.Members.Where(member =>
                     group.Source is not null &&
                     member.Key != group.Source.SourceKey &&
                     !group.ActiveTargets.ContainsKey(member.Key)))
        {
            starts.Add(new StartRequest(
                group.GroupName,
                membershipGeneration,
                group.Source!.SourceKey,
                group.Source.StreamId,
                member,
                group.Source.SourceId));
        }
    }

    private void CollectAndClearStops(GroupState group, List<ForwardTarget> stops)
    {
        foreach (ForwardTarget target in group.ActiveTargets.Values)
        {
            stops.Add(target);
            string streamKey = BuildStreamKey(target.Member, target.StreamId);
            outboundStreams.Remove(streamKey);
            recentlyEndedOutboundStreams[streamKey] = DateTimeOffset.UtcNow + LatePacketSuppressWindow;
            activeTargetKeys.Remove(target.Member.Key);
        }

        group.ActiveTargets.Clear();
    }

    private bool MembershipsEqual(Dictionary<string, GroupState> incoming)
    {
        if (groups.Count != incoming.Count)
            return false;

        foreach ((string name, GroupState state) in incoming)
        {
            if (!groups.TryGetValue(name, out GroupState? existing) ||
                existing.OneWay != state.OneWay ||
                !MembersEqual(existing.Members, state.Members))
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, GroupState> NormalizeMemberships(
        IReadOnlyDictionary<string, IReadOnlyList<PatchMemberAddress>> memberships,
        IReadOnlyDictionary<string, bool>? oneWayModes)
    {
        var normalized = new Dictionary<string, GroupState>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, IReadOnlyList<PatchMemberAddress> configuredMembers) in memberships ??
                 new Dictionary<string, IReadOnlyList<PatchMemberAddress>>())
        {
            string groupName = name?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(groupName))
                continue;

            List<PatchMemberAddress> members = (configuredMembers ?? [])
                .Where(member => member is not null)
                .GroupBy(member => member.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            if (members.Count == 0)
                continue;

            bool oneWay = oneWayModes is not null &&
                oneWayModes.TryGetValue(groupName, out bool configuredOneWay) &&
                configuredOneWay;
            normalized[groupName] = new GroupState(groupName, members, oneWay);
        }

        return normalized;
    }

    private static bool MembersEqual(IReadOnlyList<PatchMemberAddress> left, IReadOnlyList<PatchMemberAddress> right)
        => new HashSet<string>(left.Select(member => member.Key), StringComparer.OrdinalIgnoreCase)
            .SetEquals(right.Select(member => member.Key));

    private static bool IsEligibleSource(GroupState group, PatchMemberAddress source)
        => group.Members.Any(member => member.Key == source.Key) &&
           (!group.OneWay || group.Members[0].Key == source.Key);

    private static bool IsSourceStale(ActiveSource source, DateTimeOffset now)
        => now - source.LastActivityUtc > LatePacketSuppressWindow;

    private void CleanupExpiredSuppression()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (string key in recentlyEndedOutboundStreams
            .Where(entry => entry.Value <= now)
            .Select(entry => entry.Key)
            .ToArray())
        {
            recentlyEndedOutboundStreams.Remove(key);
        }
    }

    private static string BuildStreamKey(PatchMemberAddress member, uint streamId)
        => $"{member.Key}|{streamId}";

    private sealed class GroupState
    {
        public GroupState(string groupName, List<PatchMemberAddress> members, bool oneWay)
        {
            GroupName = groupName;
            Members = members;
            OneWay = oneWay;
        }

        public string GroupName { get; }
        public List<PatchMemberAddress> Members { get; }
        public bool OneWay { get; }
        public ActiveSource? Source { get; set; }
        public Dictionary<string, ForwardTarget> ActiveTargets { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ActiveSource
    {
        public ActiveSource(string sourceKey, uint streamId, uint sourceId, bool sourceIdLatched, DateTimeOffset lastActivityUtc)
        {
            SourceKey = sourceKey;
            StreamId = streamId;
            SourceId = sourceId;
            SourceIdLatched = sourceIdLatched;
            LastActivityUtc = lastActivityUtc;
        }

        public string SourceKey { get; }
        public uint StreamId { get; }
        public uint SourceId { get; set; }
        public bool SourceIdLatched { get; set; }
        public DateTimeOffset LastActivityUtc { get; set; }
    }

    private sealed class ForwardTarget
    {
        public ForwardTarget(PatchMemberAddress member, uint streamId, uint outboundSourceId)
        {
            Member = member;
            StreamId = streamId;
            OutboundSourceId = outboundSourceId;
        }

        public PatchMemberAddress Member { get; }
        public uint StreamId { get; }
        public uint OutboundSourceId { get; set; }
    }

    private sealed record StartRequest(
        string GroupName,
        int Generation,
        string SourceKey,
        uint SourceStreamId,
        PatchMemberAddress Member,
        uint SourceId);
}
