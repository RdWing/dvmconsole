// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Threading;
using DvmConsole.Application;
using DvmConsole.Core.Diagnostics;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Operations;
using System.ComponentModel;
using System.Diagnostics;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel
{
    private void HandleSystemTraffic(SystemViewModel system, RadioTrafficRecord ingress)
        => receiveTraffic.HandleIngress(system, ingress);

    private void PresentSystemTraffic(
        SystemViewModel system,
        SystemTrafficWorkItem workItem,
        bool publishTrafficDiagnostics)
        => ProcessTrafficDecision(
            system,
            workItem.Decision,
            publishTrafficDiagnostics,
            workItem.PreEnqueuedAudioChannels,
            workItem.PreEnqueuedPatchChannels,
            trafficAlreadyRecorded: true);

    internal void ProcessTraffic(
        SystemViewModel system,
        FneTrafficFrame traffic,
        bool publishTrafficDiagnostics = true,
        DateTimeOffset? receivedAt = null,
        IReadOnlyList<ChannelViewModel>? preEnqueuedAudioChannels = null,
        long ingressTimestamp = 0,
        IReadOnlyList<ChannelViewModel>? preEnqueuedPatchChannels = null)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(traffic);
        DateTimeOffset now = receivedAt ?? DateTimeOffset.Now;
        ReceivePacketDecisionEnvelope decision = receiveTraffic.ObserveIngress(
            system,
            traffic,
            now,
            ingressTimestamp);
        ProcessTrafficDecision(
            system,
            decision,
            publishTrafficDiagnostics,
            ReceiveDispatchTargets.From(preEnqueuedAudioChannels),
            ReceiveDispatchTargets.From(preEnqueuedPatchChannels),
            trafficAlreadyRecorded: false);
    }

    private void ProcessTrafficDecision(
        SystemViewModel system,
        ReceivePacketDecisionEnvelope decision,
        bool publishTrafficDiagnostics,
        ReceiveDispatchTargets preEnqueuedAudioChannels,
        ReceiveDispatchTargets preEnqueuedPatchChannels,
        bool trafficAlreadyRecorded)
    {
        FneTrafficFrame traffic = decision.Traffic;
        DateTimeOffset now = decision.ReceivedAt;
        long ingressTimestamp = decision.ReceivedTimestamp;
        ReceiveCallEpisodeObservation? episodeObservation = decision.EpisodeObservation;
        uint historyStreamId = episodeObservation?.PrimaryStreamId ?? traffic.StreamId;
        ReceiveCallEpisodeSnapshot? episode = decision.EpisodeSnapshot;
        if (trafficAlreadyRecorded)
        {
            if (publishTrafficDiagnostics)
                system.PublishTrafficDiagnostics();
        }
        else
        {
            system.RecordTraffic(traffic, publishTrafficDiagnostics);
        }
        List<ChannelViewModel> activeAudioChannels = [];
        List<ChannelViewModel> activePatchSourceChannels = [];
        bool callHistoryChanged = false;
        bool matchedAnyChannel = false;
        EncryptionSnapshot protocolEncryption =
            EncryptionSnapshotResolver.TryResolve(traffic) ?? EncryptionSnapshot.Unknown;
        EncryptionSnapshot observedEncryption = protocolEncryption.IsKnown
            ? protocolEncryption
            : episode?.Encryption ?? EncryptionSnapshot.Unknown;
        bool? protocolEncrypted = observedEncryption.IsKnown
            ? observedEncryption.IsSecure
            : null;
        foreach (ChannelViewModel channel in receiveTraffic.ResolvePresentationCandidates(system, decision))
        {
            if (!decision.Routing.TryGet(
                    channel.SessionDefinition.RouteKey,
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
            callHistoryChanged = receiveEpisodeRetirement.Advance(now) || callHistoryChanged;
            ChannelTrafficApplyResult applied = channel.ApplyTraffic(
                system.Name,
                traffic,
                now,
                routeDecision);
            if (!applied.Matched)
                continue;
            matchedAnyChannel = true;
            if (applied.Transition == ReceiveStreamTransition.IgnoredLate)
            {
                channel.RecordIgnoredLatePacket();
                PublishReceiveDiagnostics(channel, traffic.StreamId, now);
                continue;
            }

            bool patchAlreadyEnqueued = preEnqueuedPatchChannels.Contains(channel);
            if (!patchAlreadyEnqueued && patchSourceDecode.IsActive(channel))
                activePatchSourceChannels.Add(channel);

            if (applied.EndedStreamId is uint endedStreamId)
            {
                receiveMediaDispatch.StopPatchSource(channel.Id, endedStreamId);
                DateTimeOffset endedAt = applied.EndedAt ?? now;
                AddDebugLog(
                    now,
                    system.Name,
                    DebugLogSeverity.Info,
                    $"RX physical stream ended on {channel.Name}: {traffic.Protocol.ToString().ToUpperInvariant()} " +
                    $"{traffic.SourceId}→{traffic.DestinationId}, stream {endedStreamId}.");
                receiveTraffic.EndPhysicalStream(
                    system.Name,
                    traffic.Protocol,
                    channel.Id,
                    endedStreamId,
                    endedAt,
                    ReceiveTrafficClassifier.IsTerminator(traffic)
                        ? ReceivePhysicalEndReason.ConfirmedTerminator
                        : ReceivePhysicalEndReason.Replaced);
            }
            bool canStartHistory = applied.Transition is
                ReceiveStreamTransition.Started or
                ReceiveStreamTransition.Restarted or
                ReceiveStreamTransition.Colliding or
                ReceiveStreamTransition.Continued or
                ReceiveStreamTransition.Resumed;
            if (canStartHistory &&
                traffic.SourceId != 0 &&
                !callHistory.HasActiveReceiveCall(
                    system.Name,
                    traffic.Protocol,
                    historyStreamId,
                    channel.Name,
                    traffic.DestinationId,
                    episode?.EpisodeId))
            {
                AddDebugLog(
                    now,
                    system.Name,
                    DebugLogSeverity.Info,
                    $"RX logical call episode started on {channel.Name}: " +
                    $"{traffic.Protocol.ToString().ToUpperInvariant()} {traffic.CallType}, " +
                    $"{traffic.SourceId}→{traffic.DestinationId}, episode {episode?.EpisodeId}, " +
                    $"primary physical stream {historyStreamId}" +
                    (protocolEncrypted is null
                        ? ", encryption unknown"
                        : protocolEncrypted.Value ? ", encrypted" : ", clear") +
                    $"{DescribeFneSignalQuality(traffic)}.");
                callHistory.Add(new CallHistoryEntry(
                    episode?.StartedAt ?? now,
                    system.Name,
                    channel.Name,
                    traffic.SourceId,
                    traffic.DestinationId,
                    traffic.Protocol,
                    historyStreamId,
                    channel.LastCallerText,
                    protocolEncrypted ?? false,
                    receiveEpisodeId: episode?.EpisodeId,
                    encryptionKnown: protocolEncrypted is not null,
                    channelId: new ChannelId(channel.SessionId)));
                callHistoryChanged = true;
            }

            if (episode is not null)
            {
                foreach (uint physicalStreamId in episode.StreamIds)
                {
                    callHistoryChanged = callHistory.ObserveReceiveStream(
                        system.Name,
                        traffic.Protocol,
                        historyStreamId,
                        physicalStreamId,
                        channel.Name,
                        traffic.DestinationId,
                        episode.EpisodeId) || callHistoryChanged;
                }
            }

            if (observedEncryption.IsKnown)
            {
                callHistoryChanged = callHistory.UpdateEncryption(
                    system.Name,
                    traffic.Protocol,
                    historyStreamId,
                    observedEncryption,
                    channel.Name,
                    traffic.DestinationId,
                    episode?.EpisodeId) || callHistoryChanged;
            }

            if (audioCoordinator.IsActive(channel))
                activeAudioChannels.Add(channel);
        }

        if (!matchedAnyChannel &&
            traffic.Protocol == FneTrafficProtocol.Dmr &&
            ReceiveTrafficClassifier.IsTerminator(traffic))
        {
            system.RecordNonCallDmrTerminator();
        }

        foreach (ChannelViewModel channel in activeAudioChannels)
        {
            if (preEnqueuedAudioChannels.Contains(channel))
            {
                if (!ReceiveTrafficClassifier.IsTerminator(traffic))
                    channel.MarkReceivePlaybackActive(traffic.SourceId, traffic.StreamId);
                continue;
            }

            receiveMediaDispatch.EnqueueAudio(channel.Id, traffic, ingressTimestamp);
        }
        foreach (ChannelViewModel channel in activePatchSourceChannels)
            receiveMediaDispatch.EnqueuePatch(channel.Id, traffic);
        if (callHistoryChanged)
            NotifyCallHistoryChanged();
    }

    private bool ProjectReceiveLifecycleDecision(
        ChannelViewModel channel,
        ReceiveRouteProjectionDecision projection,
        DateTimeOffset now)
    {
        ChannelTrafficApplyResult applied = channel.ProjectReceiveLifecycleDecision(
            projection,
            now);
        if (applied.Transition is not (
                ReceiveStreamTransition.GraceExpired or
                ReceiveStreamTransition.TerminationExpired) ||
            applied.EndedStreamId is not uint streamId)
        {
            return false;
        }

        DateTimeOffset endedAt = applied.EndedAt ?? now;
        receiveMediaDispatch.StopPatchSource(channel.Id, streamId);
        AddDebugLog(
            now,
            channel.Definition.SystemName,
            DebugLogSeverity.Info,
            applied.Transition == ReceiveStreamTransition.TerminationExpired
                ? $"RX physical stream ended on {channel.Name}: stream {streamId}."
                : $"RX physical stream timed out on {channel.Name}: stream {streamId}.");
        receiveTraffic.EndPhysicalStream(
            channel.Definition.SystemName,
            ProtocolFor(channel),
            channel.Id,
            streamId,
            endedAt,
            applied.Transition == ReceiveStreamTransition.TerminationExpired
                ? ReceivePhysicalEndReason.ConfirmedTerminator
                : ReceivePhysicalEndReason.InactivityTimeout);
        return false;
    }

    private bool ExpireStaleReceiveRoutes(DateTimeOffset now)
    {
        receiveTraffic.Advance(now);
        return false;
    }

    private static string DescribeFneSignalQuality(FneTrafficFrame traffic)
    {
        // dvmhost appends one aggregate FEC error count for all three DMR
        // AMBE frames, plus positive RSSI magnitude, after the 33-byte burst
        // (network offsets 53 and 54). The aggregate must not be assigned to
        // an individual 20 ms decoder slot. Zero means the source did not
        // report that measurement.
        if (traffic.Protocol != FneTrafficProtocol.Dmr ||
            traffic.Payload.Length < DmrVoicePacketCodec.PacketBytes)
        {
            return string.Empty;
        }

        byte errors = traffic.Payload[53];
        byte rssi = traffic.Payload[54];
        string errorText = errors == 0 ? string.Empty : $", FNE BER errors {errors}/141";
        string rssiText = rssi == 0 ? string.Empty : $", RSSI -{rssi} dBm";
        return errorText + rssiText;
    }

    private Task StartAudioAsync(ChannelViewModel channel)
        => receiveOutput.StartAsync(channel, persistSelection: false);

    private Task StartAudioAsync(ChannelViewModel channel, bool persistSelection)
        => receiveOutput.StartAsync(channel, persistSelection);

    internal async Task ReconcileReceiveSessionsAsync(CancellationToken cancellationToken = default)
        => await receiveOutput.ReconcileAsync(cancellationToken).ConfigureAwait(false);

    private Task StopAudioAsync(ChannelViewModel channel)
        => receiveOutput.StopAsync(channel, persistSelection: false);

    private Task StopAudioAsync(ChannelViewModel channel, bool persistSelection)
        => receiveOutput.StopAsync(channel, persistSelection);

    internal void SetReceiveSelectionPreference(ChannelViewModel channel, bool enabled)
        => receiveOutput.SetSelectionPreference(channel, enabled);

    internal async ValueTask SetChannelReceiveEnabledAsync(
        ChannelViewModel channel,
        bool enabled,
        CancellationToken cancellationToken = default)
        => await receiveOutput.SetEnabledAsync(channel, enabled, cancellationToken).ConfigureAwait(false);

    internal string? GetEffectiveOutputMuteReason(ChannelViewModel channel)
        => receiveOutput.GetEffectiveMuteReason(channel, OutputMuted);

    private async Task ToggleSelectedSystemOutputMuteAsync()
        => await receiveOutput.ToggleSystemMuteAsync(SelectedSystem).ConfigureAwait(false);

    private async Task ToggleSelectedZoneOutputMuteAsync()
        => await receiveOutput.ToggleZoneMuteAsync(SelectedSystem?.SelectedZone).ConfigureAwait(false);

    private void NotifySelectedOutputMutePresentationChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSystemOutputMuted)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedZoneOutputMuted)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSystemOutputMuteGlyph)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedZoneOutputMuteGlyph)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSystemOutputMuteToolTip)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedZoneOutputMuteToolTip)));
    }

    private async Task<ReceiveProcessingStageTiming> ProcessAudioAsync(
        ChannelViewModel channel,
        FneTrafficFrame traffic,
        RadioFrameEncryption? encryption,
        CancellationToken cancellationToken)
    {
        ReceiveProcessingStageTiming processingStages = default;
        try
        {
            // TAR is independent from live RX. Frames can arrive while the
            // TAR-only decoder is still opening, so retain them in this
            // ordered worker until decoding is ready.
            if (channel.IsRecordingEnabled && !audioCoordinator.IsActive(channel))
                await EnsureRecordingAudioAsync(channel, cancellationToken).ConfigureAwait(false);
            if (!audioCoordinator.IsActive(channel))
                return default;

            ReceiveAudioProcessTiming audioTiming = await audioCoordinator
                .ProcessWithTimingAsync(channel, traffic, encryption, cancellationToken)
                .ConfigureAwait(false);
            processingStages = new ReceiveProcessingStageTiming(
                audioTiming.SessionGateDelay,
                audioTiming.SessionProcessingDuration,
                audioTiming.EncryptedSessionProcessing,
                audioTiming.Measured);
            PublishReceiveDiagnostics(channel, traffic.StreamId, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (IsAudioDeviceFailure(exception))
            {
                receiveOutput.RequestRecovery(channel, exception);
                return default;
            }

            PostToUi(() =>
            {
                AddDebugLog(DateTimeOffset.Now, "RX", DebugLogSeverity.Error,
                    $"Receive processing failed on {channel.Name}; RX and TAR selections retained. {exception}");
                AudioStatusText = $"RX interrupted on {channel.Name}; selection retained, retrying: {exception.Message}";
            });
            await receiveSessions.RetireFailedSessionAsync(channel.Id, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (ReceiveTrafficClassifier.IsTerminator(traffic))
            {
                // The physical decoder can go idle immediately. TAR remains
                // open until the logical receive episode's continuation
                // window expires, allowing a replacement stream to append.
                channel.MarkReceiveAudioMeterEnded(traffic.StreamId);
                PostToUi(() =>
                    channel.MarkReceivePlaybackEnded(traffic.StreamId));
            }
            else
            {
                ChannelViewModel? recordingTarget = ResolveReceiveRecordingTarget(channel);
                if (recordingTarget is not null)
                {
                    ReceiveCallEpisodeSnapshot? recordingEpisode = ResolveReceiveEpisode(
                        channel,
                        traffic.StreamId);
                    callRecordings.ObserveEpisodeTraffic(
                        recordingTarget,
                        recordingEpisode?.PrimaryStreamId ?? traffic.StreamId,
                        traffic.StreamId,
                        traffic,
                        recordingEpisode?.EpisodeId);
                }
            }
        }

        return processingStages;
    }

    private void PublishReceiveDiagnostics(
        ChannelViewModel channel,
        uint streamId,
        DateTimeOffset now)
    {
        if (!receiveDiagnosticsReporter.ShouldInspect(channel, now))
            return;

        ReceiveAudioDiagnostics audio = audioCoordinator.GetDiagnostics(channel);
        ReceiveWorkQueueDiagnostics pipeline = receiveAudioWork.GetDiagnostics(channel, streamId);
        var warning = new ReceiveWarningDiagnostics(
            audio.LostPackets,
            audio.DuplicateOrLatePackets,
            channel.DroppedReceiveFrameCount,
            channel.IgnoredLatePacketCount,
            audio.MalformedPackets);
        if (!receiveDiagnosticsReporter.ShouldPublish(channel, warning, now))
            return;
        AudioMixerDiagnostics? playback = audioCoordinator.GetPlaybackDiagnostics(channel);
        string message = ReceiveDiagnosticsText.FormatWarning(
            channel.Name,
            streamId,
            warning,
            audioCoordinator.IsLivePlaybackEnabled(channel),
            playback,
            pipeline,
            audioCoordinator.GetPlaybackArbitrationDiagnostics(channel));
        void Publish()
        {
            AudioStatusText = message;
            AddDebugLog(now, "RX", DebugLogSeverity.Warning, message);
        }
        if (uiDispatcher.CheckAccess())
            Publish();
        else
            PostToUi(Publish);
    }

    private void HandleReceiveWorkItemTiming(
        ChannelViewModel channel,
        ReceiveWorkItemTiming timing)
    {
        receiveJitterEffectiveness.Observe(channel.Definition.SystemName, timing);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ReceiveJitterEventPublication? jitterPublication = receiveJitterEventReporter.Observe(
            channel,
            timing,
            now);
        if (jitterPublication is ReceiveJitterEventPublication publication)
        {
            AddDebugLog(
                now,
                "RX",
                IsJitterWarning(publication)
                    ? DebugLogSeverity.Warning
                    : DebugLogSeverity.Debug,
                ReceiveDiagnosticsText.FormatJitterBufferPublication(channel.Name, publication));
        }

        if (!receivePipelineTimingReporter.ShouldPublish(channel, timing, now))
            return;

        ReceiveWorkQueueDiagnostics maximums = receiveAudioWork.GetDiagnostics(
            channel,
            timing.Traffic.StreamId);
        AddDebugLog(
            now,
            "RX",
            DebugLogSeverity.Warning,
            ReceiveDiagnosticsText.FormatPipelineDelay(channel.Name, timing, maximums));
    }

    private void PublishFinalReceiveJitterSummary(
        ChannelViewModel channel,
        uint streamId)
    {
        ReceiveJitterEventPublication? jitterPublication = receiveJitterEventReporter.Complete(
            channel,
            streamId);
        if (jitterPublication is not ReceiveJitterEventPublication publication)
            return;

        AddDebugLog(
            DateTimeOffset.UtcNow,
            "RX",
            IsJitterWarning(publication)
                ? DebugLogSeverity.Warning
                : DebugLogSeverity.Debug,
            ReceiveDiagnosticsText.FormatJitterBufferPublication(channel.Name, publication));
    }

    private static bool IsJitterWarning(ReceiveJitterEventPublication publication)
        => publication.Kind == ReceiveJitterEventPublicationKind.Final
            ? publication.TotalMissed > 0
            : publication.MissedSincePrevious > 0;

    private async Task DrainPatchSourceWorkAsync()
    {
        ChannelViewModel[] channels = Systems
            .SelectMany(system => system.Channels)
            .Distinct()
            .ToArray();
        foreach (ChannelViewModel channel in channels)
            await patchSourceReceiveWork.StopAsync(channel).ConfigureAwait(false);
    }

    private static bool IsAudioDeviceFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is IOException or ObjectDisposedException)
                return true;

            if (current is InvalidOperationException &&
                (current.Message.Contains("audio", StringComparison.OrdinalIgnoreCase) ||
                 current.Message.Contains("playback", StringComparison.OrdinalIgnoreCase) ||
                 current.Message.Contains("device", StringComparison.OrdinalIgnoreCase) ||
                 current.Message.Contains("stream", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
