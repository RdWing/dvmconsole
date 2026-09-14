// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

internal sealed class TransmitLifecycleTransport(ChannelTransmitCoordinator coordinator) : ITransmitLifecycleTransport
{
    public IReadOnlyList<ChannelId> ActiveChannels => coordinator.ActiveChannels;
    public bool ActiveMicrophoneStartedCold => coordinator.ActiveMicrophoneStartedCold;
    public bool? ActiveMicrophoneIsBluetooth => coordinator.ActiveMicrophoneIsBluetooth;
    public uint GetActiveStreamId(ChannelId channel) => coordinator.GetActiveStreamId(channel);
    public Task<MicrophoneStartExpectation> InspectNextMicrophoneStartAsync(bool? inputIsBluetooth, CancellationToken cancellationToken = default)
        => coordinator.InspectNextMicrophoneStartAsync(inputIsBluetooth, cancellationToken);
    public void SetMicrophoneAudioSuppressed(bool suppressed) => coordinator.SetMicrophoneAudioSuppressed(suppressed);
    public Task StartAsync(IReadOnlyList<TransmitTarget> targets, CancellationToken cancellationToken = default) => coordinator.StartAsync(targets, cancellationToken);
    public Task<MicrophoneReadinessTiming> WaitForMicrophoneReadyAsync(CancellationToken cancellationToken = default)
        => coordinator.WaitForMicrophoneReadyAsync(cancellationToken: cancellationToken);
    public Task ActivateAsync(CancellationToken cancellationToken = default) => coordinator.ActivateAsync(cancellationToken);
    public Task<TimeSpan> ReleaseMicrophoneAudioAsync(bool requireFreshRecoveryCallback, TimeSpan postCueSuppressionDuration,
        CancellationToken cancellationToken = default)
        => coordinator.ReleaseMicrophoneAudioAsync(requireFreshRecoveryCallback, postCueSuppressionDuration: postCueSuppressionDuration,
            cancellationToken: cancellationToken);
    public Task StopAsync() => coordinator.StopAsync();
}

internal sealed class TransmitLifecycleAudio(
    ChannelReceiveAudioCoordinator receive,
    LocalTonePlayer tones,
    TransmitAudioTransitionController transition) : ITransmitLifecycleAudio
{
    public Task MuteReceiveAudioAsync(string statusText) => transition.MuteReceiveAudioAsync(statusText);
    public long BeginReceiveTransition() => receive.SetLivePlaybackDiscarded(discarded: true);
    public Task EndColdBluetoothReceiveTransitionAsync(long discardedAtStart)
        => transition.EndColdBluetoothReceiveTransitionAsync(discardedAtStart);
    public Task<LocalTonePlaybackResult> PreparePermitToneAsync(
        bool microphoneStartedCold, bool? microphoneIsBluetooth, Task cueReleaseBarrier,
        Func<CancellationToken, Task> beforeCueAsync)
        => tones.PlayTalkPermitAsync(microphoneStartedCold, microphoneIsBluetooth, cueReleaseBarrier, beforeCueAsync);
    public Task CompletePermitToneAsync(
        Task<LocalTonePlaybackResult> playback, TransmitStartupDiagnostics diagnostics, Func<TimeSpan> getElapsed)
        => transition.PlayTalkPermitToneAsync(
            reportSuccess: false, requiredForTransmit: true,
            microphoneStartedCold: diagnostics.MicrophoneStartedCold,
            microphoneIsBluetooth: diagnostics.MicrophoneIsBluetooth,
            preparedPlayback: playback, getPttElapsed: getElapsed,
            transmitSessionsReadyAt: diagnostics.TransmitSessionsReadyAt,
            cueBarrierReleasedAt: diagnostics.CueBarrierReleasedAt,
            microphoneReadyAt: diagnostics.MicrophoneReadyAt);
    public Task RestoreSuspendedAudioAsync() => transition.RestoreSuspendedAudioAsync();
}
