// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Diagnostics;
using DvmConsole.Core.Runtime;
using DvmConsole.Media;
using DvmConsole.Operations;

namespace DvmConsole.Application;

internal interface IReceiveChannelTrafficPort
{
    void Projected(ChannelId channel, ChannelReceiveProjectionResult result);
    void IgnoredLate(ChannelId channel, uint streamId, DateTimeOffset now);
    void HistoryChanged();
    void NonCallTerminator(SystemId system);
    void Log(DateTimeOffset timestamp, string source, DebugLogSeverity severity, string message);
}

/// <summary>Projects captured ingress decisions and completes channel media transitions.</summary>
internal sealed class ReceiveChannelTrafficCoordinator(
    ReceiveIngressCoordinator ingress, IReceiveMediaState media,
    ReceiveMediaDispatchCoordinator dispatch, ReceiveEpisodeRetirement retirement,
    ReceiveCallHistoryCoordinator history, IReceiveChannelTrafficPort port)
{
    public void Process(ReceiveIngressSystem system, ReceiveIngressWorkItem work)
    {
        ReceiveIngressDecision decision = work.Decision;
        IRadioMediaFrame traffic = decision.Traffic;
        DateTimeOffset now = decision.ReceivedAt;
        long ingressTimestamp = decision.ReceivedTimestamp;
        ReceiveCallEpisodeSnapshot? episode = decision.EpisodeSnapshot;
        ReceiveStateTargets preEnqueuedAudioChannels = work.PreEnqueuedAudioChannels;
        ReceiveStateTargets preEnqueuedPatchChannels = work.PreEnqueuedPatchChannels;
        List<ConsoleChannelState> activeAudioChannels = [];
        List<ConsoleChannelState> activePatchSourceChannels = [];
        bool callHistoryChanged = false;
        bool matchedAnyChannel = false;
        EncryptionSnapshot protocolEncryption =
            EncryptionSnapshotResolver.TryResolve(traffic) ?? EncryptionSnapshot.Unknown;
        EncryptionSnapshot observedEncryption = protocolEncryption.IsKnown
            ? protocolEncryption
            : episode?.Encryption ?? EncryptionSnapshot.Unknown;
        foreach (ConsoleChannelState channel in ingress.ResolvePresentationCandidates(system, decision))
        {
            if (!decision.Routing.TryGet(
                    channel.Identity.RouteKey,
                    out ReceiveIngressRouteDecision routeDecision))
            {
                continue;
            }
            foreach (ReceiveRouteProjectionDecision preceding in routeDecision.PrecedingDecisions)
            {
                callHistoryChanged = ProjectReceiveLifecycleDecision(
                    channel,
                    preceding,
                    now) || callHistoryChanged;
            }
            callHistoryChanged = retirement.Advance(now) || callHistoryChanged;
            ChannelReceiveProjectionResult result = ChannelReceiveProjection.Apply(channel, system.Name, traffic, now, routeDecision);
            port.Projected(channel.Id, result);
            if (result.Decision is not { } applied || applied.Transition == ReceiveStreamTransition.None)
                continue;
            matchedAnyChannel = true;
            if (applied.Transition == ReceiveStreamTransition.IgnoredLate)
            {
                channel.Receive.RecordIgnoredLatePacket();
                port.IgnoredLate(channel.Id, traffic.StreamId, now);
                continue;
            }

            bool patchAlreadyEnqueued = preEnqueuedPatchChannels.Contains(channel);
            if (!patchAlreadyEnqueued && media.IsPatchActive(channel.Id))
                activePatchSourceChannels.Add(channel);

            if (applied.EndedStreamId is uint endedStreamId)
            {
                dispatch.StopPatchSource(channel.Id, endedStreamId);
                DateTimeOffset endedAt = applied.EndedAt ?? now;
                port.Log(
                    now,
                    system.Name,
                    DebugLogSeverity.Info,
                    $"RX physical stream ended on {channel.Identity.Name}: {traffic.Protocol.ToString().ToUpperInvariant()} " +
                    $"{traffic.SourceId}→{traffic.DestinationId}, stream {endedStreamId}.");
                ingress.EndPhysicalStream(
                    system.Name,
                    traffic.Protocol,
                    channel.Id,
                    endedStreamId,
                    endedAt,
                    RadioReceiveTrafficClassifier.IsTerminator(traffic)
                        ? ReceivePhysicalEndReason.ConfirmedTerminator
                        : ReceivePhysicalEndReason.Replaced);
            }
            callHistoryChanged = history.Observe(system, channel, decision, applied, observedEncryption) || callHistoryChanged;

            if (media.IsAudioActive(channel.Id))
                activeAudioChannels.Add(channel);
        }

        if (!matchedAnyChannel &&
            traffic.Protocol == RadioMediaProtocol.Dmr &&
            RadioReceiveTrafficClassifier.IsTerminator(traffic))
        {
            port.NonCallTerminator(system.Id);
        }

        foreach (ConsoleChannelState channel in activeAudioChannels)
        {
            if (preEnqueuedAudioChannels.Contains(channel))
            {
                if (!RadioReceiveTrafficClassifier.IsTerminator(traffic))
                    dispatch.MarkPlaybackActive(channel.Id, traffic.SourceId, traffic.StreamId);
                continue;
            }

            dispatch.EnqueueAudio(channel.Id, traffic, ingressTimestamp);
        }
        foreach (ConsoleChannelState channel in activePatchSourceChannels)
            dispatch.EnqueuePatch(channel.Id, traffic);
        if (callHistoryChanged)
            port.HistoryChanged();
    }

    public bool ProjectReceiveLifecycleDecision(
        ConsoleChannelState channel,
        ReceiveRouteProjectionDecision projection,
        DateTimeOffset now)
    {
        ChannelReceiveProjectionResult result = ChannelReceiveProjection.Advance(channel, projection, now);
        port.Projected(channel.Id, result);
        ReceiveStreamDecision applied = result.Decision!.Value;
        if (applied.Transition is not (
                ReceiveStreamTransition.GraceExpired or
                ReceiveStreamTransition.TerminationExpired) ||
            applied.EndedStreamId is not uint streamId)
        {
            return false;
        }

        DateTimeOffset endedAt = applied.EndedAt ?? now;
        dispatch.StopPatchSource(channel.Id, streamId);
        port.Log(
            now,
            channel.Runtime.Definition.SystemName,
            DebugLogSeverity.Info,
            applied.Transition == ReceiveStreamTransition.TerminationExpired
                ? $"RX physical stream ended on {channel.Identity.Name}: stream {streamId}."
                : $"RX physical stream timed out on {channel.Identity.Name}: stream {streamId}.");
        ingress.EndPhysicalStream(
            channel.Runtime.Definition.SystemName,
            ChannelProtocolMediaMapper.ToTrafficProtocol(channel.Runtime.Definition.Protocol),
            channel.Id,
            streamId,
            endedAt,
            applied.Transition == ReceiveStreamTransition.TerminationExpired
                ? ReceivePhysicalEndReason.ConfirmedTerminator
                : ReceivePhysicalEndReason.InactivityTimeout);
        return false;
    }

}
