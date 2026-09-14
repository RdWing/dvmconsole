// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Diagnostics;

namespace DvmConsole.Application;

internal interface ITransmitLifecycleTransport
{
    IReadOnlyList<ChannelId> ActiveChannels { get; }
    bool ActiveMicrophoneStartedCold { get; }
    bool? ActiveMicrophoneIsBluetooth { get; }
    uint GetActiveStreamId(ChannelId channel);
    Task<MicrophoneStartExpectation> InspectNextMicrophoneStartAsync(bool? inputIsBluetooth, CancellationToken cancellationToken = default);
    void SetMicrophoneAudioSuppressed(bool suppressed);
    Task StartAsync(IReadOnlyList<TransmitTarget> targets, CancellationToken cancellationToken = default);
    Task<MicrophoneReadinessTiming> WaitForMicrophoneReadyAsync(CancellationToken cancellationToken = default);
    Task ActivateAsync(CancellationToken cancellationToken = default);
    Task<TimeSpan> ReleaseMicrophoneAudioAsync(bool requireFreshRecoveryCallback, TimeSpan postCueSuppressionDuration,
        CancellationToken cancellationToken = default);
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
        Task<LocalTonePlaybackResult> playback, TransmitStartupDiagnostics diagnostics, Func<TimeSpan> getElapsed);
    Task RestoreSuspendedAudioAsync();
}

internal interface ITransmitLifecyclePresentation
{
    DateTimeOffset Now { get; }
    long GetTimestamp();
    TimeSpan GetElapsedTime(long started);
    bool MuteReceiveWhileTransmitting { get; }
    bool? SelectedMicrophoneIsBluetooth { get; }
    Task StartedAsync(IReadOnlyList<TransmitTarget> targets, IReadOnlyList<ChannelId> activeChannels,
        TransmitStartupDiagnostics diagnostics, Func<TimeSpan> getElapsed);
    Task StartFailedAsync(IReadOnlyList<ChannelId> channels, Exception failure);
    Task StoppingAsync(IReadOnlyList<TransmitStream> streams);
    Task StoppedAsync(IReadOnlyList<ChannelId> channels, IReadOnlyList<TransmitStream> streams,
        IReadOnlySet<ChannelId> unresolved, TimeSpan elapsed, Exception? failure, string? statusText);
    void ClearActivation();
    void Log(DateTimeOffset timestamp, string source, DebugLogSeverity severity, string message);
}
