// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Frozen;
using System.Collections.Immutable;
using DvmConsole.Core.Runtime;
using DvmConsole.Operations;

namespace DvmConsole.Application;

// Owns route admission and target selection over application-owned channel state.
// Hosts map the selected states to their presentation objects after decisions.
internal sealed class ReceiveRouteCoordinator
{
    private readonly IMonotonicTimeSource time;
    private readonly ReceiveRouteSnapshot snapshot;
    private readonly ReceiveRouteRuntime runtime;
    private readonly FrozenDictionary<ChannelRouteKey, ConsoleChannelState[]> presentationRoutes;
    private readonly FrozenDictionary<
        (RadioMediaProtocol Protocol, uint DestinationId),
        ConsoleChannelState[]> legacyPresentationRoutes;
    private readonly FrozenDictionary<
        (RadioMediaProtocol Protocol, uint DestinationId),
        ConsoleChannelState[][]> presentationResourceGroups;
    private readonly FrozenDictionary<
        (RadioMediaProtocol Protocol, uint DestinationId, byte Slot),
        ChannelRouteKey> operationRouteKeys;
    private readonly FrozenDictionary<ConsoleChannelState, ConsoleChannelState[]> singletonRoutes;
    private readonly HashSet<ConsoleChannelState> configuredChannels;
    private readonly ConsoleChannelState[] configuredChannelList;
    private readonly ChannelRouteKey[] configuredRouteKeys;

    public ReceiveRouteCoordinator(
        IReadOnlyDictionary<
            (RadioMediaProtocol Protocol, uint DestinationId),
            ConsoleChannelState[]> legacyRoutes,
        ConsoleReceiveRouteState? sharedState = null, IMonotonicTimeSource? time = null)
    {
        ArgumentNullException.ThrowIfNull(legacyRoutes);
        this.time = time ?? SystemReceiveWorkQueueScheduler.Instance;
        ConsoleChannelState[] channels = legacyRoutes.Values
            .SelectMany(route => route)
            .Distinct()
            .ToArray();
        snapshot = sharedState?.Snapshot ?? ReceiveRouteSnapshot.Create(
            version: 1,
            channels.Select(channel => channel.Identity));
        runtime = sharedState?.Runtime ?? new ReceiveRouteRuntime(snapshot);
        presentationRoutes = channels
            .GroupBy(channel => channel.Identity.RouteKey)
            .ToFrozenDictionary(group => group.Key, group => group.ToArray());
        legacyPresentationRoutes = legacyRoutes.ToFrozenDictionary(
            route => route.Key,
            route => route.Value.ToArray());
        presentationResourceGroups = legacyRoutes.ToFrozenDictionary(
            route => route.Key,
            route => route.Value
                .GroupBy(channel => channel.Identity.RouteKey)
                .Select(group => group.ToArray())
                .ToArray());
        operationRouteKeys = presentationRoutes.Keys.ToFrozenDictionary(
            routeKey => (
                ChannelProtocolMediaMapper.ToTrafficProtocol(routeKey.Protocol),
                routeKey.DestinationId,
                routeKey.Slot),
            routeKey => routeKey);
        singletonRoutes = channels.ToFrozenDictionary(
            channel => channel,
            channel => new[] { channel });
        configuredChannels = new HashSet<ConsoleChannelState>(
            channels,
            ReferenceEqualityComparer.Instance);
        configuredChannelList = channels;
        configuredRouteKeys = presentationRoutes.Keys.ToArray();
    }

    public ReceiveIngressRoutingDecision ObserveIngress(
        IRadioMediaFrame traffic,
        Func<ConsoleChannelState, uint, bool> isTrackingStream,
        DateTimeOffset? observedAt = null)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        ArgumentNullException.ThrowIfNull(isTrackingStream);

        if (RadioReceiveTrafficClassifier.IsTerminator(traffic))
            return ObserveTerminatorIngress(traffic, isTrackingStream, observedAt);
        if ((!RadioReceiveTrafficClassifier.CarriesVoicePayload(traffic) &&
             !RadioReceiveTrafficClassifier.IsDefinitiveStart(traffic) &&
             !RadioReceiveTrafficClassifier.IsDmrPrivacyHeader(traffic)) ||
            traffic.DestinationId == 0)
        {
            return ReceiveIngressRoutingDecision.Empty;
        }

        byte slot = traffic.Protocol == RadioMediaProtocol.Dmr
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

