// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Audio;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel : IGeneratedAudioOperationPort
{
    async ValueTask<IAsyncDisposable> IGeneratedAudioOperationPort.EnterTransmitAsync(CancellationToken token)
    {
        await transmitAdmissionGate.WaitAsync(token).ConfigureAwait(false);
        return new TransmitAdmission(transmitAdmissionGate);
    }

    void IGeneratedAudioOperationPort.Validate(IReadOnlyList<TransmitTarget> targets)
    {
        ObjectDisposedException.ThrowIf(IsSessionInputSuppressed, this);
        if (transmitCoordinator.ActiveChannel is not null)
            throw new InvalidOperationException("Release PTT before sending generated audio.");
        ToneTransmitCoordinator.ValidateTargets(targets);
    }

    bool IGeneratedAudioOperationPort.MonitorEnabled => LocalToneMonitorEnabled;
    Task IGeneratedAudioOperationPort.MuteReceiveAsync()
        => MuteReceiveAudioAsync("RX audio muted while sending generated audio.");
    Task IGeneratedAudioOperationPort.RestoreReceiveAsync() => RestoreSuspendedAudioAsync();
    Task IGeneratedAudioOperationPort.MonitorAsync(ReadOnlyMemory<short> samples, CancellationToken token)
        => generatedAudioMonitor.PlayAsync(samples, token);
    Task IGeneratedAudioOperationPort.TransmitAsync(IReadOnlyList<TransmitTarget> targets,
        ReadOnlyMemory<short> samples, GeneratedToneSequence? sequence, CancellationToken token)
        => sequence is null
            ? toneTransmitCoordinator.SendAsync(targets, samples, token)
            : toneTransmitCoordinator.SendAsync(targets, sequence, samples, token);

    private sealed class TransmitAdmission(SemaphoreSlim gate) : IAsyncDisposable
    {
        private int released;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
                gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
