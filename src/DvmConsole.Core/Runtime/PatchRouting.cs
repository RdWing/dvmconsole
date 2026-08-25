namespace DvmConsole.Core.Runtime;

// Stable configured-channel identity used by patch routing. The router does not
// know about UI controls or protocol encoders; the host supplies those at the
// begin/end/audio callback boundary.
public sealed record PatchMemberAddress
{
    public PatchMemberAddress(
        string systemName,
        uint destinationId,
        string? channelName = null)
    {
        SystemName = string.IsNullOrWhiteSpace(systemName)
            ? throw new ArgumentException("A patch member system is required.", nameof(systemName))
            : systemName.Trim();
        if (destinationId == 0)
            throw new ArgumentOutOfRangeException(nameof(destinationId));
        DestinationId = destinationId;
        ChannelName = string.IsNullOrWhiteSpace(channelName) ? null : channelName.Trim();
    }

    public string SystemName { get; }
    public uint DestinationId { get; }
    public string? ChannelName { get; }
    public bool HasConfiguredChannelIdentity => ChannelName is not null;
    public string Key => ChannelName is null
        ? $"{SystemName.ToLowerInvariant()}|destination|{DestinationId}"
        : $"{SystemName.ToLowerInvariant()}|channel|{ChannelName.ToLowerInvariant()}";
}

// Protocol-independent patch membership and active-call state machine.
// Membership changes stop active target calls before the new membership is
// committed. Audio forwarding remains callback-driven so each host can choose
// the appropriate DMR, P25, or analog packetizer.
public sealed class PatchRoutingTable
{
    private static readonly TimeSpan LatePacketSuppressWindow = TimeSpan.FromSeconds(2);

    private readonly object sync = new();
    private readonly IPatchForwardingSink sink;
    private readonly TimeProvider timeProvider;
    private readonly PatchLoopSuppression loopSuppression;
    private readonly Dictionary<string, GroupState> groups = new(StringComparer.OrdinalIgnoreCase);
    private bool sourceIdPassthrough;
    private int membershipGeneration;

    public PatchRoutingTable(
        Func<PatchMemberAddress, uint, uint> beginCall,
        Action<PatchMemberAddress, uint, uint> endCall,
        Action<PatchMemberAddress, uint, ReadOnlyMemory<short>, uint> sendAudio,
        Func<PatchMemberAddress, uint> fallbackSourceId,
        TimeProvider? timeProvider = null)
        : this(
            new DelegatePatchForwardingSink(beginCall, endCall, sendAudio, fallbackSourceId),
            timeProvider)
    {
    }

    public PatchRoutingTable(
        IPatchForwardingSink sink,
        TimeProvider? timeProvider = null)
    {
        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        loopSuppression = new PatchLoopSuppression(this.timeProvider, LatePacketSuppressWindow);
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
        Dictionary<string, PatchGroupMembership> incoming = PatchMembershipPolicy.Normalize(
            memberships,
            oneWayModes);
        List<ForwardTarget> stops;
        HashSet<string> explicitlyReconfiguredSourceKeys = new(StringComparer.OrdinalIgnoreCase);

        lock (sync)
        {
            if (MembershipsEqual(incoming))
                return;

            membershipGeneration++;
            stops = [];
            foreach (string groupName in groups.Keys
                .Where(name => !incoming.ContainsKey(name) ||
                              !PatchMembershipPolicy.RoutingEqual(groups[name].Membership, incoming[name]))
                .ToArray())
            {
                if (incoming.TryGetValue(groupName, out PatchGroupMembership? replacement) &&
                    replacement.OneWay &&
                    replacement.Members.Count > 0 &&
                    groups[groupName].OneWay &&
                    groups[groupName].Members.Count > 0 &&
                    !string.Equals(
                        groups[groupName].Members[0].Key,
                        replacement.Members[0].Key,
                        StringComparison.OrdinalIgnoreCase))
                {
                    explicitlyReconfiguredSourceKeys.Add(replacement.Members[0].Key);
                }

                CollectAndClearStops(groups[groupName], stops);
                groups.Remove(groupName);
            }

            foreach ((string groupName, PatchGroupMembership membership) in incoming)
            {
                if (!groups.ContainsKey(groupName))
                    groups[groupName] = new GroupState(membership);
            }

            foreach (string sourceKey in explicitlyReconfiguredSourceKeys)
                loopSuppression.AllowReconfiguredSource(sourceKey);
        }

        EndTargets(stops);
    }

