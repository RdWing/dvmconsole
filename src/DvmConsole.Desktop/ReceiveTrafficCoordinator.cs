// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Diagnostics;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Operations;

namespace DvmConsole.Desktop;

internal interface IReceiveTrafficPresentation
{
    bool InputsSuppressed { get; }
    bool IsTrackingStream(ChannelId channel, uint streamId);
    void RecordIngress(SystemViewModel system, FneTrafficFrame traffic);
    void Present(SystemViewModel system, SystemTrafficWorkItem workItem);
    void ProjectLifecycle(ChannelId channel, ReceiveRouteProjectionDecision decision, DateTimeOffset now);
    void Log(DateTimeOffset timestamp, string source, DebugLogSeverity severity, string message);
}

/// <summary>
/// Owns the route table and packet decisions captured at ingress. Media is
/// dispatched before presentation can coalesce; UI state is projected through
/// the presentation port. Existing route adapters retain channel identity and
/// allocation-free single-target dispatch across this desktop boundary.
/// </summary>
internal sealed class ReceiveTrafficCoordinator
{
    private readonly IReadOnlyList<SystemViewModel> systems;
    private readonly IReadOnlyDictionary<ChannelId, ChannelViewModel> channels;
    private readonly IReadOnlyDictionary<SystemViewModel, ReceiveAudioTrafficRouter> receiveTrafficRouters;
    private readonly ReceiveCallEpisodeTracker receiveCallEpisodes;
    private readonly IReceiveMediaState state;
    private readonly ReceiveMediaDispatchCoordinator dispatch;
    private readonly IReceiveTrafficPresentation presentation;
    private readonly TimeProvider clock;

    public ReceiveTrafficCoordinator(
        IReadOnlyList<SystemViewModel> systems,
        IReadOnlyDictionary<ChannelId, ChannelViewModel> channels,
        ReceiveCallEpisodeTracker episodes,
        IReceiveMediaState state,
        ReceiveMediaDispatchCoordinator dispatch,
        IReceiveTrafficPresentation presentation,
        TimeProvider? clock = null)
    {
        this.systems = systems;
        this.channels = channels;
        receiveCallEpisodes = episodes;
        this.state = state;
        this.dispatch = dispatch;
        this.presentation = presentation;
        this.clock = clock ?? TimeProvider.System;
        receiveTrafficRouters = systems.ToDictionary(
            system => system,
            system => new ReceiveAudioTrafficRouter(
                system.Channels.GroupBy(channel =>
                    (FneTrafficProtocolMapper.FromChannelProtocol(channel.Definition.Protocol), channel.Definition.DestinationId))
                .ToDictionary(group => group.Key, group => group.ToArray())));
    }

    public void HandleIngress(SystemViewModel system, RadioTrafficRecord ingress)
    {
        if (presentation.InputsSuppressed || state.IsDisposing)
            return;
        if (ingress.Traffic is not FneTrafficFrame traffic)
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
            : traffic.FneBoundaryTimestamp > 0
                ? traffic.FneBoundaryTimestamp
            : clock.GetTimestamp();
        ReceivePacketDecisionEnvelope decision = ObserveIngress(
            system,
            traffic,
            receivedAt,
            receivedTimestamp,
            ingress.CandidateChannels);
        // Connection statistics are thread-safe and account for every packet
        // before continuation-only UI projection is allowed to coalesce.
        presentation.RecordIngress(system, decision.Traffic);
        ReceiveDispatchTargets preEnqueuedAudioChannels = EnqueuePriorityReceiveAudio(
            system,
            decision);
        ReceiveDispatchTargets preEnqueuedPatchChannels = EnqueuePriorityPatchAudio(
            system,
            decision);
        var workItem = new SystemTrafficWorkItem(
            decision,
            preEnqueuedAudioChannels,
            preEnqueuedPatchChannels);
        presentation.Present(system, workItem);
    }

