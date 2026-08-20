using Avalonia.Threading;
using DvmConsole.Core.Diagnostics;
using DvmConsole.Core.Runtime;
using DvmConsole.FneClient;
using DvmConsole.Media;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel
{
    private void HandleSystemTraffic(SystemViewModel system, FneTrafficFrame traffic)
    {
        if (Volatile.Read(ref disposeStarted) != 0)
            return;

        if (Dispatcher.UIThread.CheckAccess())
        {
            ProcessTraffic(system, traffic);
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
            pending.Enqueue(traffic);
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
            FneTrafficFrame? traffic = null;
            bool empty;
            lock (systemTrafficWorkSync)
            {
                empty = !pendingSystemTraffic.TryGetValue(system, out SystemTrafficBuffer? pending) ||
                    !pending.TryDequeue(out traffic);
                if (empty)
                {
                    pendingSystemTraffic.Remove(system);
                    scheduledSystemTraffic.Remove(system);
                }
            }

            if (empty)
            {
                system.PublishTrafficDiagnostics();
                return;
            }

            ProcessTraffic(system, traffic!, publishTrafficDiagnostics: false);
            processed++;
        }

        system.PublishTrafficDiagnostics();
        Dispatcher.UIThread.Post(() => DrainSystemTraffic(system));
    }

    internal void ProcessTraffic(
        SystemViewModel system,
        FneTrafficFrame traffic,
        bool publishTrafficDiagnostics = true,
        DateTimeOffset? receivedAt = null)
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
            ChannelTrafficApplyResult applied = channel.ApplyTraffic(system.Name, traffic, now);
            if (!applied.Matched)
                continue;
            matchedAnyChannel = true;
            if (applied.Transition == ReceiveStreamTransition.IgnoredLate)
            {
                channel.RecordIgnoredLatePacket();
                PublishReceiveDiagnostics(channel, now);
                continue;
            }

            patchForwarding.ObserveTraffic(channel, traffic);
            if (patchSourceDecode.IsActive(channel))
                activePatchSourceChannels.Add(channel);

            if (applied.EndedStreamId is uint endedStreamId)
            {
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
                    now,
                    channel.Name,
                    channel.Definition.DestinationId) || callHistoryChanged;
            }

            bool canStartHistory = applied.Transition is
                ReceiveStreamTransition.Started or
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
            IsDmrTerminator(traffic))
        {
            system.RecordNonCallDmrTerminator();
        }

        foreach (ChannelViewModel channel in activeAudioChannels)
            EnqueueReceiveAudio(channel, traffic);
        foreach (ChannelViewModel channel in activePatchSourceChannels)
            EnqueuePatchSource(channel, traffic);
        if (callHistoryChanged)
            NotifyCallHistoryChanged();
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
            traffic.Payload);
    }

    private static bool IsDmrTerminator(FneTrafficFrame traffic)
    {
        return traffic.FrameType.Equals("TERMINATOR", StringComparison.OrdinalIgnoreCase) ||
            traffic.Subtype.Equals("TERMINATOR_WITH_LC", StringComparison.OrdinalIgnoreCase);
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
        if (!IsTerminatingTraffic(traffic))
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

    private static bool IsTerminatingTraffic(FneTrafficFrame traffic)
    {
        if (traffic.FrameType.Equals("TERMINATOR", StringComparison.OrdinalIgnoreCase))
            return true;

        return traffic.Protocol switch
        {
            FneTrafficProtocol.Dmr => traffic.Subtype.Equals("TERMINATOR_WITH_LC", StringComparison.OrdinalIgnoreCase),
            FneTrafficProtocol.P25 => traffic.Subtype.Equals("TDU", StringComparison.OrdinalIgnoreCase) ||
                                      traffic.Subtype.Equals("TDULC", StringComparison.OrdinalIgnoreCase),
            FneTrafficProtocol.Analog => traffic.Subtype.Equals("TERMINATOR", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private async Task StartAudioAsync(ChannelViewModel channel)
    {
        try
        {
            await Task.Run(() => audioCoordinator.StartAsync(channel)).ConfigureAwait(false);
            receiveAudioWork.Start(channel);
            await RunOnUiThreadAsync(() =>
            {
                channel.SetAudioEnabled(true);
                AudioStatusText = $"Listening to {channel.Name} ({channel.ModeText}); {audioCoordinator.ActiveChannels.Count} channel(s) active.";
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await RunOnUiThreadAsync(() =>
            {
                channel.SetAudioEnabled(false);
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
            ChannelViewModel[] missing = Systems
                .SelectMany(system => system.Channels)
                .Where(channel => channel.IsAudioEnabled &&
                    !audioCoordinator.IsActive(channel) &&
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
                    await audioCoordinator.StartAsync(channel).ConfigureAwait(false);
                    receiveAudioWork.Start(channel);
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
                    ? $"Restored {restarted} selected receive channel(s)."
                    : $"RX audio unavailable; retrying {missing.Length - restarted} selected channel(s).");
        }
        finally
        {
            audioReconfigurationLock.Release();
        }
    }

    private async Task StopAudioAsync(ChannelViewModel channel)
    {
        try
        {
            await receiveAudioWork.StopAsync(channel).ConfigureAwait(false);
            await Task.Run(() => audioCoordinator.StopAsync(channel)).ConfigureAwait(false);
        }
        finally
        {
            await RunOnUiThreadAsync(() =>
            {
                callRecordings.StopChannel(channel);
                channel.SetAudioEnabled(false);
                AudioStatusText = audioCoordinator.ActiveChannels.Count == 0
                    ? "RX audio disabled."
                    : $"Listening to {audioCoordinator.ActiveChannels.Count} channel(s).";
            }).ConfigureAwait(false);
        }
    }

    private async Task ProcessAudioAsync(ChannelViewModel channel, FneTrafficFrame traffic)
    {
        try
        {
            await audioCoordinator.ProcessAsync(channel, traffic).ConfigureAwait(false);
            PublishReceiveDiagnostics(channel, DateTimeOffset.UtcNow);
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
            // A terminator must close TAR even when the output device failed
            // while decoding the same frame; recording lifecycle is separate
            // from playback recovery.
            callRecordings.ObserveTraffic(channel, traffic);
            if (IsTerminatingTraffic(traffic))
            {
                Dispatcher.UIThread.Post(() =>
                    channel.MarkReceivePlaybackEnded(traffic.StreamId));
            }
        }
    }

    private void PublishReceiveDiagnostics(ChannelViewModel channel, DateTimeOffset now)
    {
        ReceiveAudioDiagnostics audio = audioCoordinator.GetDiagnostics(channel);
        var combined = new ReceiveAudioDiagnostics(
            audio.FramesDecoded,
            audio.LostPackets + channel.DroppedReceiveFrameCount,
            audio.DuplicateOrLatePackets + channel.IgnoredLatePacketCount,
            audio.MalformedPackets);
        if (!receiveDiagnosticsReporter.ShouldPublish(channel, combined, now))
            return;
        string stateText = audioCoordinator.IsActive(channel) ? "audio continues" : "late traffic ignored";
        void Publish() => AudioStatusText = $"RX {channel.Name}: {combined.SummaryText} ({stateText})";
        if (Dispatcher.UIThread.CheckAccess())
            Publish();
        else
            Dispatcher.UIThread.Post(Publish);
    }

    private void EnqueueReceiveAudio(ChannelViewModel channel, FneTrafficFrame traffic)
    {
        if (Volatile.Read(ref disposeStarted) != 0)
            return;

        bool accepted = receiveAudioWork.Enqueue(channel, traffic, out bool droppedFrame);
        if (droppedFrame)
            channel.RecordDroppedReceiveFrame();
        if (!accepted)
        {
            PublishReceiveDiagnostics(channel, DateTimeOffset.UtcNow);
            return;
        }

        if (droppedFrame)
            PublishReceiveDiagnostics(channel, DateTimeOffset.UtcNow);

        if (!IsTerminatingTraffic(traffic))
            channel.MarkReceivePlaybackActive(traffic.SourceId, traffic.StreamId);
    }

    private async Task DrainPatchSourceWorkAsync()
    {
        Task[] pending;
        lock (patchSourceWorkSync)
            pending = patchSourceWork.Values.ToArray();
        if (pending.Length > 0)
            await Task.WhenAll(pending).ConfigureAwait(false);
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
