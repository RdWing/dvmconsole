using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics;
using DvmConsole.Core.Runtime;
using DvmConsole.FneClient;
using DvmConsole.Operations;

namespace DvmConsole.Desktop;

// Immutable, owner-independent result of reducing one packet for one route.
// Presentation objects never escape through this boundary, so audio, patch,
// and delayed UI consumers can replay the same ingress decision without
// advancing receive lifecycle state a second time.
internal readonly record struct ReceiveRouteProjectionDecision(
    ChannelRouteKey RouteKey,
    ReceiveAction Actions,
    ReceiveStreamDecision StreamDecision,
    ImmutableHashSet<uint> ActiveStreamIds)
{
    public uint PrimaryStreamId => StreamDecision.ActiveStreamId ?? 0;
    public int StreamCount => ActiveStreamIds.Count;
}

internal readonly record struct ReceiveIngressRouteDecision(
    ReceiveRouteProjectionDecision PacketDecision,
    IReadOnlyList<ReceiveRouteProjectionDecision> PrecedingDecisions)
{
    public ChannelRouteKey RouteKey => PacketDecision.RouteKey;
    public uint PrimaryStreamId => PacketDecision.PrimaryStreamId;
    public int StreamCount => PacketDecision.StreamCount;
    public ReceiveAction Actions => PacketDecision.Actions;
    public ReceiveStreamDecision StreamDecision => PacketDecision.StreamDecision;
    public ImmutableHashSet<uint> ActiveStreamIds => PacketDecision.ActiveStreamIds;
}

// The common packet path has one route and therefore does not allocate a
// collection. Destinationless terminators may close multiple tracked routes;
// those rare additional decisions remain private so the envelope is immutable
// to consumers.
internal readonly struct ReceiveIngressRoutingDecision
{
    private readonly ReceiveIngressRouteDecision primary;
    private readonly ReceiveIngressRouteDecision[]? additional;

    private ReceiveIngressRoutingDecision(
        ReceiveIngressRouteDecision primary,
        ReceiveIngressRouteDecision[]? additional)
    {
        this.primary = primary;
        this.additional = additional;
        HasDecision = true;
    }

    public static ReceiveIngressRoutingDecision Empty => default;
    public bool HasDecision { get; }
    public int Count => HasDecision ? 1 + (additional?.Length ?? 0) : 0;

    public bool TryGet(
        ChannelRouteKey routeKey,
        out ReceiveIngressRouteDecision decision)
    {
        if (HasDecision && primary.RouteKey == routeKey)
        {
            decision = primary;
            return true;
        }

        if (additional is not null)
        {
            for (int index = 0; index < additional.Length; index++)
            {
                if (additional[index].RouteKey == routeKey)
                {
                    decision = additional[index];
                    return true;
                }
            }
        }

        decision = default;
        return false;
    }

    public static ReceiveIngressRoutingDecision Create(
        ReceiveIngressRouteDecision primary,
        IReadOnlyList<ReceiveIngressRouteDecision>? additional = null)
        => new(
            primary,
            additional is null || additional.Count == 0
                ? null
                : additional.ToArray());
}

// The sole receive-routing boundary that knows both immutable operational
// definitions and presentation channel objects. The snapshot/runtime own route
// and lifecycle decisions; this adapter maps the selected definition back to
// the existing ChannelViewModel facade used by audio, TAR, and patch services.
internal sealed class ReceiveRoutePresentationAdapter
{
    private readonly ReceiveRouteSnapshot snapshot;
    private readonly ReceiveRouteRuntime runtime;
    private readonly FrozenDictionary<ChannelRouteKey, ChannelViewModel[]> presentationRoutes;
    private readonly FrozenDictionary<
        (FneTrafficProtocol Protocol, uint DestinationId),
        ChannelViewModel[]> legacyPresentationRoutes;
    private readonly FrozenDictionary<
        (FneTrafficProtocol Protocol, uint DestinationId),
        ChannelViewModel[][]> presentationResourceGroups;
    private readonly FrozenDictionary<
        (FneTrafficProtocol Protocol, uint DestinationId, byte Slot),
        ChannelRouteKey> operationRouteKeys;
    private readonly FrozenDictionary<ChannelViewModel, ChannelViewModel[]> singletonRoutes;
    private readonly HashSet<ChannelViewModel> configuredChannels;
    private readonly ChannelViewModel[] configuredChannelList;
    private readonly ChannelRouteKey[] configuredRouteKeys;

