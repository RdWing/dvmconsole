// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;

namespace DvmConsole.Desktop;

// Keeps ownership registration and ordered teardown separate from the
// operator-facing view-model behavior. All session resources remain owned by
// ConsoleSessionServices, which rolls them back in reverse registration order.
public sealed partial class MainWindowViewModel
{
    private static void ReportReceiveWorkShutdownDelay(
        string queueName,
        IReadOnlyList<ReceiveWorkerShutdownDiagnostic> diagnostics)
    {
        string details = string.Join(
            Environment.NewLine,
            diagnostics.Select(diagnostic =>
                $"channel={diagnostic.ChannelId}; " +
                $"stream={diagnostic.StreamId?.ToString() ?? "none"}; " +
                $"activity={diagnostic.Activity}; " +
                $"pendingFrames={diagnostic.PendingFrames}; " +
                $"pendingContinuations={diagnostic.PendingContinuations}; " +
                $"cancellationTimedOut={diagnostic.CancellationAcknowledgementTimedOut}"));
        DesktopCrashLog.Write(
            $"{queueName} shutdown delay",
            new TimeoutException(details));
    }

    private void RegisterSessionOwnership(ConsoleSessionServices services)
    {
        services.Presentation.Register("shell-settings", shellSettings.DisposeAsync);
        services.Presentation.Register("operator-undo", operatorUndo.DisposeAsync);
        services.Transmit.Own("ptt-state-change-lock", pttStateChangeLock);
        services.Transmit.Own("transmit-admission-gate", transmitAdmissionGate);
        services.Presentation.Register(
            "channel-subscriptions",
            () => new ValueTask(DetachChannelSubscriptionsAsync()));
        services.Presentation.Register("background-appearance", backgroundAppearance.DisposeAsync);
        services.Recording.Register("call-recording-manager", () => DisposeAsync(callRecordings));
        services.Recording.Register("recording-finalized-subscription", DetachRecordingSubscription);
        services.Audio.Own("reconfiguration-lock", audioReconfigurationLock);
        services.Audio.Register("backend-provider", () => DisposeAsync(audioBackendProvider));
        services.Recording.Register("recording-playback", () => DisposeAsync(recordingPlayback));
        services.Recording.Register("recording-playback-subscription", DetachRecordingPlaybackSubscription);
        services.Audio.Register(
            "web-stream-operator",
            () => webStreamOperator is null ? ValueTask.CompletedTask : webStreamOperator.DisposeAsync());
        services.Audio.Register("receive-audio-coordinator", () => DisposeAsync(audioCoordinator));
        services.Audio.Register("receive-output-failure-subscription", DetachReceiveOutputSubscription);
        services.Patch.Register("routing", DisposePatchRouting);
        services.Patch.Register("source-decode", () => DisposeAsync(patchSourceDecode));
        services.Patch.Register("source-receive-work", () => DisposeAsync(patchSourceReceiveWork));
        services.Receive.Register("audio-work", () => DisposeAsync(receiveAudioWork));
        services.Receive.Register("session-reconciler", () => DisposeAsync(receiveSessionReconciler));
        services.Connection.Register("p25-key-request-coordinator", p25KeyRequestCoordinator.DisposeAsync);
        services.Transmit.Register(
            "coordinators-under-ptt-gate",
            () => new ValueTask(DisposeTransmitCoordinatorsAsync()));
        services.Transmit.Register("ptt-session", () => DisposeAsync(pttSession));
        services.Presentation.Register("view-model-subscriptions", DetachViewModelSubscriptions);
        services.Recording.Register("catalog-scan", () => new ValueTask(DisposeRecordingCatalogScanAsync()));
        services.Audio.Register("default-device-monitor", () => DisposeAsync(defaultAudioDeviceMonitor));
        services.Presentation.Own("debug-log-workspace", debugLogs);
        services.Audio.Register(
            "receive-output-controller",
            () => receiveOutput is null ? ValueTask.CompletedTask : receiveOutput.DisposeAsync());
    }

    private ValueTask DetachRecordingSubscription()
    {
        if (callRecordings is not null)
            callRecordings.RecordingFinalized -= HandleRecordingFinalized;
        return ValueTask.CompletedTask;
    }

    private ValueTask DetachRecordingPlaybackSubscription()
    {
        if (recordingPlayback is not null)
            recordingPlayback.PlaybackStateChanged -= HandleRecordingPlaybackStateChanged;
        return ValueTask.CompletedTask;
    }

    private ValueTask DetachReceiveOutputSubscription()
    {
        if (audioCoordinator is not null)
            audioCoordinator.OutputFailed -= receiveOutput.HandleOutputFailed;
        return ValueTask.CompletedTask;
    }

    private ValueTask DisposePatchRouting()
        => patchRouting is null ? ValueTask.CompletedTask : patchRouting.DisposeAsync();

