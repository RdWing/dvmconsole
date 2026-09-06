// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

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
        Identity = new PatchMemberIdentity(
            SystemName.ToUpperInvariant(),
            ChannelName?.ToUpperInvariant(),
            ChannelName is null ? DestinationId : 0);
        Key = Identity.ToString();
    }

    public string SystemName { get; }
    public uint DestinationId { get; }
    public string? ChannelName { get; }
    public bool HasConfiguredChannelIdentity => ChannelName is not null;
    public string Key { get; }
    internal PatchMemberIdentity Identity { get; }
}

internal readonly record struct PatchMemberIdentity(
    string SystemName,
    string? ChannelName,
    uint DestinationId)
{
    public override string ToString()
        => ChannelName is null
            ? $"{SystemName}|DESTINATION|{DestinationId}"
            : $"{SystemName}|CHANNEL|{ChannelName}";
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
    private readonly Dictionary<SourceStreamKey, ForwardTarget[]> audioRouteSnapshots = [];
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

    public static PatchRoutingTable WithAdmission(
        Func<PatchMemberAddress, uint, PatchCallStartResult> beginCall,
        Action<PatchMemberAddress, uint, uint> endCall,
        Action<PatchMemberAddress, uint, ReadOnlyMemory<short>, uint> sendAudio,
        Func<PatchMemberAddress, uint> fallbackSourceId,
        TimeProvider? timeProvider = null)
        => new(new DelegatePatchForwardingSink(beginCall, endCall, sendAudio, fallbackSourceId), timeProvider);

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
        List<ForwardTarget>? stops = null;
        HashSet<PatchMemberIdentity> explicitlyReconfiguredSources = [];

        lock (sync)
        {
            if (MembershipsEqual(incoming))
                return;

            membershipGeneration++;
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
                    groups[groupName].Members[0].Identity != replacement.Members[0].Identity)
                {
                    explicitlyReconfiguredSources.Add(replacement.Members[0].Identity);
                }

                CollectAndClearStops(groups[groupName], ref stops);
                groups.Remove(groupName);
            }

            foreach ((string groupName, PatchGroupMembership membership) in incoming)
            {
                if (!groups.ContainsKey(groupName))
                    groups[groupName] = new GroupState(membership);
            }

            audioRouteSnapshots.Clear();
            foreach (PatchMemberIdentity source in explicitlyReconfiguredSources)
                loopSuppression.AllowReconfiguredSource(source);
        }

        EndTargets(stops);
    }

    public void HandleCallStart(PatchMemberAddress source, uint streamId, uint sourceId)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (streamId == 0)
            return;

        List<StartRequest>? starts = null;
        List<ForwardTarget>? stops = null;
        lock (sync)
        {
            if (loopSuppression.ShouldSuppressInbound(source, streamId, sourceId))
                return;

            DateTimeOffset now = timeProvider.GetUtcNow();
            foreach (GroupState group in groups.Values)
            {
                if (!IsEligibleSource(group, source))
                    continue;
                if (group.Source is not null)
                {
                    if (group.Source.Source == source.Identity && group.Source.StreamId == streamId)
                    {
                        group.Source.LastActivityUtc = now;
                        continue;
                    }

                    if (!IsSourceStale(group.Source, now))
                        continue;

                    CollectAndClearStops(group, ref stops);
                    group.Source = null;
                }

                group.Source = new ActiveSource(source.Identity, streamId, sourceId, sourceId != 0, now);
                AddStartRequests(group, ref starts);
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

        List<StartRequest>? starts = null;
        List<ForwardTarget>? stops = null;
        lock (sync)
        {
            if (loopSuppression.ShouldSuppressInbound(source, streamId, sourceId))
                return;

            DateTimeOffset now = timeProvider.GetUtcNow();
            foreach (GroupState group in groups.Values)
            {
                if (!IsEligibleSource(group, source))
                    continue;
                if (group.Source is null ||
                    (group.Source.Source != source.Identity || group.Source.StreamId != streamId) &&
                    IsSourceStale(group.Source, now))
                {
                    if (group.Source is not null)
                        CollectAndClearStops(group, ref stops);

                    group.Source = new ActiveSource(source.Identity, streamId, sourceId, sourceId != 0, now);
                }

                if (group.Source.Source != source.Identity || group.Source.StreamId != streamId)
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
                    AddStartRequests(group, ref starts);
            }
        }

        EndTargets(stops);
        BeginTargets(starts);

        ForwardTarget[] audioTargets;
        lock (sync)
            audioTargets = GetOrCreateAudioRouteSnapshot(source.Identity, streamId);

        foreach (ForwardTarget target in audioTargets)
            sink.SendAudio(target.Member, target.StreamId, samples, target.OutboundSourceId);
    }

    public void HandleCallEnd(PatchMemberAddress source, uint streamId)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (streamId == 0)
            return;

        List<ForwardTarget>? stops = null;
        lock (sync)
        {
            foreach (GroupState group in groups.Values)
            {
                if (group.Source?.Source != source.Identity || group.Source.StreamId != streamId)
                    continue;

                CollectAndClearStops(group, ref stops);
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
    public bool ReportTargetFailure(PatchMemberAddress member, uint streamId, bool skipSourceCall = false)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (streamId == 0)
            return false;

        List<ForwardTarget> removedTargets = [];
        lock (sync)
        {
            foreach (GroupState group in groups.Values)
            {
                if (!group.ActiveTargets.TryGetValue(member.Identity, out ForwardTarget? target) ||
                    target.StreamId != streamId)
                {
                    continue;
                }

                group.ActiveTargets.Remove(member.Identity);
                if (skipSourceCall)
                    group.Source?.SkipTarget(member.Identity);
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
            audioRouteSnapshots.Clear();
        }

        return true;
    }

    public int CleanupStaleSources()
    {
        List<ForwardTarget>? stops = null;
        int cleaned = 0;
        lock (sync)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            foreach (GroupState group in groups.Values.Where(group =>
                         group.Source is not null && IsSourceStale(group.Source, now)))
            {
                CollectAndClearStops(group, ref stops);
                group.Source = null;
                cleaned++;
            }
        }

        EndTargets(stops);
        return cleaned;
    }

    private void BeginTargets(List<StartRequest>? starts)
    {
        if (starts is null)
            return;
        foreach (StartRequest start in starts)
        {
            uint outboundSourceId = SourceIdPassthrough && start.SourceId != 0
                ? start.SourceId
                : sink.GetFallbackSourceId(start.Member);
            if (outboundSourceId == 0)
                continue;

            PatchCallStartResult admission = sink.TryBeginCall(start.Member, outboundSourceId);
            if (admission.SkipSourceCall)
            {
                lock (sync)
                {
                    if (membershipGeneration == start.Generation &&
                        groups.TryGetValue(start.GroupName, out GroupState? group) &&
                        group.Source?.Source == start.Source && group.Source.StreamId == start.SourceStreamId)
                        group.Source.SkipTarget(start.Member.Identity);
                }
                continue;
            }
            uint streamId = admission.StreamId;
            if (streamId == 0)
                continue;

            bool accepted = false;
            lock (sync)
            {
                if (membershipGeneration == start.Generation &&
                    groups.TryGetValue(start.GroupName, out GroupState? group) &&
                    group.Source?.Source == start.Source &&
                    group.Source.StreamId == start.SourceStreamId &&
                    ContainsMember(group.Members, start.Member.Identity) &&
                    !group.ActiveTargets.ContainsKey(start.Member.Identity))
                {
                    group.ActiveTargets[start.Member.Identity] = new ForwardTarget(
                        start.Member,
                        streamId,
                        outboundSourceId);
                    audioRouteSnapshots.Clear();
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

    private void EndTargets(List<ForwardTarget>? stops)
    {
        if (stops is null)
            return;
        foreach (ForwardTarget target in stops)
            sink.EndCall(target.Member, target.StreamId, target.OutboundSourceId);
    }

    private void AddStartRequests(GroupState group, ref List<StartRequest>? starts)
    {
        if (sourceIdPassthrough && group.Source is { SourceIdLatched: false })
            return;

        for (int index = 0; index < group.Members.Count; index++)
        {
            PatchMemberAddress member = group.Members[index];
            if (group.Source is null ||
                member.Identity == group.Source.Source ||
                group.ActiveTargets.ContainsKey(member.Identity) ||
                group.Source.IsTargetSkipped(member.Identity))
            {
                continue;
            }
            (starts ??= []).Add(new StartRequest(
                group.GroupName,
                membershipGeneration,
                group.Source.Source,
                group.Source.StreamId,
                member,
                group.Source.SourceId));
        }
    }

    private void CollectAndClearStops(GroupState group, ref List<ForwardTarget>? stops)
    {
        foreach (ForwardTarget target in group.ActiveTargets.Values)
        {
            (stops ??= []).Add(target);
            loopSuppression.ReleaseTarget(
                target.Member,
                target.StreamId,
                target.OutboundSourceId);
        }

        group.ActiveTargets.Clear();
        audioRouteSnapshots.Clear();
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

    private ForwardTarget[] GetOrCreateAudioRouteSnapshot(
        PatchMemberIdentity source,
        uint streamId)
    {
        var key = new SourceStreamKey(source, streamId);
        if (audioRouteSnapshots.TryGetValue(key, out ForwardTarget[]? existing))
            return existing;

        int count = 0;
        foreach (GroupState group in groups.Values)
        {
            if (group.Source?.Source == source && group.Source.StreamId == streamId)
                count += group.ActiveTargets.Count;
        }
        if (count == 0)
            return [];

        var snapshot = new ForwardTarget[count];
        int index = 0;
        foreach (GroupState group in groups.Values)
        {
            if (group.Source?.Source != source || group.Source.StreamId != streamId)
                continue;
            foreach (ForwardTarget target in group.ActiveTargets.Values)
                snapshot[index++] = target;
        }
        audioRouteSnapshots[key] = snapshot;
        return snapshot;
    }

    private static bool ContainsMember(
        IReadOnlyList<PatchMemberAddress> members,
        PatchMemberIdentity identity)
    {
        foreach (PatchMemberAddress member in members)
        {
            if (member.Identity == identity)
                return true;
        }
        return false;
    }

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
        public Dictionary<PatchMemberIdentity, ForwardTarget> ActiveTargets { get; } = [];
    }

    private sealed class ActiveSource
    {
        public ActiveSource(PatchMemberIdentity source, uint streamId, uint sourceId, bool sourceIdLatched, DateTimeOffset lastActivityUtc)
        {
            Source = source;
            StreamId = streamId;
            SourceId = sourceId;
            SourceIdLatched = sourceIdLatched;
            LastActivityUtc = lastActivityUtc;
        }

        public PatchMemberIdentity Source { get; }
        public uint StreamId { get; }
        public uint SourceId { get; set; }
        public bool SourceIdLatched { get; set; }
        private HashSet<PatchMemberIdentity>? skippedTargets;
        public void SkipTarget(PatchMemberIdentity member) => (skippedTargets ??= []).Add(member);
        public bool IsTargetSkipped(PatchMemberIdentity member) => skippedTargets?.Contains(member) == true;
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
        PatchMemberIdentity Source,
        uint SourceStreamId,
        PatchMemberAddress Member,
        uint SourceId);

    private readonly record struct SourceStreamKey(
        PatchMemberIdentity Source,
        uint StreamId);
}
