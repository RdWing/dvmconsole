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
        => RegisterSessionOwnership(services, PreparedLiveSession.FromViewModel(this));

    // Reserve the same global order before construction. Operational resources
    // belong to preparation; view subscriptions only exist after attachment.
    internal static void RegisterSessionOwnership(ConsoleSessionServices services, PreparedLiveSession prepared)
    {
        ConsoleOperationalRuntime runtime = prepared.Runtime;
        services.Presentation.Register("shell-settings", () => DisposeAsync(prepared.ViewModel?.shellSettings));
        services.Presentation.Register("operator-undo", () => DisposeAsync(prepared.ViewModel?.operatorUndo));
        services.Transmit.Own("ptt-state-change-lock", prepared.PttStateChangeLock);
        services.Transmit.Own("transmit-admission-gate", prepared.TransmitAdmissionGate);
        services.Presentation.Register("channel-subscriptions", () =>
            new ValueTask(prepared.ViewModel?.DetachChannelSubscriptionsAsync() ?? Task.CompletedTask));
        services.Presentation.Register("background-appearance", () => DisposeAsync(prepared.ViewModel?.backgroundAppearance));
        services.Recording.Register("call-recording-manager", () => DisposeAsync(prepared.OwnedRecordings));
        services.Recording.Register("recording-finalized-subscription", () =>
            prepared.ViewModel?.DetachRecordingSubscription() ?? ValueTask.CompletedTask);
        runtime.RegisterRecordingOwnership(services.Recording);
        services.Audio.Own("reconfiguration-lock", prepared.AudioReconfigurationLock);
        services.Audio.Register("backend-provider", () => DisposeAsync(prepared.OwnedAudioBackendProvider));
        runtime.RegisterRecordingPlaybackOwnership("recording-playback",
            () => prepared.RecordingPlayback?.DetachRuntime());
        services.Recording.Register("recording-playback-subscription", () =>
        {
            prepared.UnbindRecordingPlaybackState();
            return prepared.ViewModel?.DetachRecordingPlaybackSubscription() ?? ValueTask.CompletedTask;
        });
        runtime.RegisterWebPlaybackOwnership("web-stream-operator", () =>
            prepared.ViewModel?.webStreamOperator is { } webOperator
                ? webOperator.DisposeAsync()
                : DisposeAsync(prepared.WebPlayback));
        runtime.RegisterReceiveAudioOwnership();
        runtime.RegisterPatchOwnership(() => prepared.ViewModel?.patchRouting?.Dispose());
        runtime.RegisterReceiveWorkOwnership();
        services.Receive.Register("session-reconciler", () => DisposeAsync(prepared.ReceiveReconciler ?? prepared.ViewModel?.receiveSessionReconciler));
        services.Connection.Register("p25-key-retrieval", () =>
            (prepared.KeyRetrieval ?? prepared.ViewModel?.p25KeyRetrieval)?.DisposeAsync() ?? ValueTask.CompletedTask);
        runtime.RegisterTransmitOwnership("coordinators-under-ptt-gate",
            prepared.PttStateChangeLock, prepared.TransmitAdmissionGate,
            () => prepared.ViewModel?.PrepareMicrophoneRetirementAsync() ?? Task.CompletedTask);
        services.Transmit.Register("ptt-session", () => DisposeAsync(prepared.ViewModel?.pttSession));
        services.Presentation.Register("view-model-subscriptions", () =>
            prepared.ViewModel?.DetachViewModelSubscriptions() ?? ValueTask.CompletedTask);
        services.Recording.Register("catalog-scan", () =>
            new ValueTask(prepared.ViewModel?.DisposeRecordingCatalogScanAsync() ?? Task.CompletedTask));
        services.Audio.Register("default-device-monitor", () => DisposeAsync(prepared.ViewModel?.defaultAudioDeviceMonitor));
        services.Presentation.Register("debug-log-workspace", () =>
        {
            prepared.ViewModel?.debugLogs?.Dispose();
            return ValueTask.CompletedTask;
        });
        runtime.RegisterReceiveOutputOwnership();
        runtime.RegisterSnapshotOwnership();

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

    private ValueTask DetachViewModelSubscriptions()
    {
        toneTargetRefresh?.Dispose();
        historyDiagnostics?.Dispose();
        if (channelsById is not null)
            foreach (var channel in channelsById.Values) channel.DetachSessionState();
        if (operationalRuntime is not null && transmitCoordinator is not null)
        {
            transmitCoordinator.Faulted -= HandleTransmitFaulted;
            transmitCoordinator.ActiveChannelsChanged -= HandleTransmitAvailabilityChanged;
        }
        if (operationalRuntime is not null && toneTransmitCoordinator is not null)
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

    private async Task PrepareMicrophoneRetirementAsync()
    {
        if (warmMicrophoneReconciler is null) return;
        var cleanup = new AsyncCleanup();
        cleanup.Run(() => warmMicrophoneReconciler.Reconciled -= HandleWarmMicrophoneReconciled);
        await cleanup.RunTaskAsync(warmMicrophoneReconciler.WhenIdleAsync).ConfigureAwait(false);
        cleanup.ThrowIfFailed();
    }

    private async Task DisposeSystemsAsync(IReadOnlyList<SystemViewModel> ownedSystems)
    {
        var cleanup = new AsyncCleanup();
        foreach (SystemViewModel system in ownedSystems)
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
            ownedSystems.Select(system => (Func<Task>)(() => system.DisposeAsync().AsTask())))
            .ConfigureAwait(false);
        cleanup.ThrowIfFailed();
    }

    internal static async Task DisposePreparedSystemsAsync(
        PreparedLiveSession? prepared, IReadOnlyList<SystemViewModel> ownedSystems)
    {
        if (prepared?.ViewModel is { } view)
        {
            await view.DisposeSystemsAsync(ownedSystems).ConfigureAwait(false);
            return;
        }

        var cleanup = new AsyncCleanup();
        await cleanup.RunTasksAsync(ownedSystems.Select(system =>
            (Func<Task>)(() => system.DisposeAsync().AsTask()))).ConfigureAwait(false);
        cleanup.ThrowIfFailed();
    }

    private Task DetachChannelSubscriptionsAsync()
    {
        var cleanup = new AsyncCleanup();
        foreach (ChannelViewModel channel in (Systems ?? []).SelectMany(system => system.Channels))
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
