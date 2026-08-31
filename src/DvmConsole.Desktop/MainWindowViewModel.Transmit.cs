using DvmConsole.Audio;
using DvmConsole.Application;
using DvmConsole.Core.Diagnostics;
using System.Diagnostics;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel
{
    private async Task StartTransmitAsync(ChannelViewModel channel)
    {
        await StartTransmitAsync([channel]).ConfigureAwait(false);
    }

    private async Task StartTransmitAsync(IReadOnlyCollection<ChannelViewModel> channels)
    {
        if (networkDisabledDemo)
        {
            await RunOnUiThreadAsync(() =>
                TransmitStatusText = "Demo safety boundary: PTT input observed; network output remains disabled.")
                .ConfigureAwait(false);
            return;
        }

        if (channels.Count == 0 || transmitCoordinator.ActiveChannel is not null)
            return;

        ChannelViewModel? receivingChannel = channels.FirstOrDefault(
            channel => channel.IsReceivePresentationActive && !channel.HasCallPriority);
        if (receivingChannel is not null)
        {
            await RunOnUiThreadAsync(() =>
                TransmitStatusText = $"PTT unavailable: {receivingChannel.Name} is currently receiving.")
                .ConfigureAwait(false);
            return;
        }

        TransmitTarget[] targets = channels
            .Select(channel => new TransmitTarget(
                channel.ToTransmitDescriptor(),
                Systems.FirstOrDefault(candidate => candidate.Name.Equals(
                    channel.Definition.SystemName,
                    StringComparison.OrdinalIgnoreCase))!))
            .ToArray();
        TransmitChannelDescriptor? missingSystemChannel = targets
            .FirstOrDefault(target => target.System is null)?.Channel;
        if (missingSystemChannel is not null)
        {
            TransmitStatusText = $"PTT unavailable: system '{missingSystemChannel.Definition.SystemName}' was not found.";
            return;
        }

        bool playPermitTone = TalkPermitTone;
        var startupTimer = Stopwatch.StartNew();
        TaskCompletionSource<bool>? cueRelease = null;
        TaskCompletionSource<bool>? transmitActivated = null;
        Task<LocalTonePlaybackResult>? preparedPermitTone = null;
        bool receiveTransitionGateActive = false;
        long receiveTransitionDiscardedAtStart = 0;
        try
        {
            // Keep the Apple duplex unit alive across PTT so its output mix
            // remains the AEC reference and macOS does not repeatedly remove
            // and recreate the system microphone-mode control.
            if (userSettings.MuteRxAudioWhileTransmitting)
                await MuteReceiveAudioAsync("RX audio muted while transmitting.");

            MicrophoneStartExpectation microphoneExpectation = await transmitCoordinator
                .InspectNextMicrophoneStartAsync()
                .ConfigureAwait(false);
            if (!userSettings.MuteRxAudioWhileTransmitting &&
                microphoneExpectation.RequiresReceiveTransitionGate)
            {
                receiveTransitionDiscardedAtStart =
                    audioCoordinator.SetLivePlaybackDiscarded(discarded: true);
                receiveTransitionGateActive = true;
            }

            // Never publish operator audio or an ON AIR presentation until
            // fresh physical callbacks prove the selected capture path is
            // ready. This gate applies with or without a permit tone and also
            // re-arms when a warm capture has become stale.
            transmitCoordinator.SetMicrophoneAudioSuppressed(true);
            await Task.Run(() => transmitCoordinator.StartAsync(targets)).ConfigureAwait(false);
            TimeSpan transmitSessionsReadyAt = startupTimer.Elapsed;
            bool microphoneStartedCold = transmitCoordinator.ActiveMicrophoneStartedCold;
            bool? microphoneIsBluetooth = transmitCoordinator.ActiveMicrophoneIsBluetooth;
            bool actualTransitionRequiresGate =
                microphoneStartedCold && microphoneIsBluetooth != false;
            if (!userSettings.MuteRxAudioWhileTransmitting &&
                actualTransitionRequiresGate &&
                !receiveTransitionGateActive)
            {
                receiveTransitionDiscardedAtStart =
                    audioCoordinator.SetLivePlaybackDiscarded(discarded: true);
                receiveTransitionGateActive = true;
            }
            else if (receiveTransitionGateActive && !actualTransitionRequiresGate)
            {
                await EndColdBluetoothReceiveTransitionAsync(
                    receiveTransitionDiscardedAtStart).ConfigureAwait(false);
                receiveTransitionGateActive = false;
            }
            MicrophoneReadinessTiming microphoneReadiness;
            TimeSpan cueBarrierReleasedAt;
            if (playPermitTone)
            {
                cueRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                transmitActivated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task<MicrophoneReadinessTiming> microphoneReady = transmitCoordinator.WaitForMicrophoneReadyAsync();
                preparedPermitTone = localTonePlayer.PlayTalkPermitAsync(
                    microphoneStartedCold,
                    microphoneIsBluetooth,
                    cueRelease.Task,
                    beforeCueAsync: async cancellationToken =>
                    {
                        try
                        {
                            await transmitCoordinator.ActivateAsync(cancellationToken).ConfigureAwait(false);
                            transmitActivated.TrySetResult(true);
                        }
                        catch (Exception exception)
                        {
                            transmitActivated.TrySetException(exception);
                            throw;
                        }
                    });
                microphoneReadiness = await microphoneReady.ConfigureAwait(false);
                cueBarrierReleasedAt = startupTimer.Elapsed;
                cueRelease.TrySetResult(true);
            }
            else
            {
                microphoneReadiness = await transmitCoordinator
                    .WaitForMicrophoneReadyAsync()
                    .ConfigureAwait(false);
                await transmitCoordinator.ActivateAsync().ConfigureAwait(false);
                cueBarrierReleasedAt = startupTimer.Elapsed;
            }
            TimeSpan microphoneReadyAt = startupTimer.Elapsed;
            ChannelViewModel[] activeChannels = ResolveChannels(transmitCoordinator.ActiveChannels);
            var startupDiagnostics = new TransmitStartupDiagnostics(
                transmitSessionsReadyAt,
                cueBarrierReleasedAt,
                microphoneReadyAt,
                microphoneStartedCold,
                microphoneIsBluetooth,
                microphoneReadiness);
            if (playPermitTone)
            {
                Task completed = await Task.WhenAny(
                    transmitActivated!.Task,
                    preparedPermitTone!).ConfigureAwait(false);
                if (completed == preparedPermitTone)
                    await preparedPermitTone.ConfigureAwait(false);
                await transmitActivated.Task.ConfigureAwait(false);
            }
            await PresentTransmitStartupAsync(
                targets,
                activeChannels,
                startupDiagnostics,
                startupTimer).ConfigureAwait(false);
            // A permit tone is an operational readiness indication. Play it
            // only after the final output route is rendering, every selected
            // protocol call is active, and the shared microphone path is
            // ready. Operator audio remains suppressed until it completes.
            if (playPermitTone)
            {
                await PlayTalkPermitToneAsync(
                    reportSuccess: false,
                    requiredForTransmit: true,
                    microphoneStartedCold: microphoneStartedCold,
                    microphoneIsBluetooth: microphoneIsBluetooth,
                    preparedPlayback: preparedPermitTone,
                    pttTimer: startupTimer,
                    transmitSessionsReadyAt: transmitSessionsReadyAt,
                    cueBarrierReleasedAt: cueBarrierReleasedAt,
                    microphoneReadyAt: microphoneReadyAt).ConfigureAwait(false);
            }
            bool requirePostTransitionMicrophoneRecovery =
                microphoneStartedCold && microphoneIsBluetooth != false;
            TimeSpan postCueMicrophoneRecovery = await transmitCoordinator
                .ReleaseMicrophoneAudioAsync(requirePostTransitionMicrophoneRecovery)
                .ConfigureAwait(false);
            if (requirePostTransitionMicrophoneRecovery)
            {
                string recoveryContext = playPermitTone
                    ? "after permit-tone output closed"
                    : "after cold Bluetooth startup";
                AddDebugLog(
                    DateTimeOffset.Now,
                    "TX",
                    DebugLogSeverity.Debug,
                    $"Cold Bluetooth microphone resumed {postCueMicrophoneRecovery.TotalMilliseconds:0} ms " +
                    $"{recoveryContext}; operator audio released and watchdog armed.");
            }
            if (receiveTransitionGateActive)
            {
                await EndColdBluetoothReceiveTransitionAsync(
                    receiveTransitionDiscardedAtStart).ConfigureAwait(false);
                receiveTransitionGateActive = false;
            }
        }
        catch (Exception exception)
        {
            cueRelease?.TrySetCanceled();
            transmitActivated?.TrySetCanceled();
            await ObservePreparedPermitToneFailureAsync(preparedPermitTone).ConfigureAwait(false);
            Exception startupFailure = exception;
            try
            {
                await Task.Run(() => transmitCoordinator.StopAsync()).ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                AddDebugLog(DateTimeOffset.Now, "TX", DebugLogSeverity.Warning,
                    $"Transmit startup cleanup also failed: {cleanupException.Message}");
            }
            finally
            {
                transmitCoordinator.SetMicrophoneAudioSuppressed(false);
            }

            if (receiveTransitionGateActive)
            {
                await EndColdBluetoothReceiveTransitionAsync(
                    receiveTransitionDiscardedAtStart).ConfigureAwait(false);
                receiveTransitionGateActive = false;
            }

            try
            {
                await RestoreSuspendedAudioAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                AddDebugLog(DateTimeOffset.Now, "TX", DebugLogSeverity.Warning,
                    $"Receive-audio restoration after transmit startup failure also failed: {cleanupException.Message}");
            }

            await RunOnUiThreadAsync(() =>
            {
                foreach (ChannelViewModel channel in channels)
                {
                    channel.SetTransmitEnabled(false);
                    callRecordings.StopTransmit(channel);
                }
                AddDebugLog(DateTimeOffset.Now, "TX", DebugLogSeverity.Error,
                    $"Transmit startup failed: {startupFailure}");
                TransmitStatusText = $"PTT unavailable: {startupFailure.Message}";
            }).ConfigureAwait(false);
        }
    }

    private async Task PresentTransmitStartupAsync(
        IReadOnlyList<TransmitTarget> targets,
        IReadOnlyList<ChannelViewModel> activeChannels,
        TransmitStartupDiagnostics diagnostics,
        Stopwatch startupTimer)
    {
        await RunOnUiThreadAsync(() =>
        {
            foreach (ChannelViewModel channel in activeChannels)
                channel.SetTransmitEnabled(
                    true,
                    transmitCoordinator.GetActiveStreamId(new ChannelId(channel.SessionId)));
            foreach (ChannelViewModel channel in activeChannels)
            {
                TransmitTarget target = targets.First(candidate =>
                    candidate.Channel.Id == new ChannelId(channel.SessionId));
                uint streamId = transmitCoordinator.GetActiveStreamId(new ChannelId(channel.SessionId));
                bool secure = channel.Definition.IsEncrypted && channel.IsTransmitEncrypted;
                byte? algorithmId = null;
                ushort? keyId = null;
                if (secure && EncryptionPresentation.TryParseConfiguredAlgorithm(
                        channel.Definition,
                        out byte parsedAlgorithmId,
                        out ushort parsedKeyId))
                {
                    algorithmId = parsedAlgorithmId;
                    keyId = parsedKeyId;
                }

                AddDebugLog(
                    DateTimeOffset.Now,
                    target.System.Name,
                    DebugLogSeverity.Info,
                    $"TX call started on {channel.Name}: " +
                    $"{ProtocolFor(channel).ToString().ToUpperInvariant()} " +
                    $"{target.System.SourceId ?? 0}→{channel.Definition.DestinationId}, " +
                    $"stream {streamId}" +
                    (secure ? ", secure." : ", clear."));
                AddDebugLog(
                    DateTimeOffset.Now,
                    target.System.Name,
                    DebugLogSeverity.Debug,
                    FormatTransmitStartupDiagnostics(
                        channel,
                        streamId,
                        diagnostics,
                        startupTimer.Elapsed));
                callHistory.AddConsoleTransmission(
                    DateTimeOffset.Now,
                    target.System.Name,
                    channel.Name,
                    target.System.SourceId ?? 0,
                    channel.Definition.DestinationId,
                    ProtocolFor(channel),
                    streamId,
                    callerText: "Console",
                    encrypted: secure,
                    encryptionAlgorithmId: algorithmId,
                    encryptionKeyId: keyId,
                    channelId: new ChannelId(channel.SessionId));
            }

            NotifyCallHistoryChanged();
            TransmitStatusText = activeChannels.Count == 1
                ? $"Transmitting on {activeChannels[0].Name} · PTT: {PttInputSourceText}."
                : $"Transmitting on {activeChannels.Count} selected channels · PTT: {PttInputSourceText}.";
        }).ConfigureAwait(false);
    }

    private string FormatTransmitStartupDiagnostics(
        ChannelViewModel channel,
        uint streamId,
        TransmitStartupDiagnostics diagnostics,
        TimeSpan channelPresentationAt)
    {
        string captureTiming = diagnostics.MicrophoneReadiness is null
            ? "microphone readiness not gated"
            : $"capture start returned " +
              $"{diagnostics.MicrophoneReadiness.CaptureStartReturned.TotalMilliseconds:0} ms, " +
              $"first samples " +
              $"{diagnostics.MicrophoneReadiness.FirstSamplesReceived.TotalMilliseconds:0} ms";
        return $"Vocoder TX initialized for {channel.Name}: mode {channel.Definition.Mode}, " +
               $"stream {streamId}, audio processing {userSettings.AudioProcessingMode}, " +
               $"warm microphone {(userSettings.KeepTransmitMicrophoneWarm ? "enabled" : "disabled")}, " +
               $"TX sessions {diagnostics.TransmitSessionsReadyAt.TotalMilliseconds:0} ms, " +
               $"cue barrier {diagnostics.CueBarrierReleasedAt.TotalMilliseconds:0} ms, " +
               $"selected microphone {diagnostics.MicrophoneReadyAt.TotalMilliseconds:0} ms " +
               $"({(diagnostics.MicrophoneStartedCold ? "cold" : "warm")}, " +
               $"Bluetooth {DescribeBluetoothState(diagnostics.MicrophoneIsBluetooth)}), " +
               $"{captureTiming}, " +
               $"channel presentation {channelPresentationAt.TotalMilliseconds:0} ms.";
    }

    private static async Task ObservePreparedPermitToneFailureAsync(
        Task<LocalTonePlaybackResult>? preparedPermitTone)
    {
        if (preparedPermitTone is null || preparedPermitTone.IsCompletedSuccessfully)
            return;

        try
        {
            await preparedPermitTone.ConfigureAwait(false);
        }
        catch
        {
            // The primary startup failure is reported by the caller. Observe a
            // concurrently prepared cue's failure so it cannot escape as an
            // unobserved background exception.
        }
    }

    private async Task EndColdBluetoothReceiveTransitionAsync(long discardedAtStart)
    {
        try
        {
            long discardedAtEnd = audioCoordinator.SetLivePlaybackDiscarded(discarded: false);
            long discardedSamples = Math.Max(0, discardedAtEnd - discardedAtStart);
            await RunOnUiThreadAsync(() => AddDebugLog(
                DateTimeOffset.Now,
                "RX",
                DebugLogSeverity.Debug,
                $"Cold Bluetooth PTT transition discarded " +
                $"{discardedSamples * 1000.0 / PcmAudioFormat.Voice8KhzMono16Bit.SampleRate:0} ms " +
                "of live speaker-bound RX audio; call state and TAR observation continued.")).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await RunOnUiThreadAsync(() => AddDebugLog(
                DateTimeOffset.Now,
                "RX",
                DebugLogSeverity.Warning,
                $"Unable to release the cold Bluetooth RX transition gate: {exception.Message}")).ConfigureAwait(false);
        }
    }

    private async Task StopTransmitAsync(ChannelViewModel channel)
        => await StopTransmitAsync([channel]).ConfigureAwait(false);

    private async Task StopTransmitAsync(IReadOnlyCollection<ChannelViewModel> channels)
    {
        (ChannelViewModel Channel, uint StreamId)[] activeStreams = channels
            .Select(channel => (
                channel,
                transmitCoordinator.GetActiveStreamId(new ChannelId(channel.SessionId))))
            .Where(entry => entry.Item2 != 0)
            .ToArray();
        Exception? stopFailure = null;
        try
        {
            await Task.Run(() => transmitCoordinator.StopAsync()).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // A failed audio-device stop or final FNE terminator must release
            // the UI call state without escaping through an async-void PTT
            // pointer/key callback and terminating the desktop process.
            stopFailure = exception;
        }
        finally
        {
            try
            {
                await RestoreSuspendedAudioAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                stopFailure ??= exception;
            }
            await RunOnUiThreadAsync(() =>
            {
                foreach (ChannelViewModel channel in channels)
                {
                    channel.SetTransmitEnabled(false);
                    callRecordings.StopTransmit(channel);
                }
                foreach ((ChannelViewModel channel, uint streamId) in activeStreams)
                {
                    SystemViewModel? system = Systems.FirstOrDefault(candidate => candidate.Channels.Contains(channel));
                    if (system is not null)
                    {
                        AddDebugLog(
                            DateTimeOffset.Now,
                            system.Name,
                            DebugLogSeverity.Info,
                            $"TX call ended on {channel.Name}: {ProtocolFor(channel).ToString().ToUpperInvariant()} " +
                            $"stream {streamId}.");
                        callHistory.CompleteConsoleTransmission(
                            system.Name,
                            ProtocolFor(channel),
                            streamId,
                            DateTimeOffset.Now,
                            channel.Name,
                            channel.Definition.DestinationId);
                    }
                }
                if (activeStreams.Length > 0)
                    NotifyCallHistoryChanged();
                TransmitStatusText = stopFailure is null
                    ? "PTT idle."
                    : $"Transmission stopped safely after an error: {stopFailure.Message}";
            }).ConfigureAwait(false);
        }
    }

    public async Task TestTalkPermitToneAsync()
        => await PlayTalkPermitToneAsync(reportSuccess: true).ConfigureAwait(false);

    private async Task PlayTalkPermitToneAsync(
        bool reportSuccess,
        bool requiredForTransmit = false,
        bool microphoneStartedCold = false,
        bool? microphoneIsBluetooth = false,
        Task<LocalTonePlaybackResult>? preparedPlayback = null,
        Stopwatch? pttTimer = null,
        TimeSpan? transmitSessionsReadyAt = null,
        TimeSpan? cueBarrierReleasedAt = null,
        TimeSpan? microphoneReadyAt = null)
    {
        try
        {
            LocalTonePlaybackResult result = preparedPlayback is not null
                ? await preparedPlayback.ConfigureAwait(false)
                : requiredForTransmit
                    ? await localTonePlayer.PlayTalkPermitAsync(
                        microphoneStartedCold,
                        microphoneIsBluetooth).ConfigureAwait(false)
                    : await localTonePlayer.PlayAsync(LocalToneCues.TalkPermit).ConfigureAwait(false);
            string drainText = result.QueuedSamples is int queued &&
                               result.ConsumedSamples is int consumed
                ? $" queued {FormatAudioLevelDuration(queued)} / consumed {FormatAudioLevelDuration(consumed)}"
                : string.Empty;
            LocalTonePresentationEvidence presentation = result.PresentationEvidence;
            string presentationText = presentation.CallbackConsumptionConfirmed
                ? presentation.WarmupCallbacksBefore is long
                    ? $" Output callback consumption confirmed (warm-up {presentation.WarmupCallbacksBefore}->{presentation.WarmupCallbacksAfter}, " +
                      $"cue {presentation.CueCallbacksBefore}->{presentation.CueCallbacksAfter})."
                    : $" Output callback consumption confirmed (cue {presentation.CueCallbacksBefore}->{presentation.CueCallbacksAfter}; no warm-up)."
                : " Output callback confirmation was not requested for this cue.";
            string presentationLatencyText = result.MeasuredOutputPresentationLatency is TimeSpan outputLatency
                ? $" CoreAudio presentation latency {outputLatency.TotalMilliseconds:0} ms; " +
                  $"post-drain wait {result.PostDrainWaitDuration.TotalMilliseconds:0} ms."
                : result.PostDrainWaitDuration > TimeSpan.Zero
                    ? $" Fixed post-drain wait {result.PostDrainWaitDuration.TotalMilliseconds:0} ms."
                    : " No post-drain wait.";
            string pttText = pttTimer is null
                ? string.Empty
                : $" PTT sessions {transmitSessionsReadyAt?.TotalMilliseconds:0} ms, " +
                  $"cue barrier {cueBarrierReleasedAt?.TotalMilliseconds:0} ms, " +
                  $"microphone ready {microphoneReadyAt?.TotalMilliseconds:0} ms, " +
                  $"permit complete {pttTimer.Elapsed.TotalMilliseconds:0} ms.";
            LocalTonePlaybackTiming timing = result.Timing;
            await RunOnUiThreadAsync(() => AddDebugLog(
                DateTimeOffset.Now,
                "TX",
                DebugLogSeverity.Debug,
                $"Talk permit tone completed on {result.Output.Name} ({result.Output.Id}) " +
                $"after {result.Attempts} playback attempt(s).{drainText}{pttText}{presentationText}" +
                $"{presentationLatencyText} " +
                $"Output preparation: gate {timing.GateAcquired.TotalMilliseconds:0} ms, " +
                $"initial route {timing.InitialRouteResolved.TotalMilliseconds:0} ms, " +
                $"initial open {timing.InitialPlaybackOpened.TotalMilliseconds:0} ms, " +
                $"cue release {timing.CueReleased.TotalMilliseconds:0} ms, " +
                $"final route {timing.OutputRouteConfirmed.TotalMilliseconds:0} ms, " +
                $"final open {timing.FinalPlaybackOpened.TotalMilliseconds:0} ms, " +
                $"warm-up {timing.OutputWarmupDrained.TotalMilliseconds:0} ms, " +
                $"cue queued {timing.CueQueued.TotalMilliseconds:0} ms, " +
                $"cue drained {timing.CueDrained.TotalMilliseconds:0} ms, " +
                $"complete {timing.Completed.TotalMilliseconds:0} ms.")).ConfigureAwait(false);
            if (reportSuccess)
            {
                await RunOnUiThreadAsync(() =>
                    AudioStatusText = $"Talk permit tone sent to {result.Output.Name}.{drainText}").ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            await RunOnUiThreadAsync(() =>
            {
                AddDebugLog(
                    DateTimeOffset.Now,
                    "TX",
                    DebugLogSeverity.Warning,
                    $"Talk permit tone unavailable: {exception}");
                AudioStatusText = $"Talk permit tone unavailable: {exception.Message}";
            }).ConfigureAwait(false);
            if (requiredForTransmit)
            {
                throw new InvalidOperationException(
                    "The talk-permit tone could not be completed, so microphone audio remained muted.",
                    exception);
            }
        }
    }

    private static string DescribeBluetoothState(bool? isBluetooth)
        => isBluetooth switch
        {
            true => "yes",
            false => "no",
            null => "unknown"
        };

    private sealed record TransmitStartupDiagnostics(
        TimeSpan TransmitSessionsReadyAt,
        TimeSpan CueBarrierReleasedAt,
        TimeSpan MicrophoneReadyAt,
        bool MicrophoneStartedCold,
        bool? MicrophoneIsBluetooth,
        MicrophoneReadinessTiming? MicrophoneReadiness);

    private async Task RestoreSuspendedAudioAsync()
    {
        await audioReconfigurationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ChannelViewModel[] channels = suspendedAudioChannels;
            bool keptActive = suspendedAudioKeptActive;
            suspendedAudioChannels = [];
            suspendedAudioKeptActive = false;
            foreach (ChannelViewModel channel in channels)
            {
                if (!channel.IsAudioSuspended)
                    continue;

                if (keptActive && audioCoordinator.IsActive(channel))
                {
                    await audioCoordinator.SetGainAsync(channel, GetChannelVolume(channel)).ConfigureAwait(false);
                    bool enableLivePlayback = receiveOutputMutePolicy.ShouldEnableLivePlayback(
                        channel,
                        isTemporarilySuspended: false);
                    await audioCoordinator.SetLivePlaybackEnabledAsync(channel, enableLivePlayback)
                        .ConfigureAwait(false);
                    await RunOnUiThreadAsync(() => channel.SetAudioSuspended(false)).ConfigureAwait(false);
                }
                else
                    await StartAudioAsync(channel).ConfigureAwait(false);
            }
        }
        finally
        {
            audioReconfigurationLock.Release();
        }
    }

    private async Task MuteReceiveAudioAsync(string statusText)
    {
        await audioReconfigurationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ChannelViewModel[] receivingChannels = ResolveChannels(audioCoordinator.LivePlaybackChannels);
            if (receivingChannels.Length == 0)
                return;

            suspendedAudioChannels = receivingChannels;
            suspendedAudioKeptActive = true;
            await RunOnUiThreadAsync(() =>
            {
                foreach (ChannelViewModel receivingChannel in receivingChannels)
                    receivingChannel.SetAudioSuspended(true);
            }).ConfigureAwait(false);

            foreach (ChannelViewModel receivingChannel in receivingChannels)
            {
                await audioCoordinator
                    .SetLivePlaybackEnabledAsync(receivingChannel, enabled: false)
                    .ConfigureAwait(false);
            }

            await RunOnUiThreadAsync(() => AudioStatusText = statusText).ConfigureAwait(false);
        }
        finally
        {
            audioReconfigurationLock.Release();
        }
    }

}
