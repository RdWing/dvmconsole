// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;

namespace DvmConsole.Application;

public sealed partial class ConsoleReceiveSession : IConsoleToneCommands
{
    private Task toneStop = Task.CompletedTask;
    private readonly SemaphoreSlim tonePreparationGate = new(1, 1);
    private CancellationTokenSource tonePreparation = new();
    public bool LocalToneMonitorEnabled { get; set; } = true;

    public Task CancelTonesAsync()
    {
        connectionCues?.Cancel();
        lock (ingressSync)
        {
            if (transmit?.GeneratedOperation is not { } operation) return Task.CompletedTask;
            return toneStop.IsCompleted ? toneStop = CancelToneWorkAsync(operation) : toneStop;
        }
    }

    private async Task CancelToneWorkAsync(GeneratedAudioOperation operation)
    {
        tonePreparation.Cancel();
        try
        {
            await operation.CancelAndDrainAsync().ConfigureAwait(false);
            await tonePreparationGate.WaitAsync().ConfigureAwait(false);
            tonePreparationGate.Release();
        }
        finally
        {
            lock (ingressSync) { tonePreparation.Dispose(); tonePreparation = new(); }
        }
    }

    public async Task SendAlertAudioAsync(AssetId asset, CancellationToken cancellationToken = default)
    {
        CancellationTokenSource pending;
        ConsoleExecutionLease lease;
        lock (ingressSync)
        {
            EnsureToneAdmission();
            lease = state.Execution.TryAcquire(ConsoleTransmitIntent.Tone)!.Value;
            pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, tonePreparation.Token);
        }
        using (pending)
        {
            await tonePreparationGate.WaitAsync(pending.Token).ConfigureAwait(false);
            try
            {
                await using Stream source = await dependencies.Host.Assets.OpenReadAsync(asset, pending.Token).ConfigureAwait(false);
                short[] samples = await Task.Run(() => PcmAudioFileLoader.LoadAsync(source, cancellationToken: pending.Token),
                    pending.Token).ConfigureAwait(false);
                Task<Exception?> sending;
                lock (ingressSync)
                {
                    pending.Token.ThrowIfCancellationRequested();
                    if (!state.Execution.IsCurrent(lease)) throw new OperationCanceledException("Audio preparation was superseded.");
                    EnsureToneAdmission();
                    var selected = CaptureToneTargets(ConsoleToneTargets.Alert);
                    sending = transmit!.GeneratedOperation.SendAsync(selected, samples, pending.Token);
                }
                await sending.ConfigureAwait(false);
                SetStatus("Alert audio transmission completed.");
            }
            finally { tonePreparationGate.Release(); }
        }
    }

    private TransmitTarget[] CaptureToneTargets(ConsoleToneTargets targets)
        => transmitTargets.Capture(transmitChannels.SelectToneChannels(targets));

    public async Task SendToneAsync(GeneratedToneSequence sequence, ConsoleToneTargets targets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        if (!Enum.IsDefined(targets)) throw new ArgumentOutOfRangeException(nameof(targets));
        Task<Exception?> sending;
        CancellationTokenSource pending;
        lock (ingressSync)
        {
            EnsureToneAdmission();
            var selected = CaptureToneTargets(targets);
            // Capture cancellation before dispatch; rendering and encoding must not hold ingress or the UI thread.
            pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, tonePreparation.Token);
            sending = transmit!.GeneratedOperation.SendAsync(selected, sequence, pending.Token);
        }
        using (pending)
        {
            Exception? monitorFailure = await sending.ConfigureAwait(false);
            SetStatus(monitorFailure is null ? "Tone transmission completed." : $"Tone sent; local monitor failed: {monitorFailure.Message}");
        }
    }

    private void EnsureToneAdmission()
    {
        if (transmit is null || IsStopping || Volatile.Read(ref radioTransitions) != 0 || !toneStop.IsCompleted ||
            state.Execution.TryAcquire(ConsoleTransmitIntent.Tone) is null)
            throw new InvalidOperationException("Tone transmission is unavailable while the session is paused or stopping.");
    }

}
