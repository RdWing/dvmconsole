// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Diagnostics;

namespace DvmConsole.Application;

internal sealed record TransmitStartRequest(IReadOnlyList<TransmitTarget> Targets, bool PlayPermitTone,
    CancellationToken CancellationToken = default);
internal readonly record struct TransmitStream(ChannelId ChannelId, uint StreamId);
internal sealed record TransmitStartupDiagnostics(
    TimeSpan TransmitSessionsReadyAt,
    TimeSpan CueBarrierReleasedAt,
    TimeSpan MicrophoneReadyAt,
    bool MicrophoneStartedCold,
    bool? MicrophoneIsBluetooth,
    MicrophoneReadinessTiming? MicrophoneReadiness);

/// <summary>
/// Owns microphone/cue ordering and transmit cleanup. Input sequencers retain
/// PTT edge ordering; the session runtime retains backend lifetime ownership.
/// </summary>
internal sealed class TransmitLifecycleCoordinator(
    ITransmitLifecycleTransport transport,
    ITransmitLifecycleAudio audio,
    ITransmitLifecyclePresentation presentation)
{
    public async Task StartAsync(TransmitStartRequest request)
    {
        bool playPermitTone = request.PlayPermitTone;
        IReadOnlyList<TransmitTarget> targets = request.Targets;
        CancellationToken cancellationToken = request.CancellationToken;
        ChannelId[] channels = targets.Select(target => target.Channel.Id).ToArray();
        long startupStarted = presentation.GetTimestamp();
        TimeSpan GetStartupElapsed() => presentation.GetElapsedTime(startupStarted);
        TaskCompletionSource<bool>? cueRelease = null;
        TaskCompletionSource<bool>? transmitActivated = null;
        Task<LocalTonePlaybackResult>? preparedPermitTone = null;
        bool receiveTransitionGateActive = false;
        long receiveTransitionDiscardedAtStart = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Keep the Apple duplex unit alive across PTT so its output mix
            // remains the AEC reference and macOS does not repeatedly remove
            // and recreate the system microphone-mode control.
            if (presentation.MuteReceiveWhileTransmitting)
                await audio.MuteReceiveAudioAsync("RX audio muted while transmitting.");

            MicrophoneStartExpectation microphoneExpectation = await transport
                .InspectNextMicrophoneStartAsync(presentation.SelectedMicrophoneIsBluetooth, cancellationToken)
                .ConfigureAwait(false);
            if (!presentation.MuteReceiveWhileTransmitting &&
                microphoneExpectation.RequiresReceiveTransitionGate)
            {
                receiveTransitionDiscardedAtStart =
                    audio.BeginReceiveTransition();
                receiveTransitionGateActive = true;
            }

            // Never publish operator audio or an ON AIR presentation until
            // fresh physical callbacks prove the selected capture path is
            // ready. This gate applies with or without a permit tone and also
            // re-arms when a warm capture has become stale.
            transport.SetMicrophoneAudioSuppressed(true);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() => transport.StartAsync(targets, cancellationToken), CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan transmitSessionsReadyAt = GetStartupElapsed();
            bool microphoneStartedCold = transport.ActiveMicrophoneStartedCold;
            bool? microphoneIsBluetooth = transport.ActiveMicrophoneIsBluetooth;
            bool actualTransitionRequiresGate =
                microphoneStartedCold && microphoneIsBluetooth != false;
            if (!presentation.MuteReceiveWhileTransmitting &&
                actualTransitionRequiresGate &&
                !receiveTransitionGateActive)
            {
                receiveTransitionDiscardedAtStart =
                    audio.BeginReceiveTransition();
                receiveTransitionGateActive = true;
            }
            else if (receiveTransitionGateActive && !actualTransitionRequiresGate)
            {
                await audio.EndColdBluetoothReceiveTransitionAsync(
                    receiveTransitionDiscardedAtStart).ConfigureAwait(false);
                receiveTransitionGateActive = false;
            }
            MicrophoneReadinessTiming microphoneReadiness;
            TimeSpan cueBarrierReleasedAt;
            if (playPermitTone)
            {
                cueRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                transmitActivated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task<MicrophoneReadinessTiming> microphoneReady = transport.WaitForMicrophoneReadyAsync(cancellationToken);
                preparedPermitTone = audio.PreparePermitToneAsync(
                    microphoneStartedCold,
                    microphoneIsBluetooth,
                    cueRelease.Task,
                    beforeCueAsync: async cueCancellationToken =>
                    {
                        try
                        {
                            using var activation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cueCancellationToken);
                            await transport.ActivateAsync(activation.Token).ConfigureAwait(false);
                            transmitActivated.TrySetResult(true);
                        }
                        catch (Exception exception)
                        {
                            transmitActivated.TrySetException(exception);
                            throw;
                        }
                    });
                microphoneReadiness = await microphoneReady.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                cueBarrierReleasedAt = GetStartupElapsed();
                cueRelease.TrySetResult(true);
            }
            else
            {
                microphoneReadiness = await transport
                    .WaitForMicrophoneReadyAsync(cancellationToken)
                    .ConfigureAwait(false);
                await transport.ActivateAsync(cancellationToken).ConfigureAwait(false);
                cueBarrierReleasedAt = GetStartupElapsed();
            }
            TimeSpan microphoneReadyAt = GetStartupElapsed();
            IReadOnlyList<ChannelId> activeChannels = transport.ActiveChannels;
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
            cancellationToken.ThrowIfCancellationRequested();
            await presentation.StartedAsync(
                targets,
                activeChannels,
                startupDiagnostics,
                GetStartupElapsed).ConfigureAwait(false);
            // A permit tone is an operational readiness indication. Play it
            // only after the final output route is rendering, every selected
            // protocol call is active, and the shared microphone path is
            // ready. Operator audio remains suppressed until it completes.
            if (playPermitTone)
            {
                await audio.CompletePermitToneAsync(
                    preparedPermitTone!, startupDiagnostics, GetStartupElapsed).ConfigureAwait(false);
            }
            bool requirePostTransitionMicrophoneRecovery =
                playPermitTone || (microphoneStartedCold && microphoneIsBluetooth != false);
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan postCueMicrophoneRecovery = await transport
                .ReleaseMicrophoneAudioAsync(
                    requirePostTransitionMicrophoneRecovery,
                    postCueSuppressionDuration: playPermitTone ? TimeSpan.FromMilliseconds(60) : TimeSpan.Zero,
                    cancellationToken)
                .ConfigureAwait(false);
            if (requirePostTransitionMicrophoneRecovery)
            {
                string recoveryContext = playPermitTone
                    ? "after permit-tone output closed"
                    : "after cold Bluetooth startup";
                presentation.Log(
                    presentation.Now,
                    "TX",
                    DebugLogSeverity.Debug,
                    $"Cold Bluetooth microphone resumed {postCueMicrophoneRecovery.TotalMilliseconds:0} ms " +
                    $"{recoveryContext}; operator audio released and watchdog armed.");
            }
            if (receiveTransitionGateActive)
            {
                await audio.EndColdBluetoothReceiveTransitionAsync(
                    receiveTransitionDiscardedAtStart).ConfigureAwait(false);
                receiveTransitionGateActive = false;
            }
        }
        catch (Exception exception)
        {
            cueRelease?.TrySetCanceled();
            transmitActivated?.TrySetCanceled();
            await TransmitAudioTransitionController
                .ObservePreparedPermitToneFailureAsync(preparedPermitTone)
                .ConfigureAwait(false);
            Exception startupFailure = exception;
            try
            {
                await Task.Run(() => transport.StopAsync()).ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                presentation.Log(presentation.Now, "TX", DebugLogSeverity.Warning,
                    $"Transmit startup cleanup also failed: {cleanupException.Message}");
            }
            finally
            {
                transport.SetMicrophoneAudioSuppressed(false);
            }

            if (receiveTransitionGateActive)
            {
                await audio.EndColdBluetoothReceiveTransitionAsync(
                    receiveTransitionDiscardedAtStart).ConfigureAwait(false);
                receiveTransitionGateActive = false;
            }

            try
            {
                await audio.RestoreSuspendedAudioAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                presentation.Log(presentation.Now, "TX", DebugLogSeverity.Warning,
                    $"Receive-audio restoration after transmit startup failure also failed: {cleanupException.Message}");
            }

            await presentation.StartFailedAsync(channels, startupFailure).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(
        IReadOnlyList<ChannelId> channels,
        string? stoppedStatusText = null,
        bool propagateUnconfirmedStop = false)
    {
        // Presentation may need the UI dispatcher. Gate operator audio before
        // that await so microphone gating does not depend on window responsiveness.
        transport.SetMicrophoneAudioSuppressed(true);
        TransmitStream[] activeStreams = channels
            .Select(id => new TransmitStream(id, transport.GetActiveStreamId(id)))
            .Where(stream => stream.StreamId != 0)
            .ToArray();
        long stopStarted = presentation.GetTimestamp();
        if (activeStreams.Length > 0)
            await presentation.StoppingAsync(activeStreams).ConfigureAwait(false);
        Exception? stopFailure = null;
        try
        {
            await Task.Run(() => transport.StopAsync()).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            stopFailure = exception;
        }
        finally
        {
            HashSet<ChannelId> unresolved = transport.ActiveChannels.ToHashSet();
            if (unresolved.Count == 0)
            {
                presentation.ClearActivation();
                try
                {
                    await audio.RestoreSuspendedAudioAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    stopFailure ??= exception;
                }
            }
            await presentation.StoppedAsync(
                channels, activeStreams, unresolved, presentation.GetElapsedTime(stopStarted), stopFailure, stoppedStatusText)
                .ConfigureAwait(false);
        }

        if (propagateUnconfirmedStop && transport.ActiveChannels.Count > 0)
        {
            throw new InvalidOperationException(
                "PTT release was not confirmed; transmission ownership remains active.",
                stopFailure);
        }
    }
}