    public ReceiveRoutePresentationAdapter(
        IReadOnlyDictionary<
            (FneTrafficProtocol Protocol, uint DestinationId),
            ChannelViewModel[]> legacyRoutes)
    {
        ArgumentNullException.ThrowIfNull(legacyRoutes);
        ChannelViewModel[] channels = legacyRoutes.Values
            .SelectMany(route => route)
            .Distinct()
            .ToArray();
        snapshot = ReceiveRouteSnapshot.Create(
            version: 1,
            channels.Select(channel => channel.SessionDefinition));
        runtime = new ReceiveRouteRuntime(snapshot);
        presentationRoutes = channels
            .GroupBy(channel => channel.SessionDefinition.RouteKey)
            .ToFrozenDictionary(group => group.Key, group => group.ToArray());
        legacyPresentationRoutes = legacyRoutes.ToFrozenDictionary(
            route => route.Key,
            route => route.Value.ToArray());
        presentationResourceGroups = legacyRoutes.ToFrozenDictionary(
            route => route.Key,
            route => route.Value
                .GroupBy(channel => channel.SessionDefinition.RouteKey)
                .Select(group => group.ToArray())
                .ToArray());
        operationRouteKeys = presentationRoutes.Keys.ToFrozenDictionary(
            routeKey => (
                FneTrafficProtocolMapper.FromChannelProtocol(routeKey.Protocol),
                routeKey.DestinationId,
                routeKey.Slot),
            routeKey => routeKey);
        singletonRoutes = channels.ToFrozenDictionary(
            channel => channel,
            channel => new[] { channel });
        configuredChannels = new HashSet<ChannelViewModel>(
            channels,
            ReferenceEqualityComparer.Instance);
        configuredChannelList = channels;
        configuredRouteKeys = presentationRoutes.Keys.ToArray();
    }

    public ReceiveIngressRoutingDecision ObserveIngress(
        FneTrafficFrame traffic,
        Func<ChannelViewModel, uint, bool> isTrackingStream,
        DateTimeOffset? observedAt = null)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        ArgumentNullException.ThrowIfNull(isTrackingStream);

        if (ReceiveTrafficClassifier.IsTerminator(traffic))
            return ObserveTerminatorIngress(traffic, isTrackingStream, observedAt);
        if ((!ReceiveTrafficClassifier.CarriesVoicePayload(traffic) &&
             !ReceiveTrafficClassifier.IsDefinitiveStart(traffic) &&
             !ReceiveTrafficClassifier.IsDmrPrivacyHeader(traffic)) ||
            traffic.DestinationId == 0)
        {
            return ReceiveIngressRoutingDecision.Empty;
        }

        byte slot = traffic.Protocol == FneTrafficProtocol.Dmr
            ? traffic.Slot ?? 0
            : (byte)0;
        if (!operationRouteKeys.TryGetValue(
                (traffic.Protocol, traffic.DestinationId, slot),
                out ChannelRouteKey routeKey) ||
            snapshot.Resolve(routeKey).Count == 0)
        {
            return ReceiveIngressRoutingDecision.Empty;
        }

