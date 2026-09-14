// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Diagnostics;
using DvmConsole.Core.Runtime;
using DvmConsole.Media;
using DvmConsole.Operations;
using DvmConsole.Threading;

namespace DvmConsole.Application;

internal interface IReceiveIngressPresentation
{
    void RecordIngress(ReceiveIngressSystem system, IRadioMediaFrame traffic);
    void Log(DateTimeOffset timestamp, string source, DebugLogSeverity severity, string message);
}

internal interface IReceiveIngressProjection
{
    void Apply(ReceiveIngressSystem system, ReceiveIngressWorkItem workItem);
    void Advance(ConsoleChannelState channel, ReceiveRouteProjectionDecision decision, DateTimeOffset now);
}

/// <summary>
/// Owns the route table and packet decisions captured at ingress. Media is
/// dispatched before presentation can coalesce; UI state is projected through
/// the presentation port. Application-owned routes retain channel identity and
/// allocation-free single-target dispatch independently of host presentation.
/// </summary>
internal sealed class ReceiveIngressCoordinator
{
    private readonly IReadOnlyList<ReceiveIngressSystem> systems;
    private readonly IReadOnlyDictionary<ReceiveIngressSystem, ReceiveRouteCoordinator> receiveTrafficRouters;
    private readonly ReceiveCallEpisodeTracker receiveCallEpisodes;
    private readonly IReceiveMediaState state;
    private readonly ReceiveMediaDispatchCoordinator dispatch;
    private readonly IReceiveIngressPresentation presentation;
    private readonly ConsoleSessionAdmission admission;
    private readonly ReceiveBufferingRuntime buffering;
    private readonly IReceiveIngressProjection projection;
    private readonly IRadioReceiveFrameNormalizer normalizer;
    private readonly IMonotonicTimeSource clock;

    public ReceiveIngressCoordinator(
        IReadOnlyList<ReceiveIngressSystem> systems,
        ReceiveCallEpisodeTracker episodes,
        IReceiveMediaState state,
        ReceiveMediaDispatchCoordinator dispatch,
        IReceiveIngressPresentation presentation,
        IReceiveIngressProjection projection,
        IRadioReceiveFrameNormalizer normalizer,
        ConsoleSessionAdmission admission,
        ReceiveBufferingRuntime buffering,
        IMonotonicTimeSource? clock = null,
        ConsoleSessionState? sharedState = null)
    {
        this.systems = systems;
        receiveCallEpisodes = episodes;
        this.state = state;
        this.dispatch = dispatch;
        this.presentation = presentation;
        this.admission = admission;
        this.buffering = buffering;
        this.projection = projection;
        this.normalizer = normalizer;
        this.clock = clock ?? SystemReceiveWorkQueueScheduler.Instance;
        receiveTrafficRouters = systems.ToDictionary(
            system => system,
            system => new ReceiveRouteCoordinator(
                system.Channels.GroupBy(channel =>
                    (ChannelProtocolMediaMapper.ToTrafficProtocol(channel.Runtime.Definition.Protocol), channel.Runtime.Definition.DestinationId))
                .ToDictionary(group => group.Key, group => group.ToArray()),
                sharedState?.ReceiveRoutes[SystemId.FromName(system.Name)], this.clock));
    }

    public void HandleIngress(ReceiveIngressSystem system, RadioTrafficRecord ingress)
    {
        if (admission.IsSuppressed || state.IsDisposing)
            return;
        IRadioMediaFrame? traffic = normalizer.Normalize(ingress.Traffic);
        if (traffic is null)
        {
            presentation.Log(
                ingress.ReceivedAt,
                system.Name,
                DebugLogSeverity.Warning,
                $"Ignored unsupported radio frame type {ingress.Traffic.GetType().Name}.");
            return;
        }

        DateTimeOffset receivedAt = ingress.ReceivedAt;
        long receivedTimestamp = ingress.BoundaryTimestamp > 0
            ? ingress.BoundaryTimestamp
            : (traffic as IRadioFrameIngressTiming)?.BoundaryTimestamp > 0
                ? ((IRadioFrameIngressTiming)traffic).BoundaryTimestamp
            : clock.GetTimestamp();
        ReceiveIngressDecision decision = ObserveNormalizedIngress(
            system,
            traffic,
            receivedAt,
            receivedTimestamp,
            ingress.CandidateChannels);
        buffering.Observe(system.Name, decision.Traffic);
        // Connection statistics are thread-safe and account for every packet
        // before continuation-only UI projection is allowed to coalesce.
        presentation.RecordIngress(system, decision.Traffic);
        ReceiveStateTargets preEnqueuedAudioChannels = EnqueuePriorityReceiveAudio(
            system,
            decision);
        ReceiveStateTargets preEnqueuedPatchChannels = EnqueuePriorityPatchAudio(
            system,
            decision);
        var workItem = new ReceiveIngressWorkItem(
            decision,
            preEnqueuedAudioChannels,
            preEnqueuedPatchChannels);
        projection.Apply(system, workItem);
    }