    public ConsoleChannelState[] ResolveTargets(
        IReadOnlyList<ConsoleChannelState> decodeChannels,
        IRadioMediaFrame traffic,
        ReceiveIngressRoutingDecision ingressDecision,
        Func<ConsoleChannelState, uint, bool> isTrackingStream)
    {
        return ResolveDispatchTargets(
            decodeChannels,
            includeRecordingChannels: false,
            traffic,
            ingressDecision,
            isTrackingStream).ToArray();
    }

    public ReceiveStateTargets ResolveDispatchTargets(
        IReadOnlyList<ConsoleChannelState> decodeChannels,
        bool includeRecordingChannels,
        IRadioMediaFrame traffic,
        ReceiveIngressRoutingDecision ingressDecision,
        Func<ConsoleChannelState, uint, bool> isTrackingStream)
        => ResolveDispatchTargets(new DecodeChannelSelection(decodeChannels),
            includeRecordingChannels, traffic, ingressDecision, isTrackingStream);

    public ReceiveStateTargets ResolveDispatchTargetsById(
        IReadOnlyList<ChannelId> decodeChannels,
        bool includeRecordingChannels,
        IRadioMediaFrame traffic,
        ReceiveIngressRoutingDecision ingressDecision,
        Func<ConsoleChannelState, uint, bool> isTrackingStream)
        => ResolveDispatchTargets(new DecodeChannelSelection(decodeChannels),
            includeRecordingChannels, traffic, ingressDecision, isTrackingStream);

    private ReceiveStateTargets ResolveDispatchTargets(
        DecodeChannelSelection decodeChannels,
        bool includeRecordingChannels,
        IRadioMediaFrame traffic,
        ReceiveIngressRoutingDecision ingressDecision,
        Func<ConsoleChannelState, uint, bool> isTrackingStream)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        ArgumentNullException.ThrowIfNull(isTrackingStream);

        if (RadioReceiveTrafficClassifier.IsTerminator(traffic))
        {
            return ResolveTerminatorDispatchTargets(
                decodeChannels,
                includeRecordingChannels,
                traffic,
                ingressDecision,
                isTrackingStream);
        }
        if (!RadioReceiveTrafficClassifier.CarriesVoicePayload(traffic) &&
            !RadioReceiveTrafficClassifier.IsDefinitiveStart(traffic) &&
            !RadioReceiveTrafficClassifier.IsDmrPrivacyHeader(traffic))
        {
            return ReceiveStateTargets.Empty;
        }
        if (traffic.DestinationId == 0)
            return ReceiveStateTargets.Empty;

        byte slot = traffic.Protocol == RadioMediaProtocol.Dmr
            ? traffic.Slot ?? 0
            : (byte)0;
        if (!operationRouteKeys.TryGetValue(
                (traffic.Protocol, traffic.DestinationId, slot),
                out ChannelRouteKey routeKey) ||
            snapshot.Resolve(routeKey).Count == 0 ||
            !presentationRoutes.TryGetValue(routeKey, out ConsoleChannelState[]? candidates) ||
            !ingressDecision.TryGet(routeKey, out ReceiveIngressRouteDecision reduced) ||
            !ShouldDeliver(reduced.Actions))
        {
            return ReceiveStateTargets.Empty;
        }

