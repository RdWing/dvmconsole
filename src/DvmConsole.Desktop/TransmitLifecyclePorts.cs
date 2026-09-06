// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Diagnostics;
using System.Diagnostics;

namespace DvmConsole.Desktop;

internal interface ITransmitLifecycleTransport
{
    IReadOnlyList<ChannelId> ActiveChannels { get; }
    bool ActiveMicrophoneStartedCold { get; }
    bool? ActiveMicrophoneIsBluetooth { get; }
    uint GetActiveStreamId(ChannelId channel);
    Task<MicrophoneStartExpectation> InspectNextMicrophoneStartAsync(bool? inputIsBluetooth);
    void SetMicrophoneAudioSuppressed(bool suppressed);
    Task StartAsync(IReadOnlyList<TransmitTarget> targets);
    Task<MicrophoneReadinessTiming> WaitForMicrophoneReadyAsync();
    Task ActivateAsync(CancellationToken cancellationToken = default);
    Task<TimeSpan> ReleaseMicrophoneAudioAsync(bool requireFreshRecoveryCallback, TimeSpan postCueSuppressionDuration);
    Task StopAsync();
}

internal interface ITransmitLifecycleAudio
{
    Task MuteReceiveAudioAsync(string statusText);
    long BeginReceiveTransition();
    Task EndColdBluetoothReceiveTransitionAsync(long discardedAtStart);
    Task<LocalTonePlaybackResult> PreparePermitToneAsync(
        bool microphoneStartedCold, bool? microphoneIsBluetooth, Task cueReleaseBarrier,
        Func<CancellationToken, Task> beforeCueAsync);
    Task CompletePermitToneAsync(
        Task<LocalTonePlaybackResult> playback, TransmitStartupDiagnostics diagnostics, Stopwatch timer);
    Task RestoreSuspendedAudioAsync();
}

internal interface ITransmitLifecyclePresentation
{
    bool MuteReceiveWhileTransmitting { get; }
    bool? SelectedMicrophoneIsBluetooth { get; }
    Task StartedAsync(IReadOnlyList<TransmitTarget> targets, IReadOnlyList<ChannelId> activeChannels,
        TransmitStartupDiagnostics diagnostics, Stopwatch timer);
    Task StartFailedAsync(IReadOnlyList<ChannelId> channels, Exception failure);
    Task StoppingAsync(IReadOnlyList<TransmitStream> streams);
    Task StoppedAsync(IReadOnlyList<ChannelId> channels, IReadOnlyList<TransmitStream> streams,
        IReadOnlySet<ChannelId> unresolved, TimeSpan elapsed, Exception? failure, string? statusText);
    void ClearActivation();
    void Log(DateTimeOffset timestamp, string source, DebugLogSeverity severity, string message);
}

internal sealed class TransmitLifecycleTransport(ChannelTransmitCoordinator coordinator) : ITransmitLifecycleTransport
{
    public IReadOnlyList<ChannelId> ActiveChannels => coordinator.ActiveChannels;
    public bool ActiveMicrophoneStartedCold => coordinator.ActiveMicrophoneStartedCold;
    public bool? ActiveMicrophoneIsBluetooth => coordinator.ActiveMicrophoneIsBluetooth;
    public uint GetActiveStreamId(ChannelId channel) => coordinator.GetActiveStreamId(channel);
    public Task<MicrophoneStartExpectation> InspectNextMicrophoneStartAsync(bool? inputIsBluetooth)
        => coordinator.InspectNextMicrophoneStartAsync(inputIsBluetooth);
    public void SetMicrophoneAudioSuppressed(bool suppressed) => coordinator.SetMicrophoneAudioSuppressed(suppressed);
    public Task StartAsync(IReadOnlyList<TransmitTarget> targets) => coordinator.StartAsync(targets);
    public Task<MicrophoneReadinessTiming> WaitForMicrophoneReadyAsync() => coordinator.WaitForMicrophoneReadyAsync();
    public Task ActivateAsync(CancellationToken cancellationToken = default) => coordinator.ActivateAsync(cancellationToken);
    public Task<TimeSpan> ReleaseMicrophoneAudioAsync(bool requireFreshRecoveryCallback, TimeSpan postCueSuppressionDuration)
        => coordinator.ReleaseMicrophoneAudioAsync(requireFreshRecoveryCallback, postCueSuppressionDuration: postCueSuppressionDuration);
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
        Task<LocalTonePlaybackResult> playback, TransmitStartupDiagnostics diagnostics, Stopwatch timer)
        => transition.PlayTalkPermitToneAsync(
            reportSuccess: false, requiredForTransmit: true,
            microphoneStartedCold: diagnostics.MicrophoneStartedCold,
            microphoneIsBluetooth: diagnostics.MicrophoneIsBluetooth,
            preparedPlayback: playback, pttTimer: timer,
            transmitSessionsReadyAt: diagnostics.TransmitSessionsReadyAt,
            cueBarrierReleasedAt: diagnostics.CueBarrierReleasedAt,
            microphoneReadyAt: diagnostics.MicrophoneReadyAt);
    public Task RestoreSuspendedAudioAsync() => transition.RestoreSuspendedAudioAsync();
}