    public ReceiveIngressDecision ObserveIngress(
        ReceiveIngressSystem system,
        IRadioMediaFrame traffic,
        DateTimeOffset receivedAt,
        long receivedTimestamp,
        IReadOnlyList<ChannelId>? candidateChannelIds = null)
        => ObserveNormalizedIngress(system, normalizer.Normalize(traffic) ?? traffic,
            receivedAt, receivedTimestamp, candidateChannelIds);

    private ReceiveIngressDecision ObserveNormalizedIngress(
        ReceiveIngressSystem system, IRadioMediaFrame traffic, DateTimeOffset receivedAt,
        long receivedTimestamp, IReadOnlyList<ChannelId>? candidateChannelIds)
    {
        ReceiveCallEpisodeObservation? episodeObservation = receiveCallEpisodes.Observe(
            system.Name,
            traffic,
            receivedAt);
        ReceiveCallEpisodeSnapshot? episodeSnapshot = null;
        if (episodeObservation is not null &&
            receiveCallEpisodes.TryGet(
                system.Name,
                traffic.Protocol,
                traffic.StreamId,
                out ReceiveCallEpisodeSnapshot snapshot))
        {
            episodeSnapshot = snapshot;
        }

        ReceiveIngressRoutingDecision routing = ReceiveIngressRoutingDecision.Empty;
        if (receiveTrafficRouters.TryGetValue(
                system,
                out ReceiveRouteCoordinator? router))
        {
            routing = router.ObserveIngress(
                traffic,
                (channel, streamId) =>
                    state.IsAudioTrackingStream(channel.Id, streamId) ||
                    channel.Receive.IsTracking(streamId) ||
                    state.IsPatchTrackingStream(channel.Id, streamId),
                receivedAt);
        }

        bool canCoalescePresentation =
            routing.IsContinuationOnly &&
            episodeObservation is not { EpisodeStarted: true } &&
            episodeObservation is not { StreamAdded: true } &&
            RadioReceiveTrafficClassifier.CarriesVoicePayload(traffic) &&
            EncryptionSnapshotResolver.TryResolve(traffic) is null;
        return new ReceiveIngressDecision(
            traffic,
            receivedAt,
            receivedTimestamp,
            routing,
            episodeObservation,
            episodeSnapshot,
            canCoalescePresentation,
            candidateChannelIds);
    }

    public IReadOnlyList<ConsoleChannelState> ResolvePresentationCandidates(
        ReceiveIngressSystem system,
        ReceiveIngressDecision decision)
    {
        if (!receiveTrafficRouters.TryGetValue(system, out ReceiveRouteCoordinator? router))
            return [];

        IReadOnlyList<ConsoleChannelState> candidates = router.ResolvePresentationCandidates(
            system.Channels,
            decision.Traffic,
            decision.Routing,
            channel => state.IsAudioActive(channel.Id),
            channel => state.IsPatchActive(channel.Id),
            (channel, streamId) => channel.Receive.IsTracking(streamId));
        if (decision.CandidateChannelIds is null)
            return candidates;

        var candidateIds = decision.CandidateChannelIds.ToHashSet();
        return candidates
            .Where(channel => candidateIds.Contains(new ChannelId(channel.Identity.SessionId)))
            .ToArray();
    }