        ReceiveObservation observation = CreateObservation(traffic, routeKey, observedAt);
        IReadOnlyList<ReceiveRouteProjectionDecision> preceding = AdvanceRoute(
            routeKey,
            observation.ObservedAt);
        ReceiveRouteDecision decision = runtime.Observe(observation);
        return ReceiveIngressRoutingDecision.Create(
            ToIngressDecision(routeKey, decision, preceding));
    }

    public ChannelViewModel[] ResolveTargets(
        IReadOnlyList<ChannelViewModel> decodeChannels,
        FneTrafficFrame traffic,
        ReceiveIngressRoutingDecision ingressDecision,
        Func<ChannelViewModel, uint, bool> isTrackingStream)
    {
        ArgumentNullException.ThrowIfNull(decodeChannels);
        ArgumentNullException.ThrowIfNull(traffic);
        ArgumentNullException.ThrowIfNull(isTrackingStream);

        if (ReceiveTrafficClassifier.IsTerminator(traffic))
        {
            return ResolveTerminatorTargets(
                decodeChannels,
                traffic,
                ingressDecision,
                isTrackingStream);
        }
        if (!ReceiveTrafficClassifier.CarriesVoicePayload(traffic) &&
            !ReceiveTrafficClassifier.IsDefinitiveStart(traffic) &&
            !ReceiveTrafficClassifier.IsDmrPrivacyHeader(traffic))
        {
            return [];
        }
        if (traffic.DestinationId == 0)
            return [];

        byte slot = traffic.Protocol == FneTrafficProtocol.Dmr
            ? traffic.Slot ?? 0
            : (byte)0;
        if (!operationRouteKeys.TryGetValue(
                (traffic.Protocol, traffic.DestinationId, slot),
                out ChannelRouteKey routeKey) ||
            snapshot.Resolve(routeKey).Count == 0 ||
            !presentationRoutes.TryGetValue(routeKey, out ChannelViewModel[]? candidates) ||
            !ingressDecision.TryGet(routeKey, out ReceiveIngressRouteDecision reduced) ||
            !ShouldDeliver(reduced.Actions))
        {
            return [];
        }

        for (int index = 0; index < candidates.Length; index++)
        {
            ChannelViewModel candidate = candidates[index];
            if (!ContainsReference(decodeChannels, candidate))
                continue;
            return [candidate];
        }
        return [];
    }

    public ChannelViewModel[] ResolvePresentationCandidates(
        IReadOnlyList<ChannelViewModel> systemChannels,
        FneTrafficFrame traffic,
        ReceiveIngressRoutingDecision ingressDecision,
        Func<ChannelViewModel, bool> isAudioActive,
        Func<ChannelViewModel, bool> isPatchActive,
        Func<ChannelViewModel, uint, bool> isTrackingStream)
    {
        ArgumentNullException.ThrowIfNull(systemChannels);
        ArgumentNullException.ThrowIfNull(traffic);
        ArgumentNullException.ThrowIfNull(isAudioActive);
        ArgumentNullException.ThrowIfNull(isPatchActive);
        ArgumentNullException.ThrowIfNull(isTrackingStream);

        if (ReceiveTrafficClassifier.IsTerminator(traffic))
        {
            return ResolvePresentationTerminatorCandidates(
                systemChannels,
                traffic,
                ingressDecision,
                isTrackingStream);
        }
        if (!presentationResourceGroups.TryGetValue(
                (traffic.Protocol, traffic.DestinationId),
                out ChannelViewModel[][]? resourceGroups))
        {
            return [];
        }

        if (resourceGroups.Length == 1)
        {
            ChannelViewModel owner = SelectPresentationOwner(
                resourceGroups[0],
                traffic,
                isAudioActive,
                isPatchActive);
            if (!ShouldPresent(owner, ingressDecision))
                return [];
            return singletonRoutes[owner];
        }

        var candidates = new ChannelViewModel[resourceGroups.Length];
        for (int index = 0; index < resourceGroups.Length; index++)
        {
            ChannelViewModel owner = SelectPresentationOwner(
                resourceGroups[index],
                traffic,
                isAudioActive,
                isPatchActive);
            // A DMR destination can contain multiple slot groups. Only the
            // group matching this packet has an operational route decision;
            // unmatched groups retain the legacy projection and reject the
            // packet in ChannelViewModel.ApplyTraffic.
            candidates[index] = owner;
        }
        return candidates;
    }

    private ReceiveIngressRoutingDecision ObserveTerminatorIngress(
        FneTrafficFrame traffic,
        Func<ChannelViewModel, uint, bool> isTrackingStream,
        DateTimeOffset? observedAt)
    {
        var observedRoutes = new HashSet<ChannelRouteKey>();
        ReceiveIngressRouteDecision? primary = null;
        List<ReceiveIngressRouteDecision>? additional = null;
        for (int index = 0; index < configuredChannelList.Length; index++)
        {
            ChannelViewModel channel = configuredChannelList[index];
            if (!IsTrackedTerminatorTarget(channel, traffic, isTrackingStream))
                continue;

            ChannelRouteKey routeKey = channel.SessionDefinition.RouteKey;
            if (!observedRoutes.Add(routeKey))
                continue;

            ReceiveObservation observation = CreateObservation(traffic, routeKey, observedAt);
            ChannelReceiveState stateBeforeAdvance = runtime.GetState(routeKey);
            bool wasActiveAtIngress = stateBeforeAdvance.StreamLifecycle.IsActive(
                traffic.StreamId);
            bool hasLiveTombstone =
                stateBeforeAdvance.StreamLifecycle.Tombstones.TryGetValue(
                    traffic.StreamId,
                    out DateTimeOffset tombstoneExpiresAt) &&
                tombstoneExpiresAt > observation.ObservedAt;
            IReadOnlyList<ReceiveRouteProjectionDecision> preceding = AdvanceRoute(
                routeKey,
                observation.ObservedAt);
            ReceiveRouteDecision decision = runtime.Observe(
                observation,
                preferredOwner: null,
                // A decoder can lead the route snapshot when presentation is
                // backlogged, so an otherwise unknown tracked terminator may
                // establish bounded pending state. Never revive a route that
                // this same ingress pass just expired, or a live tombstone.
                assumeStreamActive: !wasActiveAtIngress && !hasLiveTombstone);
            ReceiveIngressRouteDecision ingress = ToIngressDecision(
                routeKey,
                decision,
                preceding);
            if (primary is null)
                primary = ingress;
            else
                (additional ??= []).Add(ingress);
        }

        return primary is ReceiveIngressRouteDecision first
            ? ReceiveIngressRoutingDecision.Create(first, additional)
            : ReceiveIngressRoutingDecision.Empty;
    }

    private ChannelViewModel[] ResolveTerminatorTargets(
        IReadOnlyList<ChannelViewModel> decodeChannels,
        FneTrafficFrame traffic,
        ReceiveIngressRoutingDecision ingressDecision,
        Func<ChannelViewModel, uint, bool> isTrackingStream)
    {
        int targetCount = 0;
        for (int index = 0; index < decodeChannels.Count; index++)
        {
            if (IsTrackedTerminatorTarget(
                    decodeChannels[index],
                    traffic,
                    isTrackingStream) &&
                ingressDecision.TryGet(
                    decodeChannels[index].SessionDefinition.RouteKey,
                    out ReceiveIngressRouteDecision countedDecision) &&
                ShouldDeliver(countedDecision.Actions))
            {
                targetCount++;
            }
        }
        if (targetCount == 0)
            return [];

        var targets = new ChannelViewModel[targetCount];
        int targetIndex = 0;
        for (int index = 0; index < decodeChannels.Count; index++)
        {
            ChannelViewModel candidate = decodeChannels[index];
            if (!IsTrackedTerminatorTarget(candidate, traffic, isTrackingStream) ||
                !ingressDecision.TryGet(
                    candidate.SessionDefinition.RouteKey,
                    out ReceiveIngressRouteDecision replayedDecision) ||
                !ShouldDeliver(replayedDecision.Actions))
            {
                continue;
            }
            targets[targetIndex++] = candidate;
        }

        if (targetIndex == targets.Length)
            return targets;
        if (targetIndex == 0)
            return [];
        Array.Resize(ref targets, targetIndex);
        return targets;
    }

    private ChannelViewModel[] ResolvePresentationTerminatorCandidates(
        IReadOnlyList<ChannelViewModel> systemChannels,
        FneTrafficFrame traffic,
        ReceiveIngressRoutingDecision ingressDecision,
        Func<ChannelViewModel, uint, bool> isTrackingStream)
    {
        legacyPresentationRoutes.TryGetValue(
            (traffic.Protocol, traffic.DestinationId),
            out ChannelViewModel[]? routedChannels);
        routedChannels ??= [];

        int activeCount = 0;
        for (int index = 0; index < systemChannels.Count; index++)
        {
            if (IsPresentationTerminatorTarget(
                    systemChannels[index],
                    traffic,
                    ingressDecision,
                    isTrackingStream))
                activeCount++;
        }

        if (activeCount == 0)
            return routedChannels;

        if (routedChannels.Length == 0)
        {
            var activeChannels = new ChannelViewModel[activeCount];
            int activeIndex = 0;
            for (int index = 0; index < systemChannels.Count; index++)
            {
                ChannelViewModel channel = systemChannels[index];
                if (IsPresentationTerminatorTarget(
                        channel,
                        traffic,
                        ingressDecision,
                        isTrackingStream))
                    activeChannels[activeIndex++] = channel;
            }
            return activeChannels;
        }

        // Preserve the former Concat(...).Distinct() behavior when a routed
        // destination and tracked fallback channels are both present.
        var distinctCandidates = new HashSet<ChannelViewModel>(
            ReferenceEqualityComparer.Instance);
        var candidates = new List<ChannelViewModel>(routedChannels.Length + activeCount);
        for (int index = 0; index < routedChannels.Length; index++)
        {
            ChannelViewModel channel = routedChannels[index];
            if (distinctCandidates.Add(channel))
                candidates.Add(channel);
        }
        for (int index = 0; index < systemChannels.Count; index++)
        {
            ChannelViewModel channel = systemChannels[index];
            if (IsPresentationTerminatorTarget(
                    channel,
                    traffic,
                    ingressDecision,
                    isTrackingStream) &&
                distinctCandidates.Add(channel))
            {
                candidates.Add(channel);
            }
        }
        return candidates.ToArray();
    }

    private static ChannelViewModel SelectPresentationOwner(
        IReadOnlyList<ChannelViewModel> candidates,
        FneTrafficFrame traffic,
        Func<ChannelViewModel, bool> isAudioActive,
        Func<ChannelViewModel, bool> isPatchActive)
        => SelectOwner(
            candidates,
            traffic.StreamId,
            requireReceivingState: true,
            isAudioActive,
            isPatchActive);

    private static ChannelViewModel SelectOwner(
        IReadOnlyList<ChannelViewModel> candidates,
        uint streamId,
        bool requireReceivingState,
        Func<ChannelViewModel, bool> isAudioActive,
        Func<ChannelViewModel, bool> isPatchActive)
    {
        for (int index = 0; index < candidates.Count; index++)
        {
            ChannelViewModel candidate = candidates[index];
            if (candidate.StreamId == streamId &&
                (!requireReceivingState || candidate.State == ChannelRuntimeState.Receiving))
            {
                return candidate;
            }
        }

        ChannelViewModel? selected = FindFirst(candidates, isAudioActive) ??
            FindFirst(candidates, isPatchActive) ??
            FindFirst(candidates, static candidate => candidate.IsRecordingEnabled);
        if (selected is not null)
            return selected;
        return candidates[0];
    }

    private static ChannelViewModel? FindFirst(
        IReadOnlyList<ChannelViewModel> candidates,
        Func<ChannelViewModel, bool> predicate)
    {
        for (int index = 0; index < candidates.Count; index++)
        {
            if (predicate(candidates[index]))
                return candidates[index];
        }
        return null;
    }

    private bool IsTrackedTerminatorTarget(
        ChannelViewModel channel,
        FneTrafficFrame traffic,
        Func<ChannelViewModel, uint, bool> isTrackingStream)
    {
        ChannelDefinition definition = channel.SessionDefinition;
        return configuredChannels.Contains(channel) &&
               snapshot.Contains(definition.SessionId) &&
               definition.Protocol == FneTrafficProtocolMapper.ToChannelProtocol(traffic.Protocol) &&
               (definition.Protocol != ChannelProtocol.Dmr ||
                traffic.Slot == definition.Slot) &&
               (runtime.IsActive(definition.RouteKey, traffic.StreamId) ||
                isTrackingStream(channel, traffic.StreamId));
    }

    private static bool IsPresentationTerminatorTarget(
        ChannelViewModel channel,
        FneTrafficFrame traffic,
        ReceiveIngressRoutingDecision ingressDecision,
        Func<ChannelViewModel, uint, bool> isTrackingStream)
        => isTrackingStream(channel, traffic.StreamId) ||
           (ingressDecision.TryGet(
                channel.SessionDefinition.RouteKey,
                out ReceiveIngressRouteDecision routeDecision) &&
            routeDecision.ActiveStreamIds.Contains(traffic.StreamId));

    private ReceiveObservation CreateObservation(
        FneTrafficFrame traffic,
        ChannelRouteKey routeKey,
        DateTimeOffset? observedAt = null)
        => new(
            routeKey,
            traffic.SourceId,
            traffic.StreamId,
            traffic.PacketSequence,
            Classify(traffic),
            observedAt ?? DateTimeOffset.UnixEpoch +
                Stopwatch.GetElapsedTime(0, traffic.FneBoundaryTimestamp));

    private static ReceiveSignalKind Classify(FneTrafficFrame traffic)
    {
        if (ReceiveTrafficClassifier.IsTerminator(traffic))
            return ReceiveSignalKind.End;
        if (ReceiveTrafficClassifier.IsDefinitiveStart(traffic))
            return ReceiveSignalKind.Start;
        if (ReceiveTrafficClassifier.CarriesVoicePayload(traffic))
            return ReceiveSignalKind.Voice;
        return ReceiveSignalKind.Metadata;
    }

    private static bool ShouldDeliver(ReceiveAction actions)
        => actions.HasFlag(ReceiveAction.Deliver);

    private static bool ShouldPresent(
        ChannelViewModel owner,
        ReceiveIngressRoutingDecision ingressDecision)
        => !ingressDecision.TryGet(
                owner.SessionDefinition.RouteKey,
                out ReceiveIngressRouteDecision decision) ||
           decision.Actions.HasFlag(ReceiveAction.Present);

    public IReadOnlyList<ReceiveRouteProjectionDecision> Advance(DateTimeOffset now)
    {
        List<ReceiveRouteProjectionDecision>? decisions = null;
        for (int index = 0; index < configuredRouteKeys.Length; index++)
        {
            IReadOnlyList<ReceiveRouteProjectionDecision> routeDecisions = AdvanceRoute(
                configuredRouteKeys[index],
                now);
            if (routeDecisions.Count == 0)
                continue;
            decisions ??= [];
            decisions.AddRange(routeDecisions);
        }
        return decisions ?? [];
    }

    public bool IsActive(ChannelRouteKey routeKey, uint streamId)
        => runtime.IsActive(routeKey, streamId);

    internal ReceiveRouteProjectionDecision ObserveCompatibility(
        ChannelViewModel channel,
        FneTrafficFrame traffic,
        DateTimeOffset now)
    {
        ChannelRouteKey routeKey = channel.SessionDefinition.RouteKey;
        ReceiveRouteDecision decision = runtime.Observe(
            CreateObservation(traffic, routeKey, now),
            channel.SessionId);
        return ToProjectionDecision(routeKey, decision);
    }

    internal ReceiveRouteProjectionDecision AdvanceCompatibility(
        ChannelViewModel channel,
        DateTimeOffset now)
    {
        ChannelRouteKey routeKey = channel.SessionDefinition.RouteKey;
        return ToProjectionDecision(
            routeKey,
            runtime.Advance(routeKey, now, channel.SessionId));
    }

    public ChannelViewModel? ResolveProjectionTarget(
        ChannelRouteKey routeKey,
        uint streamId,
        Func<ChannelViewModel, bool> isAudioActive,
        Func<ChannelViewModel, bool> isPatchActive)
    {
        if (!presentationRoutes.TryGetValue(routeKey, out ChannelViewModel[]? candidates) ||
            candidates.Length == 0)
        {
            return null;
        }
        return SelectOwner(
            candidates,
            streamId,
            requireReceivingState: false,
            isAudioActive,
            isPatchActive);
    }

    private IReadOnlyList<ReceiveRouteProjectionDecision> AdvanceRoute(
        ChannelRouteKey routeKey,
        DateTimeOffset now)
    {
        List<ReceiveRouteProjectionDecision>? decisions = null;
        while (true)
        {
            ReceiveRouteDecision decision = runtime.Advance(routeKey, now);
            if (decision.StreamDecision.Transition == ReceiveStreamTransition.None)
                return decisions ?? [];
            decisions ??= [];
            decisions.Add(ToProjectionDecision(routeKey, decision));
        }
    }

    private static ReceiveIngressRouteDecision ToIngressDecision(
        ChannelRouteKey routeKey,
        ReceiveRouteDecision decision,
        IReadOnlyList<ReceiveRouteProjectionDecision> preceding)
        => new(ToProjectionDecision(routeKey, decision), preceding);

    private static ReceiveRouteProjectionDecision ToProjectionDecision(
        ChannelRouteKey routeKey,
        ReceiveRouteDecision decision)
        => new(
            routeKey,
            decision.Actions,
            decision.StreamDecision,
            decision.State.StreamIds);

    private static bool ContainsReference(
        IReadOnlyList<ChannelViewModel> channels,
        ChannelViewModel target)
    {
        for (int index = 0; index < channels.Count; index++)
        {
            if (ReferenceEquals(channels[index], target))
                return true;
        }
        return false;
    }
}