        for (int index = 0; index < candidates.Length; index++)
        {
            ConsoleChannelState candidate = candidates[index];
            if (!IsDecodeEnabled(candidate, decodeChannels, includeRecordingChannels))
                continue;
            return ReceiveStateTargets.One(candidate);
        }
        return ReceiveStateTargets.Empty;
    }

    public ConsoleChannelState[] ResolvePresentationCandidates(
        IReadOnlyList<ConsoleChannelState> systemChannels,
        IRadioMediaFrame traffic,
        ReceiveIngressRoutingDecision ingressDecision,
        Func<ConsoleChannelState, bool> isAudioActive,
        Func<ConsoleChannelState, bool> isPatchActive,
        Func<ConsoleChannelState, uint, bool> isTrackingStream)
    {
        ArgumentNullException.ThrowIfNull(systemChannels);
        ArgumentNullException.ThrowIfNull(traffic);
        ArgumentNullException.ThrowIfNull(isAudioActive);
        ArgumentNullException.ThrowIfNull(isPatchActive);
        ArgumentNullException.ThrowIfNull(isTrackingStream);

        if (RadioReceiveTrafficClassifier.IsTerminator(traffic))
        {
            return ResolvePresentationTerminatorCandidates(
                systemChannels,
                traffic,
                ingressDecision,
                isTrackingStream);
        }
        if (!presentationResourceGroups.TryGetValue(
                (traffic.Protocol, traffic.DestinationId),
                out ConsoleChannelState[][]? resourceGroups))
        {
            return [];
        }

        if (resourceGroups.Length == 1)
        {
            ConsoleChannelState owner = SelectPresentationOwner(
                resourceGroups[0],
                traffic,
                isAudioActive,
                isPatchActive);
            if (!ShouldPresent(owner, ingressDecision))
                return [];
            return singletonRoutes[owner];
        }

        var candidates = new ConsoleChannelState[resourceGroups.Length];
        for (int index = 0; index < resourceGroups.Length; index++)
        {
            ConsoleChannelState owner = SelectPresentationOwner(
                resourceGroups[index],
                traffic,
                isAudioActive,
                isPatchActive);
            // A DMR destination can contain multiple slot groups. Only the
            // group matching this packet has an operational route decision;
            // unmatched groups retain the legacy projection and reject the
            // packet in the channel receive-state projection.
            candidates[index] = owner;
        }
        return candidates;
    }

    private ReceiveIngressRoutingDecision ObserveTerminatorIngress(
        IRadioMediaFrame traffic,
        Func<ConsoleChannelState, uint, bool> isTrackingStream,
        DateTimeOffset? observedAt)
    {
        var observedRoutes = new HashSet<ChannelRouteKey>();
        ReceiveIngressRouteDecision? primary = null;
        List<ReceiveIngressRouteDecision>? additional = null;
        for (int index = 0; index < configuredChannelList.Length; index++)
        {
            ConsoleChannelState channel = configuredChannelList[index];
            if (!IsTrackedTerminatorTarget(channel, traffic, isTrackingStream))
                continue;

            ChannelRouteKey routeKey = channel.Identity.RouteKey;
            if (!observedRoutes.Add(routeKey))
                continue;

            ReceiveObservation observation = CreateObservation(traffic, routeKey, observedAt);
            bool wasActiveAtIngress = runtime.IsActive(routeKey, traffic.StreamId);
            bool hasLiveTombstone = runtime.HasLiveTombstone(
                routeKey,
                traffic.StreamId,
                observation.ObservedAt);
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

    private ReceiveStateTargets ResolveTerminatorDispatchTargets(
        DecodeChannelSelection decodeChannels,
        bool includeRecordingChannels,
        IRadioMediaFrame traffic,
        ReceiveIngressRoutingDecision ingressDecision,
        Func<ConsoleChannelState, uint, bool> isTrackingStream)
    {
        int targetCount = 0;
        for (int index = 0; index < configuredChannelList.Length; index++)
        {
            ConsoleChannelState candidate = configuredChannelList[index];
            if (!IsDecodeEnabled(candidate, decodeChannels, includeRecordingChannels))
                continue;
            if (IsTrackedTerminatorTarget(
                    candidate,
                    traffic,
                    isTrackingStream) &&
                ingressDecision.TryGet(
                    candidate.Identity.RouteKey,
                    out ReceiveIngressRouteDecision countedDecision) &&
                ShouldDeliver(countedDecision.Actions))
            {
                targetCount++;
            }
        }
        if (targetCount == 0)
            return ReceiveStateTargets.Empty;

        if (targetCount == 1)
        {
            for (int index = 0; index < configuredChannelList.Length; index++)
            {
                ConsoleChannelState candidate = configuredChannelList[index];
                if (IsDecodeEnabled(candidate, decodeChannels, includeRecordingChannels) &&
                    IsTrackedTerminatorTarget(candidate, traffic, isTrackingStream) &&
                    ingressDecision.TryGet(
                        candidate.Identity.RouteKey,
                        out ReceiveIngressRouteDecision decision) &&
                    ShouldDeliver(decision.Actions))
                {
                    return ReceiveStateTargets.One(candidate);
                }
            }
        }

        var targets = new ConsoleChannelState[targetCount];
        int targetIndex = 0;
        for (int index = 0; index < configuredChannelList.Length; index++)
        {
            ConsoleChannelState candidate = configuredChannelList[index];
            if (!IsDecodeEnabled(candidate, decodeChannels, includeRecordingChannels) ||
                !IsTrackedTerminatorTarget(candidate, traffic, isTrackingStream) ||
                !ingressDecision.TryGet(
                    candidate.Identity.RouteKey,
                    out ReceiveIngressRouteDecision replayedDecision) ||
                !ShouldDeliver(replayedDecision.Actions))
            {
                continue;
            }
            targets[targetIndex++] = candidate;
        }

        if (targetIndex == targets.Length)
            return ReceiveStateTargets.FromArray(targets);
        if (targetIndex == 0)
            return ReceiveStateTargets.Empty;
        Array.Resize(ref targets, targetIndex);
        return ReceiveStateTargets.FromArray(targets);
    }

    private ConsoleChannelState[] ResolvePresentationTerminatorCandidates(
        IReadOnlyList<ConsoleChannelState> systemChannels,
        IRadioMediaFrame traffic,
        ReceiveIngressRoutingDecision ingressDecision,
        Func<ConsoleChannelState, uint, bool> isTrackingStream)
    {
        legacyPresentationRoutes.TryGetValue(
            (traffic.Protocol, traffic.DestinationId),
            out ConsoleChannelState[]? routedChannels);
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
            var activeChannels = new ConsoleChannelState[activeCount];
            int activeIndex = 0;
            for (int index = 0; index < systemChannels.Count; index++)
            {
                ConsoleChannelState channel = systemChannels[index];
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
        var distinctCandidates = new HashSet<ConsoleChannelState>(
            ReferenceEqualityComparer.Instance);
        var candidates = new List<ConsoleChannelState>(routedChannels.Length + activeCount);
        for (int index = 0; index < routedChannels.Length; index++)
        {
            ConsoleChannelState channel = routedChannels[index];
            if (distinctCandidates.Add(channel))
                candidates.Add(channel);
        }
        for (int index = 0; index < systemChannels.Count; index++)
        {
            ConsoleChannelState channel = systemChannels[index];
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

    private static ConsoleChannelState SelectPresentationOwner(
        IReadOnlyList<ConsoleChannelState> candidates,
        IRadioMediaFrame traffic,
        Func<ConsoleChannelState, bool> isAudioActive,
        Func<ConsoleChannelState, bool> isPatchActive)
        => SelectOwner(
            candidates,
            traffic.StreamId,
            requireReceivingState: true,
            isAudioActive,
            isPatchActive);

    private static ConsoleChannelState SelectOwner(
        IReadOnlyList<ConsoleChannelState> candidates,
        uint streamId,
        bool requireReceivingState,
        Func<ConsoleChannelState, bool> isAudioActive,
        Func<ConsoleChannelState, bool> isPatchActive)
    {
        for (int index = 0; index < candidates.Count; index++)
        {
            ConsoleChannelState candidate = candidates[index];
            if (candidate.Runtime.StreamId == streamId &&
                (!requireReceivingState || candidate.Runtime.State == ChannelRuntimeState.Receiving))
            {
                return candidate;
            }
        }

        ConsoleChannelState? selected = FindFirst(candidates, isAudioActive) ??
            FindFirst(candidates, isPatchActive) ??
            FindFirst(candidates, static candidate => candidate.Operator.Snapshot.RecordingEnabled);
        if (selected is not null)
            return selected;
        return candidates[0];
    }

    private static ConsoleChannelState? FindFirst(
        IReadOnlyList<ConsoleChannelState> candidates,
        Func<ConsoleChannelState, bool> predicate)
    {
        for (int index = 0; index < candidates.Count; index++)
        {
            if (predicate(candidates[index]))
                return candidates[index];
        }
        return null;
    }

    private bool IsTrackedTerminatorTarget(
        ConsoleChannelState channel,
        IRadioMediaFrame traffic,
        Func<ConsoleChannelState, uint, bool> isTrackingStream)
    {
        ChannelDefinition definition = channel.Identity;
        return configuredChannels.Contains(channel) &&
               snapshot.Contains(definition.SessionId) &&
               ChannelProtocolMediaMapper.ToTrafficProtocol(definition.Protocol) == traffic.Protocol &&
               (definition.Protocol != ChannelProtocol.Dmr ||
                traffic.Slot == definition.Slot) &&
               (runtime.IsActive(definition.RouteKey, traffic.StreamId) ||
                isTrackingStream(channel, traffic.StreamId));
    }

    private static bool IsPresentationTerminatorTarget(
        ConsoleChannelState channel,
        IRadioMediaFrame traffic,
        ReceiveIngressRoutingDecision ingressDecision,
        Func<ConsoleChannelState, uint, bool> isTrackingStream)
        => isTrackingStream(channel, traffic.StreamId) ||
           (ingressDecision.TryGet(
                channel.Identity.RouteKey,
                out ReceiveIngressRouteDecision routeDecision) &&
            routeDecision.ActiveStreamIds.Contains(traffic.StreamId));

    private ReceiveObservation CreateObservation(
        IRadioMediaFrame traffic,
        ChannelRouteKey routeKey,
        DateTimeOffset? observedAt = null)
        => new(
            routeKey,
            traffic.SourceId,
            traffic.StreamId,
            traffic.PacketSequence,
            Classify(traffic),
            observedAt ?? DateTimeOffset.UnixEpoch +
                time.GetElapsedTime(0, (traffic as IRadioFrameIngressTiming)?.BoundaryTimestamp ?? time.GetTimestamp()));

    private static ReceiveSignalKind Classify(IRadioMediaFrame traffic)
    {
        if (RadioReceiveTrafficClassifier.IsTerminator(traffic))
            return ReceiveSignalKind.End;
        if (RadioReceiveTrafficClassifier.IsDefinitiveStart(traffic))
            return ReceiveSignalKind.Start;
        if (RadioReceiveTrafficClassifier.CarriesVoicePayload(traffic))
            return ReceiveSignalKind.Voice;
        return ReceiveSignalKind.Metadata;
    }

    private static bool ShouldDeliver(ReceiveAction actions)
        => actions.HasFlag(ReceiveAction.Deliver);

    private static bool ShouldPresent(
        ConsoleChannelState owner,
        ReceiveIngressRoutingDecision ingressDecision)
        => !ingressDecision.TryGet(
                owner.Identity.RouteKey,
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
        return decisions is null ? Array.Empty<ReceiveRouteProjectionDecision>() : decisions;
    }

    public bool IsActive(ChannelRouteKey routeKey, uint streamId)
        => runtime.IsActive(routeKey, streamId);

    internal ReceiveRouteProjectionDecision ObserveCompatibility(
        ConsoleChannelState channel,
        IRadioMediaFrame traffic,
        DateTimeOffset now)
    {
        ChannelRouteKey routeKey = channel.Identity.RouteKey;
        ReceiveRouteDecision decision = runtime.Observe(
            CreateObservation(traffic, routeKey, now),
            channel.Identity.SessionId);
        return ToProjectionDecision(routeKey, decision);
    }

    internal ReceiveRouteProjectionDecision AdvanceCompatibility(
        ConsoleChannelState channel,
        DateTimeOffset now)
    {
        ChannelRouteKey routeKey = channel.Identity.RouteKey;
        return ToProjectionDecision(
            routeKey,
            runtime.Advance(routeKey, now, channel.Identity.SessionId));
    }

    public ConsoleChannelState? ResolveProjectionTarget(
        ChannelRouteKey routeKey,
        uint streamId,
        Func<ConsoleChannelState, bool> isAudioActive,
        Func<ConsoleChannelState, bool> isPatchActive)
    {
        if (!presentationRoutes.TryGetValue(routeKey, out ConsoleChannelState[]? candidates) ||
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
                return decisions is null ? Array.Empty<ReceiveRouteProjectionDecision>() : decisions;
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

    // Packet dispatch only needs membership. Keep the existing snapshot instead
    // of copying and deduplicating every active channel for every packet.
    private readonly struct DecodeChannelSelection
    {
        private readonly IReadOnlyList<ConsoleChannelState>? channels;
        private readonly IReadOnlyList<ChannelId>? ids;

        public DecodeChannelSelection(IReadOnlyList<ConsoleChannelState> channels)
            => this.channels = channels ?? throw new ArgumentNullException(nameof(channels));

        public DecodeChannelSelection(IReadOnlyList<ChannelId> ids)
            => this.ids = ids ?? throw new ArgumentNullException(nameof(ids));

        public bool Contains(ConsoleChannelState target)
        {
            if (ids is not null)
            {
                for (int index = 0; index < ids.Count; index++)
                    if (ids[index] == target.Id)
                        return true;
            }
            else if (channels is not null)
            {
                for (int index = 0; index < channels.Count; index++)
                    if (ReferenceEquals(channels[index], target))
                        return true;
            }
            return false;
        }
    }

    private static bool IsDecodeEnabled(
        ConsoleChannelState channel,
        DecodeChannelSelection decodeChannels,
        bool includeRecordingChannels)
        => (includeRecordingChannels && channel.Operator.Snapshot.RecordingEnabled) ||
           decodeChannels.Contains(channel);
}