    public void Advance(DateTimeOffset now)
    {
        foreach (ReceiveIngressSystem system in systems)
        {
            if (!receiveTrafficRouters.TryGetValue(
                    system,
                    out ReceiveRouteCoordinator? router))
            {
                continue;
            }

            foreach (ReceiveRouteProjectionDecision projection in
                     router.Advance(now))
            {
                uint streamId = projection.StreamDecision.EndedStreamId ??
                    projection.StreamDecision.ActiveStreamId ??
                    projection.PrimaryStreamId;
                ConsoleChannelState? channel = router.ResolveProjectionTarget(
                    projection.RouteKey,
                    streamId,
                    channel => state.IsAudioActive(channel.Id),
                    channel => state.IsPatchActive(channel.Id));
                if (channel is not null)
                    this.projection.Advance(channel, projection, now);
            }
        }
    }

    private ReceiveStateTargets EnqueuePriorityReceiveAudio(
        ReceiveIngressSystem system,
        ReceiveIngressDecision decision)
    {
        if (!receiveTrafficRouters.TryGetValue(
                system,
                out ReceiveRouteCoordinator? router))
        {
            return ReceiveStateTargets.Empty;
        }

        ReceiveStateTargets targets = router.ResolveDispatchTargetsById(
            state.ActiveAudioChannels,
            includeRecordingChannels: true,
            decision.Traffic,
            decision.Routing,
            (channel, streamId) =>
                state.IsAudioTrackingStream(channel.Id, streamId) ||
                channel.Receive.IsTracking(streamId));
        if (targets.Count == 0)
            return ReceiveStateTargets.Empty;

        ConsoleChannelState[]? accepted = targets.Count > 1
            ? new ConsoleChannelState[targets.Count]
            : null;
        int acceptedCount = 0;
        foreach (ConsoleChannelState channel in targets)
        {
            if (dispatch.TryEnqueueAudio(
                    channel.Id,
                    decision.Traffic,
                    decision.ReceivedTimestamp))
            {
                if (accepted is not null)
                    accepted[acceptedCount] = channel;
                acceptedCount++;
                dispatch.PresentAcceptedAudio(channel.Id, decision.Traffic, markPlayback: false);
            }
        }

        if (acceptedCount == targets.Count)
            return targets;
        if (acceptedCount == 0)
            return ReceiveStateTargets.Empty;

        ConsoleChannelState[] acceptedTargets = accepted!;
        Array.Resize(ref acceptedTargets, acceptedCount);
        return ReceiveStateTargets.FromArray(acceptedTargets);
    }

    private ReceiveStateTargets EnqueuePriorityPatchAudio(
        ReceiveIngressSystem system,
        ReceiveIngressDecision decision)
    {
        if (!receiveTrafficRouters.TryGetValue(
                system,
                out ReceiveRouteCoordinator? router))
        {
            return ReceiveStateTargets.Empty;
        }

        ReceiveStateTargets targets = router.ResolveDispatchTargetsById(
            state.ActivePatchChannels,
            includeRecordingChannels: false,
            decision.Traffic,
            decision.Routing,
            (channel, streamId) => state.IsPatchTrackingStream(channel.Id, streamId));
        if (targets.Count == 0)
            return ReceiveStateTargets.Empty;

        ConsoleChannelState[]? accepted = targets.Count > 1
            ? new ConsoleChannelState[targets.Count]
            : null;
        int acceptedCount = 0;
        foreach (ConsoleChannelState channel in targets)
        {
            if (dispatch.EnqueuePatch(
                    channel.Id,
                    decision.Traffic,
                    decision.ReceivedTimestamp))
            {
                if (accepted is not null)
                    accepted[acceptedCount] = channel;
                acceptedCount++;
            }
        }

        if (acceptedCount == targets.Count)
            return targets;
        if (acceptedCount == 0)
            return ReceiveStateTargets.Empty;

        ConsoleChannelState[] acceptedTargets = accepted!;
        Array.Resize(ref acceptedTargets, acceptedCount);
        return ReceiveStateTargets.FromArray(acceptedTargets);
    }

    public void EndPhysicalStream(
        string systemName, RadioMediaProtocol protocol, ChannelId channel, uint streamId,
        DateTimeOffset endedAt, ReceivePhysicalEndReason reason)
    {
        receiveCallEpisodes.ObservePhysicalEnd(systemName, protocol, streamId, endedAt, reason);
        TaskObservation.Observe(dispatch.FinalizeStreamAsync(channel, streamId, endedAt));
    }
}