    public ReceivePacketDecisionEnvelope ObserveIngress(
        SystemViewModel system,
        FneTrafficFrame traffic,
        DateTimeOffset receivedAt,
        long receivedTimestamp,
        IReadOnlyList<ChannelId>? candidateChannelIds = null)
    {
        traffic = NormalizeP25CallIdentity(traffic);
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
                out ReceiveAudioTrafficRouter? router))
        {
            routing = router.ObserveIngress(
                traffic,
                (channel, streamId) =>
                    state.IsAudioTrackingStream(channel.Id, streamId) ||
                    presentation.IsTrackingStream(channel.Id, streamId) ||
                    state.IsPatchTrackingStream(channel.Id, streamId),
                receivedAt);
        }

        bool canCoalescePresentation =
            routing.IsContinuationOnly &&
            episodeObservation is not { EpisodeStarted: true } &&
            episodeObservation is not { StreamAdded: true } &&
            ReceiveTrafficClassifier.CarriesVoicePayload(traffic) &&
            EncryptionSnapshotResolver.TryResolve(traffic) is null;
        return new ReceivePacketDecisionEnvelope(
            traffic,
            receivedAt,
            receivedTimestamp,
            routing,
            episodeObservation,
            episodeSnapshot,
            canCoalescePresentation,
            candidateChannelIds);
    }

    public IReadOnlyList<ChannelViewModel> ResolvePresentationCandidates(
        SystemViewModel system,
        ReceivePacketDecisionEnvelope decision)
    {
        if (!receiveTrafficRouters.TryGetValue(system, out ReceiveAudioTrafficRouter? router))
            return [];

        IReadOnlyList<ChannelViewModel> candidates = router.ResolvePresentationCandidates(
            system.Channels,
            decision.Traffic,
            decision.Routing,
            channel => state.IsAudioActive(channel.Id),
            channel => state.IsPatchActive(channel.Id),
            (channel, streamId) => presentation.IsTrackingStream(channel.Id, streamId));
        if (decision.CandidateChannelIds is null)
            return candidates;

        var candidateIds = decision.CandidateChannelIds.ToHashSet();
        return candidates
            .Where(channel => candidateIds.Contains(new ChannelId(channel.SessionId)))
            .ToArray();
    }

    public void Advance(DateTimeOffset now)
    {
        foreach (SystemViewModel system in systems)
        {
            if (!receiveTrafficRouters.TryGetValue(
                    system,
                    out ReceiveAudioTrafficRouter? router))
            {
                continue;
            }

            foreach (ReceiveRouteProjectionDecision projection in
                     router.Advance(now))
            {
                uint streamId = projection.StreamDecision.EndedStreamId ??
                    projection.StreamDecision.ActiveStreamId ??
                    projection.PrimaryStreamId;
                ChannelViewModel? channel = router.ResolveProjectionTarget(
                    projection.RouteKey,
                    streamId,
                    channel => state.IsAudioActive(channel.Id),
                    channel => state.IsPatchActive(channel.Id));
                if (channel is not null)
                    presentation.ProjectLifecycle(channel.Id, projection, now);
            }
        }
    }

    private static FneTrafficFrame NormalizeP25CallIdentity(FneTrafficFrame traffic)
    {
        if (!P25DfsiFrameCodec.TryExtractCallIdentifiers(
                traffic,
                out uint sourceId,
                out uint destinationId) ||
            (sourceId == traffic.SourceId && destinationId == traffic.DestinationId))
        {
            return traffic;
        }

        var normalized = new FneTrafficFrame(
            traffic.Protocol,
            traffic.PeerId,
            sourceId,
            destinationId,
            traffic.Slot,
            traffic.CallType,
            traffic.FrameType,
            traffic.Subtype,
            traffic.PacketSequence,
            traffic.StreamId,
            traffic.Payload,
            traffic.FneBoundaryTimestamp,
            traffic.TransportIngressTimestamp);
        P25DfsiFrameCodec.ShareParsedVoiceLdu(traffic, normalized);
        return normalized;
    }

    private ReceiveDispatchTargets EnqueuePriorityReceiveAudio(
        SystemViewModel system,
        ReceivePacketDecisionEnvelope decision)
    {
        if (!receiveTrafficRouters.TryGetValue(
                system,
                out ReceiveAudioTrafficRouter? router))
        {
            return ReceiveDispatchTargets.Empty;
        }

        ReceiveDispatchTargets targets = router.ResolveDispatchTargetsById(
            state.ActiveAudioChannels,
            includeRecordingChannels: true,
            decision.Traffic,
            decision.Routing,
            (channel, streamId) =>
                state.IsAudioTrackingStream(channel.Id, streamId) ||
                presentation.IsTrackingStream(channel.Id, streamId));
        if (targets.Count == 0)
            return ReceiveDispatchTargets.Empty;

        ChannelViewModel[]? accepted = targets.Count > 1
            ? new ChannelViewModel[targets.Count]
            : null;
        int acceptedCount = 0;
        foreach (ChannelViewModel channel in targets)
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
            return ReceiveDispatchTargets.Empty;

        ChannelViewModel[] acceptedTargets = accepted!;
        Array.Resize(ref acceptedTargets, acceptedCount);
        return ReceiveDispatchTargets.FromArray(acceptedTargets);
    }

    private ReceiveDispatchTargets EnqueuePriorityPatchAudio(
        SystemViewModel system,
        ReceivePacketDecisionEnvelope decision)
    {
        if (!receiveTrafficRouters.TryGetValue(
                system,
                out ReceiveAudioTrafficRouter? router))
        {
            return ReceiveDispatchTargets.Empty;
        }

        ReceiveDispatchTargets targets = router.ResolveDispatchTargetsById(
            state.ActivePatchChannels,
            includeRecordingChannels: false,
            decision.Traffic,
            decision.Routing,
            (channel, streamId) => state.IsPatchTrackingStream(channel.Id, streamId));
        if (targets.Count == 0)
            return ReceiveDispatchTargets.Empty;

        ChannelViewModel[]? accepted = targets.Count > 1
            ? new ChannelViewModel[targets.Count]
            : null;
        int acceptedCount = 0;
        foreach (ChannelViewModel channel in targets)
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
            return ReceiveDispatchTargets.Empty;

        ChannelViewModel[] acceptedTargets = accepted!;
        Array.Resize(ref acceptedTargets, acceptedCount);
        return ReceiveDispatchTargets.FromArray(acceptedTargets);
    }

    public void EndPhysicalStream(
        string systemName, FneTrafficProtocol protocol, ChannelId channel, uint streamId,
        DateTimeOffset endedAt, ReceivePhysicalEndReason reason)
    {
        receiveCallEpisodes.ObservePhysicalEnd(systemName, protocol, streamId, endedAt, reason);
        TaskObservation.Observe(dispatch.FinalizeStreamAsync(channel, streamId, endedAt));
    }
}
