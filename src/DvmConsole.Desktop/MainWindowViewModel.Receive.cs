using Avalonia.Threading;
using DvmConsole.Core.Diagnostics;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Operations;
using System.ComponentModel;
using System.Diagnostics;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel
{
    private void HandleSystemTraffic(SystemViewModel system, FneTrafficFrame traffic)
    {
        if (Volatile.Read(ref disposeStarted) != 0)
            return;

        DateTimeOffset receivedAt = DateTimeOffset.UtcNow;
        long receivedTimestamp = traffic.FneBoundaryTimestamp > 0
            ? traffic.FneBoundaryTimestamp
            : Stopwatch.GetTimestamp();
        ReceivePacketDecisionEnvelope decision = ObserveReceivePacketAtIngress(
            system,
            traffic,
            receivedAt,
            receivedTimestamp);
        ObserveAdaptiveReceiveJitter(system, decision.Traffic);
        ChannelViewModel[] preEnqueuedAudioChannels = EnqueuePriorityReceiveAudio(
            system,
            decision);
        ChannelViewModel[] preEnqueuedPatchChannels = EnqueuePriorityPatchAudio(
            system,
            decision);
        var workItem = new SystemTrafficWorkItem(
            decision,
            preEnqueuedAudioChannels,
            preEnqueuedPatchChannels);
        receivePresentation.Present(system, workItem);
    }

    private void PresentSystemTraffic(
        SystemViewModel system,
        SystemTrafficWorkItem workItem,
        bool publishTrafficDiagnostics)
        => ProcessTrafficDecision(
            system,
            workItem.Decision,
            publishTrafficDiagnostics,
            workItem.PreEnqueuedAudioChannels,
            workItem.PreEnqueuedPatchChannels);

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
        ReceivePacketDecisionEnvelope decision = ObserveReceivePacketAtIngress(
            system,
            traffic,
            now,
            ingressTimestamp);
        ProcessTrafficDecision(
            system,
            decision,
            publishTrafficDiagnostics,
            preEnqueuedAudioChannels,
            preEnqueuedPatchChannels);
    }

    private void ProcessTrafficDecision(
        SystemViewModel system,
        ReceivePacketDecisionEnvelope decision,
        bool publishTrafficDiagnostics,
        IReadOnlyList<ChannelViewModel>? preEnqueuedAudioChannels,
        IReadOnlyList<ChannelViewModel>? preEnqueuedPatchChannels)
    {
        FneTrafficFrame traffic = decision.Traffic;
        DateTimeOffset now = decision.ReceivedAt;
        long ingressTimestamp = decision.ReceivedTimestamp;
        ReceiveCallEpisodeObservation? episodeObservation = decision.EpisodeObservation;
        uint historyStreamId = episodeObservation?.PrimaryStreamId ?? traffic.StreamId;
        ReceiveCallEpisodeSnapshot? episode = decision.EpisodeSnapshot;
        system.RecordTraffic(traffic, publishTrafficDiagnostics);
        List<ChannelViewModel> activeAudioChannels = [];
        List<ChannelViewModel> activePatchSourceChannels = [];
        bool callHistoryChanged = false;
        bool matchedAnyChannel = false;
        TrafficEncryptionMetadata? protocolEncryption = TrafficEncryptionMetadataResolver.TryResolve(traffic);
        bool? protocolEncrypted = protocolEncryption?.Secure;
        foreach (ChannelViewModel channel in ResolveTrafficCandidates(system, decision))
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
            callHistoryChanged = ExpireReceiveCallEpisodes(now) || callHistoryChanged;
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

            bool patchAlreadyEnqueued = preEnqueuedPatchChannels?.Contains(channel) == true;
            if (!patchAlreadyEnqueued && patchSourceDecode.IsActive(channel))
                activePatchSourceChannels.Add(channel);

            if (applied.EndedStreamId is uint endedStreamId)
            {
                patchForwarding.StopSource(channel, endedStreamId);
                DateTimeOffset endedAt = applied.EndedAt ?? now;
                AddDebugLog(
                    now,
                    system.Name,
                    DebugLogSeverity.Info,
                    $"RX call ended on {channel.Name}: {traffic.Protocol.ToString().ToUpperInvariant()} " +
                    $"{traffic.SourceId}→{traffic.DestinationId}, stream {endedStreamId}.");
                receiveCallEpisodes.ObservePhysicalEnd(
                    system.Name,
                    traffic.Protocol,
                    endedStreamId,
                    endedAt);
                TaskObservation.Observe(FinalizeEndedReceiveStreamAsync(
                    channel,
                    endedStreamId,
                    endedAt));
            }

            patchForwarding.ObserveTraffic(channel, traffic);

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
                    $"RX call started on {channel.Name}: {traffic.Protocol.ToString().ToUpperInvariant()} " +
                    $"{traffic.CallType}, {traffic.SourceId}→{traffic.DestinationId}, stream {historyStreamId}" +
                    (protocolEncrypted ?? channel.Definition.IsEncrypted ? ", encrypted" : ", clear") +
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
                    protocolEncrypted ?? channel.Definition.IsEncrypted,
                    receiveEpisodeId: episode?.EpisodeId));
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

            if (protocolEncrypted is bool encrypted)
            {
                callHistoryChanged = callHistory.UpdateEncryption(
                    system.Name,
                    traffic.Protocol,
                    historyStreamId,
                    encrypted,
                    protocolEncryption?.AlgorithmId,
                    protocolEncryption?.KeyId,
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
            if (preEnqueuedAudioChannels?.Contains(channel) == true)
            {
                if (!ReceiveTrafficClassifier.IsTerminator(traffic))
                    channel.MarkReceivePlaybackActive(traffic.SourceId, traffic.StreamId);
                continue;
            }

            EnqueueReceiveAudio(channel, traffic, ingressTimestamp);
        }
        foreach (ChannelViewModel channel in activePatchSourceChannels)
            EnqueuePatchSource(channel, traffic);
        if (callHistoryChanged)
            NotifyCallHistoryChanged();
    }

    private ReceivePacketDecisionEnvelope ObserveReceivePacketAtIngress(
        SystemViewModel system,
        FneTrafficFrame traffic,
        DateTimeOffset receivedAt,
        long receivedTimestamp)
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
        if (trafficRoutes.TryGetValue(
                system,
                out IReadOnlyDictionary<
                    (FneTrafficProtocol Protocol, uint DestinationId),
                    ChannelViewModel[]>? routes))
        {
            routing = ReceiveAudioTrafficRouter.ObserveIngress(
                routes,
                traffic,
                (channel, streamId) =>
                    audioCoordinator.IsTrackingStream(channel, streamId) ||
                    channel.IsTrackingReceiveStream(streamId) ||
                    patchSourceDecode.IsTrackingStream(channel, streamId),
                receivedAt);
        }

        return new ReceivePacketDecisionEnvelope(
            traffic,
            receivedAt,
            receivedTimestamp,
            routing,
            episodeObservation,
            episodeSnapshot);
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
        patchForwarding.StopSource(channel, streamId);
        AddDebugLog(
            now,
            channel.Definition.SystemName,
            DebugLogSeverity.Info,
            applied.Transition == ReceiveStreamTransition.TerminationExpired
                ? $"RX call ended on {channel.Name}: stream {streamId}."
                : $"RX call timed out on {channel.Name}: stream {streamId}.");
        receiveCallEpisodes.ObservePhysicalEnd(
            channel.Definition.SystemName,
            ProtocolFor(channel),
            streamId,
            endedAt);
        TaskObservation.Observe(FinalizeEndedReceiveStreamAsync(channel, streamId, endedAt));
        return false;
    }

    private bool ExpireStaleReceiveRoutes(DateTimeOffset now)
    {
        foreach (SystemViewModel system in Systems)
        {
            if (!trafficRoutes.TryGetValue(
                    system,
                    out IReadOnlyDictionary<
                        (FneTrafficProtocol Protocol, uint DestinationId),
                        ChannelViewModel[]>? routes))
            {
                continue;
            }

            foreach (ReceiveRouteProjectionDecision projection in
                     ReceiveAudioTrafficRouter.Advance(routes, now))
            {
                uint streamId = projection.StreamDecision.EndedStreamId ??
                    projection.StreamDecision.ActiveStreamId ??
                    projection.PrimaryStreamId;
                ChannelViewModel? channel = ReceiveAudioTrafficRouter.ResolveProjectionTarget(
                    routes,
                    projection.RouteKey,
                    streamId,
                    audioCoordinator.IsActive,
                    patchSourceDecode.IsActive);
                if (channel is not null)
                    ProjectReceiveLifecycleDecision(channel, projection, now);
            }
        }
        return false;
    }

    private async Task FinalizeEndedReceiveStreamAsync(
        ChannelViewModel channel,
        uint streamId,
        DateTimeOffset endedAt)
    {
        try
        {
            await receiveAudioWork.RunAfterStreamAsync(
                channel,
                streamId,
                async () =>
                {
                    await audioCoordinator.CompleteStreamAsync(channel, streamId, endedAt)
                        .ConfigureAwait(false);
                })
                .ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref disposeStarted) != 0)
        {
            // Application shutdown already owns receive-session cleanup.
        }
        catch (Exception exception)
        {
            AddDebugLog(
                DateTimeOffset.UtcNow,
                "RX",
                DebugLogSeverity.Warning,
                $"RX audio cleanup failed for {channel.Name}, stream {streamId}: {exception.Message}");
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

        return new FneTrafficFrame(
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

    private IReadOnlyList<ChannelViewModel> ResolveTrafficCandidates(
        SystemViewModel system,
        ReceivePacketDecisionEnvelope decision)
    {
        if (!trafficRoutes.TryGetValue(system, out IReadOnlyDictionary<(FneTrafficProtocol Protocol, uint DestinationId), ChannelViewModel[]>? routes))
            return [];

        return ReceiveAudioTrafficRouter.ResolvePresentationCandidates(
            routes,
            system.Channels,
            decision.Traffic,
            decision.Routing,
            audioCoordinator.IsActive,
            patchSourceDecode.IsActive,
            (channel, streamId) => channel.IsTrackingReceiveStream(streamId));
    }

    private Task StartAudioAsync(ChannelViewModel channel)
        => StartAudioAsync(channel, persistSelection: false);

    private async Task StartAudioAsync(ChannelViewModel channel, bool persistSelection)
    {
        try
        {
            await Task.Run(() => audioCoordinator.StartAsync(channel)).ConfigureAwait(false);
            if (receiveOutputMutePolicy.IsMuted(channel))
            {
                await audioCoordinator
                    .SetLivePlaybackEnabledAsync(channel, enabled: false)
                    .ConfigureAwait(false);
            }
            receiveAudioWork.Start(channel);
            receivePipelineTimingReporter.Reset(channel);
            await RunOnUiThreadAsync(() =>
            {
                channel.SetAudioEnabled(true);
                if (persistSelection)
                    SetReceiveSelectionPreference(channel, enabled: true);
                AudioStatusText = $"Listening to {channel.Name} ({channel.ModeText}); " +
                    $"{audioCoordinator.LivePlaybackChannels.Count} channel(s) active.";
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await RunOnUiThreadAsync(() =>
            {
                channel.SetAudioEnabled(false);
                if (persistSelection)
                    SetReceiveSelectionPreference(channel, enabled: false);
                AudioStatusText = $"RX audio unavailable: {exception.Message}";
            }).ConfigureAwait(false);
        }
    }

    private async Task<ReceiveRouteRecoveryResult> RecoverSelectedReceiveAudioAsync(ChannelViewModel failedChannel)
    {
        await audioReconfigurationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ReceiveRouteRecoveryResult result = await audioCoordinator
                .RecoverSelectedAsync([failedChannel])
                .ConfigureAwait(false);
            DateTimeOffset retryAt = DateTimeOffset.UtcNow.AddSeconds(5);
            foreach (ChannelViewModel channel in result.Restarted)
            {
                if (receiveOutputMutePolicy.IsMuted(channel))
                {
                    await audioCoordinator
                        .SetLivePlaybackEnabledAsync(channel, enabled: false)
                        .ConfigureAwait(false);
                }
                receiveRetryAfter.Remove(channel);
                receiveAudioWork.Start(channel);
                receivePipelineTimingReporter.Reset(channel);
            }
            foreach (ChannelViewModel channel in result.Failed)
                receiveRetryAfter[channel] = retryAt;
            return result;
        }
        finally
        {
            audioReconfigurationLock.Release();
        }
    }

    private void HandleReceiveAudioOutputFailed(ReceiveAudioOutputFailure failure)
    {
        if (Volatile.Read(ref disposeStarted) != 0)
            return;
        lock (receiveOutputRecoverySync)
        {
            foreach (ChannelViewModel channel in failure.AffectedChannels)
                proactiveReceiveOutputRecoveries.Add(channel);
        }
        TaskObservation.Observe(RecoverFailedReceiveOutputAsync(failure));
    }

    private async Task RecoverFailedReceiveOutputAsync(ReceiveAudioOutputFailure failure)
    {
        long recoveryStarted = Stopwatch.GetTimestamp();
        try
        {
            ChannelViewModel? failedChannel = failure.AffectedChannels
                .FirstOrDefault(audioCoordinator.IsActive);
            if (failedChannel is null || Volatile.Read(ref disposeStarted) != 0)
                return;

            ReceiveRouteRecoveryResult recovery = await RecoverSelectedReceiveAudioAsync(failedChannel)
                .ConfigureAwait(false);
            ObserveRouteRecovery(
                Stopwatch.GetElapsedTime(recoveryStarted),
                DescribeRouteRecovery(recovery));
            await RunOnUiThreadAsync(() =>
            {
                AudioStatusText = recovery.Failed.Count == 0
                    ? $"RX audio restarted for {recovery.Restarted.Count} selected channel(s) after the output callback stopped."
                    : recovery.Diagnostic ?? "RX audio unavailable; retrying selected channels.";
            }).ConfigureAwait(false);
        }
        catch (Exception exception) when (Volatile.Read(ref disposeStarted) == 0)
        {
            ObserveRouteRecovery(
                Stopwatch.GetElapsedTime(recoveryStarted),
                $"failed: {exception.Message}");
            await RunOnUiThreadAsync(() =>
                AudioStatusText = $"RX audio recovery failed: {exception.Message}")
                .ConfigureAwait(false);
        }
        finally
        {
            lock (receiveOutputRecoverySync)
            {
                foreach (ChannelViewModel channel in failure.AffectedChannels)
                    proactiveReceiveOutputRecoveries.Remove(channel);
            }
        }
    }

    private bool IsProactiveReceiveOutputRecoveryRunning(ChannelViewModel channel)
    {
        lock (receiveOutputRecoverySync)
            return proactiveReceiveOutputRecoveries.Contains(channel);
    }

    internal async Task ReconcileReceiveSessionsAsync()
    {
        if (Volatile.Read(ref disposeStarted) != 0)
            return;

        await audioReconfigurationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            HashSet<ChannelViewModel> livePlaybackChannels = audioCoordinator
                .LivePlaybackChannels
                .ToHashSet();
            ChannelViewModel[] missing = Systems
                .SelectMany(system => system.Channels)
                .Where(channel => (channel.IsAudioEnabled || channel.IsRecordingEnabled) &&
                    (!audioCoordinator.IsActive(channel) ||
                     (channel.IsAudioEnabled &&
                      !channel.IsAudioSuspended &&
                      !receiveOutputMutePolicy.IsMuted(channel) &&
                      !livePlaybackChannels.Contains(channel))) &&
                    (!receiveRetryAfter.TryGetValue(channel, out DateTimeOffset retryAt) || retryAt <= now))
                .Distinct()
                .ToArray();
            if (missing.Length == 0)
                return;

            int restarted = 0;
            foreach (ChannelViewModel channel in missing)
            {
                try
                {
                    if (channel.IsAudioEnabled)
                    {
                        await audioCoordinator.StartAsync(channel).ConfigureAwait(false);
                        if (receiveOutputMutePolicy.IsMuted(channel))
                        {
                            await audioCoordinator
                                .SetLivePlaybackEnabledAsync(channel, enabled: false)
                                .ConfigureAwait(false);
                        }
                    }
                    else
                        await audioCoordinator.EnsureDecodeAsync(channel).ConfigureAwait(false);
                    receiveAudioWork.Start(channel);
                    receivePipelineTimingReporter.Reset(channel);
                    receiveRetryAfter.Remove(channel);
                    restarted++;
                }
                catch
                {
                    receiveRetryAfter[channel] = now.AddSeconds(5);
                }
            }

            Dispatcher.UIThread.Post(() =>
                AudioStatusText = restarted == missing.Length
                    ? $"Restored {restarted} receive decode session(s)."
                    : $"RX decode unavailable; retrying {missing.Length - restarted} session(s).");
        }
        finally
        {
            audioReconfigurationLock.Release();
        }
    }

    private Task StopAudioAsync(ChannelViewModel channel)
        => StopAudioAsync(channel, persistSelection: false);

    private async Task StopAudioAsync(ChannelViewModel channel, bool persistSelection)
    {
        try
        {
            if (channel.IsRecordingEnabled)
            {
                await audioCoordinator
                    .SetLivePlaybackEnabledAsync(channel, enabled: false)
                    .ConfigureAwait(false);
            }
            else
            {
                await receiveAudioWork.StopAsync(channel).ConfigureAwait(false);
                await Task.Run(() => audioCoordinator.StopAsync(channel)).ConfigureAwait(false);
            }
        }
        finally
        {
            await RunOnUiThreadAsync(() =>
            {
                if (!channel.IsRecordingEnabled)
                    callRecordings.StopChannel(channel);
                channel.SetAudioEnabled(false);
                if (persistSelection)
                    SetReceiveSelectionPreference(channel, enabled: false);
                AudioStatusText = audioCoordinator.LivePlaybackChannels.Count == 0
                    ? "RX audio disabled."
                    : $"Listening to {audioCoordinator.LivePlaybackChannels.Count} channel(s).";
            }).ConfigureAwait(false);
        }
    }

    internal void SetReceiveSelectionPreference(ChannelViewModel channel, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(channel);
        HashSet<string> selected = userSettings.ReceiveEnabledChannelKeys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool changed = enabled
            ? selected.Add(channel.SettingsKey)
            : selected.Remove(channel.SettingsKey);
        if (!changed)
            return;

        userSettings.ReceiveEnabledChannelKeys = selected
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        PersistUserSettings();
    }

    private async Task ToggleSelectedSystemOutputMuteAsync()
    {
        SystemViewModel? system = SelectedSystem;
        if (system is null)
            return;

        bool muted = receiveOutputMutePolicy.Toggle(system);
        await ApplyReceiveOutputMutePolicyAsync(system.Channels).ConfigureAwait(false);
        await RunOnUiThreadAsync(() =>
        {
            NotifySelectedOutputMutePresentationChanged();
            AudioStatusText = muted
                ? $"Live RX output for {system.Name} is muted; decoding and TAR continue."
                : $"Live RX output for {system.Name} is restored except for any muted zones.";
        }).ConfigureAwait(false);
    }

    private async Task ToggleSelectedZoneOutputMuteAsync()
    {
        ZoneViewModel? zone = SelectedSystem?.SelectedZone;
        if (zone is null)
            return;

        bool muted = receiveOutputMutePolicy.Toggle(zone);
        await ApplyReceiveOutputMutePolicyAsync(zone.Channels).ConfigureAwait(false);
        await RunOnUiThreadAsync(() =>
        {
            NotifySelectedOutputMutePresentationChanged();
            AudioStatusText = muted
                ? $"Live RX output for zone {zone.Name} is muted; decoding and TAR continue."
                : $"Live RX output for zone {zone.Name} is restored except for any muted system scope.";
        }).ConfigureAwait(false);
    }

    private async Task ApplyReceiveOutputMutePolicyAsync(IEnumerable<ChannelViewModel> channels)
    {
        await audioReconfigurationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (ChannelViewModel channel in channels.Distinct())
            {
                if (!audioCoordinator.IsActive(channel))
                    continue;

                bool livePlaybackEnabled = channel.IsAudioEnabled &&
                    !channel.IsAudioSuspended &&
                    !receiveOutputMutePolicy.IsMuted(channel);
                await audioCoordinator
                    .SetLivePlaybackEnabledAsync(channel, livePlaybackEnabled)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            audioReconfigurationLock.Release();
        }

        await ReconcileReceiveSessionsAsync().ConfigureAwait(false);
    }

    private void NotifySelectedOutputMutePresentationChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSystemOutputMuted)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedZoneOutputMuted)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSystemOutputMuteGlyph)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedZoneOutputMuteGlyph)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSystemOutputMuteToolTip)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedZoneOutputMuteToolTip)));
    }

    private async Task ProcessAudioAsync(ChannelViewModel channel, FneTrafficFrame traffic)
    {
        try
        {
            // TAR is independent from live RX. Frames can arrive while the
            // TAR-only decoder is still opening, so retain them in this
            // ordered worker until decoding is ready.
            if (channel.IsRecordingEnabled && !audioCoordinator.IsActive(channel))
                await EnsureRecordingAudioAsync(channel).ConfigureAwait(false);
            if (!audioCoordinator.IsActive(channel))
                return;

            await audioCoordinator.ProcessAsync(channel, traffic).ConfigureAwait(false);
            PublishReceiveDiagnostics(channel, traffic.StreamId, DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            if (IsAudioDeviceFailure(exception))
            {
                if (IsProactiveReceiveOutputRecoveryRunning(channel))
                    return;
                long recoveryStarted = Stopwatch.GetTimestamp();
                ReceiveRouteRecoveryResult recovery = await RecoverSelectedReceiveAudioAsync(channel).ConfigureAwait(false);
                ObserveRouteRecovery(
                    Stopwatch.GetElapsedTime(recoveryStarted),
                    DescribeRouteRecovery(recovery));
                Dispatcher.UIThread.Post(() =>
                {
                    AudioStatusText = recovery.Failed.Count == 0
                        ? $"RX audio restarted for {recovery.Restarted.Count} selected channel(s) after an output-device interruption."
                        : recovery.Diagnostic ?? "RX audio unavailable; retrying selected channels.";
                });
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                channel.SetAudioEnabled(false);
                AudioStatusText = $"RX audio stopped: {exception.Message}";
            });
            await Task.Run(() => audioCoordinator.StopAsync(channel)).ConfigureAwait(false);
        }
        finally
        {
            if (ReceiveTrafficClassifier.IsTerminator(traffic))
            {
                // The physical decoder can go idle immediately. TAR remains
                // open until the logical receive episode's continuation
                // window expires, allowing a replacement stream to append.
                channel.MarkReceiveAudioMeterEnded(traffic.StreamId);
                Dispatcher.UIThread.Post(() =>
                    channel.MarkReceivePlaybackEnded(traffic.StreamId));
            }
            else
            {
                ChannelViewModel? recordingTarget = ResolveReceiveRecordingTarget(channel);
                if (recordingTarget is not null)
                {
                    uint recordingStreamId = ResolveReceiveEpisodeStreamId(channel, traffic.StreamId);
                    callRecordings.ObserveEpisodeTraffic(
                        recordingTarget,
                        recordingStreamId,
                        traffic.StreamId,
                        traffic);
                }
            }
        }
    }

    private static string DescribeRouteRecovery(ReceiveRouteRecoveryResult recovery)
        => recovery.Failed.Count == 0
            ? $"restarted {recovery.Restarted.Count} route(s)"
            : recovery.Diagnostic ?? $"failed {recovery.Failed.Count} route(s)";

    private void PublishReceiveDiagnostics(
        ChannelViewModel channel,
        uint streamId,
        DateTimeOffset now)
    {
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
        if (Dispatcher.UIThread.CheckAccess())
            Publish();
        else
            Dispatcher.UIThread.Post(Publish);
    }

    private void HandleReceiveWorkItemTiming(
        ChannelViewModel channel,
        ReceiveWorkItemTiming timing)
    {
        receiveJitterEffectiveness.Observe(channel.Definition.SystemName, timing);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (timing.JitterBufferReorderedPacket || timing.JitterBufferDeadlineMissedPackets > 0)
        {
            AddDebugLog(
                now,
                "RX",
                timing.JitterBufferDeadlineMissedPackets > 0
                    ? DebugLogSeverity.Warning
                    : DebugLogSeverity.Debug,
                ReceiveDiagnosticsText.FormatJitterBufferEvent(channel.Name, timing));
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

    private ChannelViewModel[] EnqueuePriorityReceiveAudio(
        SystemViewModel system,
        ReceivePacketDecisionEnvelope decision)
    {
        if (!trafficRoutes.TryGetValue(
                system,
                out IReadOnlyDictionary<(FneTrafficProtocol Protocol, uint DestinationId), ChannelViewModel[]>? routes))
        {
            return [];
        }

        ChannelViewModel[] targets = ReceiveAudioTrafficRouter.ResolveTargets(
            routes,
            GetReceiveDecodeChannels(routes),
            decision.Traffic,
            decision.Routing,
            (channel, streamId) =>
                audioCoordinator.IsTrackingStream(channel, streamId) ||
                channel.IsTrackingReceiveStream(streamId));
        if (targets.Length == 0)
            return [];

        int acceptedCount = 0;
        foreach (ChannelViewModel channel in targets)
        {
            if (TryEnqueueReceiveAudio(
                    channel,
                    decision.Traffic,
                    decision.ReceivedTimestamp))
            {
                targets[acceptedCount++] = channel;
                if (ReceiveTrafficClassifier.IsTerminator(decision.Traffic))
                    channel.MarkReceiveAudioMeterEnded(decision.Traffic.StreamId);
                else
                    channel.MarkReceiveAudioMeterActive(decision.Traffic.StreamId);
            }
        }

        if (acceptedCount == targets.Length)
            return targets;
        if (acceptedCount == 0)
            return [];

        Array.Resize(ref targets, acceptedCount);
        return targets;
    }

    private ChannelViewModel[] EnqueuePriorityPatchAudio(
        SystemViewModel system,
        ReceivePacketDecisionEnvelope decision)
    {
        if (!trafficRoutes.TryGetValue(
                system,
                out IReadOnlyDictionary<(FneTrafficProtocol Protocol, uint DestinationId), ChannelViewModel[]>? routes))
        {
            return [];
        }

        IReadOnlyList<ChannelViewModel> activeSources = patchSourceDecode.ActiveChannels;
        ChannelViewModel[] targets = ReceiveAudioTrafficRouter.ResolveTargets(
            routes,
            activeSources,
            decision.Traffic,
            decision.Routing,
            patchSourceDecode.IsTrackingStream);
        if (targets.Length == 0)
            return [];

        int acceptedCount = 0;
        foreach (ChannelViewModel channel in targets)
        {
            patchSourceReceiveWork.Start(channel);
            if (patchSourceReceiveWork.Enqueue(
                    channel,
                    decision.Traffic,
                    decision.ReceivedTimestamp,
                    out _))
            {
                targets[acceptedCount++] = channel;
            }
        }

        if (acceptedCount == targets.Length)
            return targets;
        if (acceptedCount == 0)
            return [];

        Array.Resize(ref targets, acceptedCount);
        return targets;
    }

    private IReadOnlyList<ChannelViewModel> GetReceiveDecodeChannels(
        IReadOnlyDictionary<(FneTrafficProtocol Protocol, uint DestinationId), ChannelViewModel[]> routes)
        => audioCoordinator.ActiveChannels
            .Concat(routes.Values
                .SelectMany(channels => channels)
                .Where(channel => channel.IsRecordingEnabled))
            .Distinct()
            .ToArray();

    private void EnqueueReceiveAudio(
        ChannelViewModel channel,
        FneTrafficFrame traffic,
        long ingressTimestamp = 0)
    {
        if (!TryEnqueueReceiveAudio(channel, traffic, ingressTimestamp))
            return;

        if (ReceiveTrafficClassifier.IsTerminator(traffic))
        {
            channel.MarkReceiveAudioMeterEnded(traffic.StreamId);
        }
        else
        {
            channel.MarkReceiveAudioMeterActive(traffic.StreamId);
            channel.MarkReceivePlaybackActive(traffic.SourceId, traffic.StreamId);
        }
    }

    private bool TryEnqueueReceiveAudio(
        ChannelViewModel channel,
        FneTrafficFrame traffic,
        long ingressTimestamp)
    {
        if (Volatile.Read(ref disposeStarted) != 0)
            return false;

        bool accepted = receiveAudioWork.Enqueue(
            channel,
            traffic,
            ingressTimestamp > 0 ? ingressTimestamp : Stopwatch.GetTimestamp(),
            out bool droppedFrame);
        if (droppedFrame)
            channel.RecordDroppedReceiveFrame();
        if (!accepted)
        {
            PublishReceiveDiagnostics(channel, traffic.StreamId, DateTimeOffset.UtcNow);
            return false;
        }

        if (droppedFrame)
            PublishReceiveDiagnostics(channel, traffic.StreamId, DateTimeOffset.UtcNow);
        return true;
    }

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