    private ValueTask DetachViewModelSubscriptions()
    {
        toneTargetRefresh?.Dispose();
        if (transmitCoordinator is not null)
        {
            transmitCoordinator.Faulted -= HandleTransmitFaulted;
            transmitCoordinator.ActiveChannelsChanged -= HandleTransmitAvailabilityChanged;
        }
        if (toneTransmitCoordinator is not null)
            toneTransmitCoordinator.SendingChanged -= HandleGeneratedAudioAvailabilityChanged;
        if (pttSession is not null)
        {
            pttSession.StateChanged -= HandlePttSourceStateChanged;
            pttSession.CaptureFailed -= HandleGlobalPttCaptureFailed;
        }
        if (pttSettings is not null)
            pttSettings.PropertyChanged -= HandlePttSettingsPropertyChanged;
        if (historyRecording is not null)
            historyRecording.PropertyChanged -= HandleHistoryRecordingPropertyChanged;
        if (operatorUndo is not null)
            operatorUndo.Changed -= HandleOperatorUndoChanged;
        if (audioSettings is not null)
            audioSettings.PropertyChanged -= HandleAudioSettingsPropertyChanged;
        if (toneWorkspace is not null)
            toneWorkspace.PropertyChanged -= HandleToneWorkspacePropertyChanged;
        if (debugLogs is not null)
            debugLogs.PropertyChanged -= HandleDebugLogWorkspacePropertyChanged;
        return ValueTask.CompletedTask;
    }

    private async Task DisposeRecordingCatalogScanAsync()
    {
        if (historyRecording is null)
            return;

        var cleanup = new AsyncCleanup();
        RecordingCatalogScanShutdown recordingScan = historyRecording.CancelRecordingCatalogScan();
        await cleanup.RunTaskAsync(() => recordingScan.Scan).ConfigureAwait(false);
        cleanup.Run(() => recordingScan.Cancellation?.Dispose());
        cleanup.ThrowIfFailed();
    }

    private async Task DisposeTransmitCoordinatorsAsync()
    {
        var cleanup = new AsyncCleanup();
        // Cancel and join operations before taking the admission gate they own.
        if (generatedAudioOperation is not null)
            await cleanup.RunTaskAsync(() => generatedAudioOperation.DisposeAsync().AsTask()).ConfigureAwait(false);
        bool pttGateEntered = false;
        bool admissionEntered = false;
        try
        {
            await pttStateChangeLock.WaitAsync().ConfigureAwait(false);
            pttGateEntered = true;
            await transmitAdmissionGate.WaitAsync().ConfigureAwait(false);
            admissionEntered = true;
        }
        catch (Exception exception)
        {
            cleanup.Capture(exception);
        }

        if (pttGateEntered)
        {
            try
            {
                if (toneTransmitCoordinator is not null)
                {
                    await cleanup.RunTaskAsync(
                        () => toneTransmitCoordinator.DisposeAsync().AsTask()).ConfigureAwait(false);
                }
                if (generatedAudioMonitor is not null)
                {
                    await cleanup.RunTaskAsync(
                        () => generatedAudioMonitor.DisposeAsync().AsTask()).ConfigureAwait(false);
                }
                if (localTonePlayer is not null)
                {
                    await cleanup.RunTaskAsync(
                        () => localTonePlayer.DisposeAsync().AsTask()).ConfigureAwait(false);
                }
                if (warmMicrophoneReconciler is not null)
                {
                    cleanup.Run(() => warmMicrophoneReconciler.Reconciled -= HandleWarmMicrophoneReconciled);
                    await cleanup.RunTaskAsync(warmMicrophoneReconciler.WhenIdleAsync).ConfigureAwait(false);
                }
                if (transmitCoordinator is not null)
                {
                    await cleanup.RunTaskAsync(
                        () => transmitCoordinator.DisposeAsync().AsTask()).ConfigureAwait(false);
                }
            }
            finally
            {
                if (admissionEntered)
                    transmitAdmissionGate.Release();
                pttStateChangeLock.Release();
            }
        }

        cleanup.ThrowIfFailed();
    }

    private async Task DisposeSystemsAsync()
    {
        var cleanup = new AsyncCleanup();
        radioIngress.TrafficReceived -= HandleSubscribedSystemTraffic;
        radioIngress.AuthorityChanged -= HandleSystemTalkgroupAuthorityChanged;
        foreach (SystemViewModel system in Systems)
        {
            cleanup.Run(() =>
            {
                system.JitterBufferChanged -= HandleSystemJitterBufferChanged;
                system.PropertyChanged -= HandleSystemPropertyChanged;
                system.StatusChanged -= HandleSubscribedSystemStatus;
                system.KeyResponseReceived -= HandleSystemKeyResponse;
                system.LogReceived -= HandleSystemLog;
            });
        }

        await cleanup.RunTasksAsync(
            Systems.Select(system => (Func<Task>)(() => system.DisposeAsync().AsTask())))
            .ConfigureAwait(false);
        cleanup.ThrowIfFailed();
    }

    private Task DetachChannelSubscriptionsAsync()
    {
        var cleanup = new AsyncCleanup();
        foreach (ChannelViewModel channel in Systems.SelectMany(system => system.Channels))
        {
            cleanup.Run(() =>
            {
                channel.TransmitEncryptionChanged -= HandleChannelEncryptionChanged;
                channel.RecordingStateChanged -= HandleChannelRecordingChanged;
                channel.SelectionChanged -= HandleChannelSelectionChanged;
                channel.VolumeChanged -= HandleChannelVolumeChanged;
                channel.StereoBalanceChanged -= HandleChannelStereoBalanceChanged;
                channel.PropertyChanged -= HandleActivityChannelPropertyChanged;
                channel.PropertyChanged -= HandleToneTargetChannelPropertyChanged;
            });
        }
        cleanup.ThrowIfFailed();
        return Task.CompletedTask;
    }

    private static ValueTask DisposeAsync(IAsyncDisposable? disposable)
        => disposable is null ? ValueTask.CompletedTask : disposable.DisposeAsync();
}
