// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel
{
    internal bool IsSessionInputSuppressed
        => operationalRuntime.Admission.IsSuppressed;

    internal void SuppressSessionInputForTransition()
    {
        operationalRuntime.Admission.Suspend();
        transmitRuntime.Manual.CancelStartup();
        SuppressLiveReceiveOutputForShutdown();
    }

    internal void ResumeSessionInputAfterFailedTransition()
    {
        terminalFence.TryRun(() =>
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeStarted) != 0, this);
            // Rollback restores listening and command admission before reconnecting.
            // Operator mute is a separate audio policy and remains unchanged.
            audioCoordinator.SetLivePlaybackDiscarded(discarded: false);
            operationalRuntime.Admission.TryResume();
        });
    }

    internal Task ReleaseAllPttForShutdownAsync(CancellationToken cancellationToken = default)
        => transmitRuntime.ReleaseForShutdownAsync(pttStateChangeLock, transmitAdmissionGate, transmitChannels,
            pttSession.StopAsync, pttSession.ReleaseAllKeyboardToggleLatches, cancellationToken);

    internal void SuppressLiveReceiveOutputForShutdown()
    {
        try
        {
            audioCoordinator.SetLivePlaybackDiscarded(discarded: true);
        }
        catch (ObjectDisposedException)
        {
            // A concurrent completed teardown has already silenced the route.
        }
        catch (Exception exception)
        {
            DesktopCrashLog.Write("Shutdown live receive suppression", exception);
        }
    }

    internal Task DrainAcceptedRecordingWorkAsync(CancellationToken cancellationToken = default)
        => callRecordings.DrainAcceptedWorkAsync(cancellationToken);

    internal void ApplyFinalShutdownSafetyFence()
    {
        operationalRuntime.Admission.Close();
        transmitRuntime.Manual.CancelStartup();
        sessionUiCallbacks.Close();
        foreach (Exception exception in audioBackendProvider.StopImmediately())
            DesktopCrashLog.Write("Shutdown audio safety fence", exception);

        try
        {
            transmitCoordinator.SetMicrophoneAudioSuppressed(suppressed: true);
        }
        catch (ObjectDisposedException)
        {
            // Completed transmit disposal has already closed capture.
        }
        catch (Exception exception)
        {
            DesktopCrashLog.Write("Shutdown microphone safety fence", exception);
        }

        SuppressLiveReceiveOutputForShutdown();
        foreach (RadioConnectionTransition failure in connectionSession.Abort())
            DesktopCrashLog.Write($"Shutdown network safety fence ({failure.SystemName})", failure.Exception!);
    }

    // Session replacement must stop network identity ownership before the new
    // view model becomes reachable from the window. Remaining audio and
    // presentation cleanup may then finish without competing for an FNE peer.
    internal async Task QuiesceFneSessionAsync(CancellationToken cancellationToken = default)
    {
        var cleanup = new AsyncCleanup();
        // Stopping keyboard/serial listeners does not release card PTT or tones.
        // Join all accepted transmit work before a replacement can be published.
        await cleanup.RunTaskAsync(() => ReleaseAllPttForShutdownAsync(cancellationToken)).ConfigureAwait(false);
        await cleanup.RunTaskAsync(() => connectionSession.DisconnectAsync(cancellationToken)).ConfigureAwait(false);
        cleanup.ThrowIfFailed();
    }

    internal IReadOnlyList<SystemId> CaptureActiveFneSystemIds()
        => connectionSession.CaptureActiveSystemIds();

    internal Task RestoreFneSessionAsync(
        IReadOnlyList<SystemId> systemIds,
        CancellationToken cancellationToken = default)
        => connectionSession.RestoreAsync(systemIds, cancellationToken);
}