    public void HandleCallStart(PatchMemberAddress source, uint streamId, uint sourceId)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (streamId == 0)
            return;

        List<StartRequest> starts = [];
        List<ForwardTarget> stops = [];
        lock (sync)
        {
            if (loopSuppression.ShouldSuppressInbound(source, streamId, sourceId))
                return;

            DateTimeOffset now = timeProvider.GetUtcNow();
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
        if (streamId == 0 || samples.IsEmpty)
            return;

        List<StartRequest> starts = [];
        List<ForwardTarget> stops = [];
        lock (sync)
        {
            if (loopSuppression.ShouldSuppressInbound(source, streamId, sourceId))
                return;

            DateTimeOffset now = timeProvider.GetUtcNow();
            foreach (GroupState group in groups.Values.Where(group => IsEligibleSource(group, source)))
            {
                if (group.Source is null ||
                    (group.Source.SourceKey != source.Key || group.Source.StreamId != streamId) &&
                    IsSourceStale(group.Source, now))
                {
                    if (group.Source is not null)
                        CollectAndClearStops(group, stops);

                    group.Source = new ActiveSource(source.Key, streamId, sourceId, sourceId != 0, now);
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
            sink.SendAudio(target.Member, target.StreamId, samples, target.OutboundSourceId);
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
            return loopSuppression.ShouldSuppressInbound(member, streamId, sourceId: 0);
    }

    public bool IsForwardTargetActive(PatchMemberAddress member)
    {
        ArgumentNullException.ThrowIfNull(member);
        lock (sync)
            return loopSuppression.IsTargetActive(member);
    }

    // Releases router state after the host loses an outbound encoder or
    // transport session. The next source audio block can then establish a
    // fresh target instead of remaining attached to a dead session.
    public bool ReportTargetFailure(PatchMemberAddress member, uint streamId)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (streamId == 0)
            return false;

        List<ForwardTarget> removedTargets = [];
        lock (sync)
        {
            foreach (GroupState group in groups.Values)
            {
                if (!group.ActiveTargets.TryGetValue(member.Key, out ForwardTarget? target) ||
                    target.StreamId != streamId)
                {
                    continue;
                }

                group.ActiveTargets.Remove(member.Key);
                removedTargets.Add(target);
            }

            if (removedTargets.Count == 0)
                return false;

            foreach (ForwardTarget target in removedTargets)
            {
                loopSuppression.ReleaseTarget(
                    member,
                    streamId,
                    target.OutboundSourceId);
            }
        }

        return true;
    }

    public int CleanupStaleSources()
    {
        List<ForwardTarget> stops = [];
        int cleaned = 0;
        lock (sync)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
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
                : sink.GetFallbackSourceId(start.Member);
            if (outboundSourceId == 0)
                continue;

            uint streamId = sink.BeginCall(start.Member, outboundSourceId);
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
                    loopSuppression.ActivateTarget(
                        start.Member,
                        streamId,
                        outboundSourceId);
                    accepted = true;
                }
            }

            if (!accepted)
                sink.EndCall(start.Member, streamId, outboundSourceId);
        }
    }

    private void EndTargets(List<ForwardTarget> stops)
    {
        foreach (ForwardTarget target in stops)
            sink.EndCall(target.Member, target.StreamId, target.OutboundSourceId);
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
            loopSuppression.ReleaseTarget(
                target.Member,
                target.StreamId,
                target.OutboundSourceId);
        }

        group.ActiveTargets.Clear();
    }

    private bool MembershipsEqual(Dictionary<string, PatchGroupMembership> incoming)
    {
        if (groups.Count != incoming.Count)
            return false;

        foreach ((string name, PatchGroupMembership membership) in incoming)
        {
            if (!groups.TryGetValue(name, out GroupState? existing) ||
                !PatchMembershipPolicy.RoutingEqual(existing.Membership, membership))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsEligibleSource(GroupState group, PatchMemberAddress source)
        => PatchMembershipPolicy.IsEligibleSource(group.Members, group.OneWay, source);

    private static bool IsSourceStale(ActiveSource source, DateTimeOffset now)
        => now - source.LastActivityUtc > LatePacketSuppressWindow;

    private sealed class GroupState
    {
        public GroupState(PatchGroupMembership membership)
        {
            Membership = membership;
            GroupName = membership.GroupName;
            Members = membership.Members;
            OneWay = membership.OneWay;
        }

        public PatchGroupMembership Membership { get; }

        public string GroupName { get; }
        public IReadOnlyList<PatchMemberAddress> Members { get; }
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
