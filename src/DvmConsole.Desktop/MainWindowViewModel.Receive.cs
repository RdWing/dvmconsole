using Avalonia.Threading;
using DvmConsole.Core.Diagnostics;
using DvmConsole.Core.Runtime;
using DvmConsole.FneClient;
using DvmConsole.Media;
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
        traffic = NormalizeP25CallIdentity(traffic);
        ChannelViewModel[] preEnqueuedAudioChannels = EnqueuePriorityReceiveAudio(
            system,
            traffic,
            receivedTimestamp);
        var workItem = new SystemTrafficWorkItem(
            traffic,
            receivedAt,
            receivedTimestamp,
            preEnqueuedAudioChannels);

        if (Dispatcher.UIThread.CheckAccess())
        {
            ProcessTraffic(
                system,
                traffic,
                receivedAt: receivedAt,
                preEnqueuedAudioChannels: preEnqueuedAudioChannels,
                ingressTimestamp: receivedTimestamp);
            return;
        }

        bool schedule;
        lock (systemTrafficWorkSync)
        {
            if (!pendingSystemTraffic.TryGetValue(system, out SystemTrafficBuffer? pending))
            {
                pending = new SystemTrafficBuffer();
                pendingSystemTraffic.Add(system, pending);
            }
            long droppedBefore = pending.DroppedCount;
            pending.Enqueue(workItem);
            system.RecordDroppedSystemTraffic(pending.DroppedCount - droppedBefore);
            schedule = scheduledSystemTraffic.Add(system);
        }

        if (schedule)
            Dispatcher.UIThread.Post(() => DrainSystemTraffic(system));
    }

    private void DrainSystemTraffic(SystemViewModel system)
    {
        const int MaximumBatchSize = 64;
        if (Volatile.Read(ref disposeStarted) != 0)
        {
            lock (systemTrafficWorkSync)
            {
                pendingSystemTraffic.Remove(system);
                scheduledSystemTraffic.Remove(system);
            }
            return;
        }

        int processed = 0;
        while (processed < MaximumBatchSize)
        {
            SystemTrafficWorkItem? workItem = null;
            bool empty;
            lock (systemTrafficWorkSync)
            {
                empty = !pendingSystemTraffic.TryGetValue(system, out SystemTrafficBuffer? pending) ||
                    !pending.TryDequeue(out workItem);
                if (empty)
                {
                    pendingSystemTraffic.Remove(system);
                    scheduledSystemTraffic.Remove(system);
                }
            }

            if (empty)
                return;

            SystemTrafficWorkItem current = workItem!.Value;
            ProcessTraffic(
                system,
                current.Traffic,
                publishTrafficDiagnostics: false,
                receivedAt: current.ReceivedAt,
                preEnqueuedAudioChannels: current.PreEnqueuedAudioChannels,
                ingressTimestamp: current.ReceivedTimestamp);
            processed++;
        }

        Dispatcher.UIThread.Post(() => DrainSystemTraffic(system));
    }

    internal void ProcessTraffic(
        SystemViewModel system,
        FneTrafficFrame traffic,
        bool publishTrafficDiagnostics = true,
        DateTimeOffset? receivedAt = null,
        IReadOnlyList<ChannelViewModel>? preEnqueuedAudioChannels = null,
        long ingressTimestamp = 0)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(traffic);
        traffic = NormalizeP25CallIdentity(traffic);
        DateTimeOffset now = receivedAt ?? DateTimeOffset.Now;
        system.RecordTraffic(traffic, publishTrafficDiagnostics);
        List<ChannelViewModel> activeAudioChannels = [];
        List<ChannelViewModel> activePatchSourceChannels = [];
        bool callHistoryChanged = false;
        bool matchedAnyChannel = false;
        TrafficEncryptionMetadata? protocolEncryption = TrafficEncryptionMetadataResolver.TryResolve(traffic);
        bool? protocolEncrypted = protocolEncryption?.Secure;
        foreach (ChannelViewModel channel in ResolveTrafficCandidates(system, traffic))
        {
            callHistoryChanged = ExpireStaleReceiveStreams(channel, now) || callHistoryChanged;
            ChannelTrafficApplyResult applied = channel.ApplyTraffic(system.Name, traffic, now);
            if (!applied.Matched)
                continue;
            matchedAnyChannel = true;
            if (applied.Transition == ReceiveStreamTransition.IgnoredLate)
            {
                channel.RecordIgnoredLatePacket();
                PublishReceiveDiagnostics(channel, traffic.StreamId, now);
                continue;
            }

            patchForwarding.ObserveTraffic(channel, traffic);
            if (patchSourceDecode.IsActive(channel))
                activePatchSourceChannels.Add(channel);

            if (applied.EndedStreamId is uint endedStreamId)
            {
                DateTimeOffset endedAt = applied.EndedAt ?? now;
                AddDebugLog(
                    now,
                    system.Name,
                    DebugLogSeverity.Info,
                    $"RX call ended on {channel.Name}: {traffic.Protocol.ToString().ToUpperInvariant()} " +
                    $"{traffic.SourceId}→{traffic.DestinationId}, stream {endedStreamId}.");
                callHistoryChanged = callHistory.Complete(
                    system.Name,
                    traffic.Protocol,
                    endedStreamId,
                    endedAt,
                    channel.Name,
                    channel.Definition.DestinationId) || callHistoryChanged;
                callRecordings.StopStream(channel, endedStreamId);
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
                    traffic.StreamId,
                    channel.Name,
                    traffic.DestinationId))
            {
                AddDebugLog(
                    now,
                    system.Name,
                    DebugLogSeverity.Info,
                    $"RX call started on {channel.Name}: {traffic.Protocol.ToString().ToUpperInvariant()} " +
                    $"{traffic.CallType}, {traffic.SourceId}→{traffic.DestinationId}, stream {traffic.StreamId}" +
                    (protocolEncrypted ?? channel.Definition.IsEncrypted ? ", encrypted" : ", clear") +
                    $"{DescribeFneSignalQuality(traffic)}.");
                callHistory.Add(new CallHistoryEntry(
                    now,
                    system.Name,
                    channel.Name,
                    traffic.SourceId,
                    traffic.DestinationId,
                    traffic.Protocol,
                    traffic.StreamId,
                    channel.LastCallerText,
                    protocolEncrypted ?? channel.Definition.IsEncrypted));
                callHistoryChanged = true;
            }

            if (protocolEncrypted is bool encrypted)
            {
                callHistoryChanged = callHistory.UpdateEncryption(
                    system.Name,
                    traffic.Protocol,
                    traffic.StreamId,
                    encrypted,
                    protocolEncryption?.AlgorithmId,
                    protocolEncryption?.KeyId,
                    channel.Name,
                    traffic.DestinationId) || callHistoryChanged;
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

    private bool ExpireStaleReceiveStreams(ChannelViewModel channel, DateTimeOffset now)
    {
        bool callHistoryChanged = false;
        while (true)
        {
            ChannelTrafficApplyResult applied = channel.AdvanceReceiveLifecycle(now);
            if (applied.Transition == ReceiveStreamTransition.GraceStarted)
                continue;
            if (applied.Transition is not (
                    ReceiveStreamTransition.GraceExpired or
                    ReceiveStreamTransition.TerminationExpired) ||
                applied.EndedStreamId is not uint streamId)
            {
                return callHistoryChanged;
            }

            DateTimeOffset endedAt = applied.EndedAt ?? now;

            AddDebugLog(
                now,
                channel.Definition.SystemName,
                DebugLogSeverity.Info,
                applied.Transition == ReceiveStreamTransition.TerminationExpired
                    ? $"RX call ended on {channel.Name}: stream {streamId}."
                    : $"RX call timed out on {channel.Name}: stream {streamId}.");
            callHistoryChanged = callHistory.Complete(
                channel.Definition.SystemName,
                ProtocolFor(channel),
                streamId,
                endedAt,
                channel.Name,
                channel.Definition.DestinationId) || callHistoryChanged;
            _ = CompleteTimedOutReceiveAudioStreamAsync(channel, streamId, now);
            callRecordings.StopStream(channel, streamId);
        }
    }

    private async Task CompleteTimedOutReceiveAudioStreamAsync(
        ChannelViewModel channel,
        uint streamId,
        DateTimeOffset endedAt)
    {
        try
        {
            await audioCoordinator.CompleteStreamAsync(channel, streamId, endedAt)
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
        FneTrafficFrame traffic)
    {
        if (!trafficRoutes.TryGetValue(system, out IReadOnlyDictionary<(FneTrafficProtocol Protocol, uint DestinationId), ChannelViewModel[]>? routes))
            return [];

        routes.TryGetValue((traffic.Protocol, traffic.DestinationId), out ChannelViewModel[]? routedChannels);
        routedChannels ??= [];
        if (!ReceiveTrafficClassifier.IsTerminator(traffic))
            return SelectResourceRepresentatives(routedChannels, traffic);

        ChannelViewModel[] activeStreamChannels = system.Channels
            .Where(channel => channel.IsTrackingReceiveStream(traffic.StreamId))
            .ToArray();
        if (activeStreamChannels.Length == 0)
            return routedChannels;
        if (routedChannels.Length == 0)
            return activeStreamChannels;

        return routedChannels
            .Concat(activeStreamChannels)
            .Distinct()
            .ToArray();
    }

    private IReadOnlyList<ChannelViewModel> SelectResourceRepresentatives(
        IEnumerable<ChannelViewModel> channels,
        FneTrafficFrame traffic)
    {
        // A resource can be placed in more than one zone, producing multiple
        // visual channel instances for the same system/talkgroup. As in the
        // WPF console, only one copy may own an inbound stream; otherwise one
        // network frame creates duplicate call starts, recording work, patch
        // forwarding, and decoded audio.
        return channels
            .GroupBy(channel => (
                channel.Definition.Mode,
                channel.Definition.DestinationId,
                Slot: channel.Definition.Mode == "dmr" ? channel.Definition.Slot : (byte)0))
            .Select(group => group.FirstOrDefault(channel =>
                    channel.State == ChannelRuntimeState.Receiving &&
                    channel.StreamId == traffic.StreamId) ??
                group.FirstOrDefault(channel => audioCoordinator.IsActive(channel)) ??
                group.FirstOrDefault(channel => patchSourceDecode.IsActive(channel)) ??
                group.FirstOrDefault(channel => channel.IsRecordingEnabled) ??
                group.First())
            .ToArray();
    }

    private Task StartAudioAsync(ChannelViewModel channel)
        => StartAudioAsync(channel, persistSelection: false);

    private async Task StartAudioAsync(ChannelViewModel channel, bool persistSelection)
    {
        try
        {
            await Task.Run(() => audioCoordinator.StartAsync(channel)).ConfigureAwait(false);
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
                        await audioCoordinator.StartAsync(channel).ConfigureAwait(false);
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

    private async Task ProcessAudioAsync(ChannelViewModel channel, FneTrafficFrame traffic)
    {
        try
        {
            await audioCoordinator.ProcessAsync(channel, traffic).ConfigureAwait(false);
            PublishReceiveDiagnostics(channel, traffic.StreamId, DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            if (IsAudioDeviceFailure(exception))
            {
                ReceiveRouteRecoveryResult recovery = await RecoverSelectedReceiveAudioAsync(channel).ConfigureAwait(false);
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
                // The presentation can go idle immediately, but the decoder
                // and TAR writer remain available during the bounded
                // terminator hold. ExpireStaleReceiveStreams finalizes both
                // after the stream has remained quiet.
                channel.MarkReceiveAudioMeterEnded(traffic.StreamId);
                Dispatcher.UIThread.Post(() =>
                    channel.MarkReceivePlaybackEnded(traffic.StreamId));
            }
            else
            {
                callRecordings.ObserveTraffic(channel, traffic);
            }
        }
    }

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
            pipeline);
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
        FneTrafficFrame traffic,
        long ingressTimestamp)
    {
        if (!trafficRoutes.TryGetValue(
                system,
                out IReadOnlyDictionary<(FneTrafficProtocol Protocol, uint DestinationId), ChannelViewModel[]>? routes))
        {
            return [];
        }

        ChannelViewModel[] targets = ReceiveAudioTrafficRouter.ResolveTargets(
            routes,
            audioCoordinator.ActiveChannels,
            traffic,
            audioCoordinator.IsTrackingStream);
        if (targets.Length == 0)
            return [];

        int acceptedCount = 0;
        foreach (ChannelViewModel channel in targets)
        {
            if (TryEnqueueReceiveAudio(channel, traffic, ingressTimestamp))
            {
                targets[acceptedCount++] = channel;
                if (ReceiveTrafficClassifier.IsTerminator(traffic))
                    channel.MarkReceiveAudioMeterEnded(traffic.StreamId);
                else
                    channel.MarkReceiveAudioMeterActive(traffic.StreamId);
            }
        }

        if (acceptedCount == targets.Length)
            return targets;
        if (acceptedCount == 0)
            return [];

        Array.Resize(ref targets, acceptedCount);
        return targets;
    }

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
