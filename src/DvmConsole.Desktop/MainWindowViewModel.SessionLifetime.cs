// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel
{
    internal bool IsSessionInputSuppressed
        => terminalFence.IsClosed || Volatile.Read(ref sessionInputSuppressed) != 0;

    internal void SuppressSessionInputForTransition()
    {
        Volatile.Write(ref sessionInputSuppressed, 1);
        SuppressLiveReceiveOutputForShutdown();
    }

    internal void ResumeSessionInputAfterFailedTransition()
    {
        if (!terminalFence.IsClosed && Volatile.Read(ref disposeStarted) == 0)
            Volatile.Write(ref sessionInputSuppressed, 0);
    }

    internal async Task ReleaseAllPttForShutdownAsync(
        CancellationToken cancellationToken = default)
    {
        var cleanup = new AsyncCleanup();
        await cleanup.RunTaskAsync(() => generatedAudioOperation.CancelAndDrainAsync()).ConfigureAwait(false);
        await cleanup.RunTaskAsync(
            () => pttSession.StopAsync(cancellationToken).AsTask()).ConfigureAwait(false);

        bool gateEntered = false;
        bool admissionEntered = false;
        try
        {
            await pttStateChangeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateEntered = true;
            await transmitAdmissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            admissionEntered = true;
            pttSession.ReleaseAllKeyboardToggleLatches();
            ChannelViewModel[] active = ResolveChannels(transmitCoordinator.ActiveChannels)
                .Concat(Systems
                    .SelectMany(system => system.Channels)
                    .Where(channel => channel.IsTransmitting))
                .Distinct()
                .ToArray();
            if (active.Length > 0)
            {
                await cleanup.RunTaskAsync(() => StopTransmitCoreAsync(
                    active,
                    "Transmission stopped during application shutdown.")).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            cleanup.Capture(exception);
        }
        finally
        {
            if (admissionEntered)
                transmitAdmissionGate.Release();
            if (gateEntered)
                pttStateChangeLock.Release();
        }

        cleanup.ThrowIfFailed();
    }

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
        terminalFence.TryClose();
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
        foreach (SystemViewModel system in Systems)
        {
            try
            {
                system.Abort();
            }
            catch (Exception exception)
            {
                DesktopCrashLog.Write($"Shutdown network safety fence ({system.Name})", exception);
            }
        }
    }

    // Session replacement must stop network identity ownership before the new
    // view model becomes reachable from the window. Remaining audio and
    // presentation cleanup may then finish without competing for an FNE peer.
    internal Task QuiesceFneSessionAsync(CancellationToken cancellationToken = default)
        => connectionSession.DisconnectAsync(cancellationToken);

    internal IReadOnlyList<SystemId> CaptureActiveFneSystemIds()
        => Systems
            .Where(system => system.IsConnectionActive)
            .Select(system => SystemId.FromName(system.Name))
            .ToArray();

    internal Task RestoreFneSessionAsync(
        IReadOnlyList<SystemId> systemIds,
        CancellationToken cancellationToken = default)
        => connectionSession.RestoreAsync(systemIds, cancellationToken);
}
