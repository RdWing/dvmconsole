// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;

namespace DvmConsole.Application;

public interface IGeneratedAudioOperationPort
{
    ValueTask<IAsyncDisposable> EnterTransmitAsync(CancellationToken cancellationToken);
    void Validate(IReadOnlyList<TransmitTarget> targets);
    bool MonitorEnabled { get; }
    Task MuteReceiveAsync();
    Task RestoreReceiveAsync();
    Task MonitorAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken);
    Task TransmitAsync(IReadOnlyList<TransmitTarget> targets, ReadOnlyMemory<short> samples,
        GeneratedToneSequence? sequence, CancellationToken cancellationToken);
}

// One owner spans admission, receive suspension, monitoring, transmit, and
// restoration. Low-level transmit serialization alone cannot protect this lifecycle.
public sealed class GeneratedAudioOperation : IAsyncDisposable
{
    private readonly IGeneratedAudioOperationPort port;
    private readonly object sync = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private CancellationTokenSource lifetime = new();
    private readonly AsyncDisposal disposal = new();
    private int closing;
    private readonly HashSet<PendingOperation> pending = [];

    private sealed class PendingOperation(TransmitTarget[] targets, CancellationTokenSource cancellation)
    {
        public TransmitTarget[] Targets { get; } = targets;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task? CancellationRequested { get; set; }
    }

    public GeneratedAudioOperation(IGeneratedAudioOperationPort port)
    {
        this.port = port ?? throw new ArgumentNullException(nameof(port));
    }

    public Task<Exception?> SendAsync(IReadOnlyList<TransmitTarget> targets,
        ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
        => RunAsync(targets, samples, null, cancellationToken);

    public Task<Exception?> SendAsync(IReadOnlyList<TransmitTarget> targets,
        GeneratedToneSequence sequence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        return RunAsync(targets, default, sequence, cancellationToken);
    }

    private async Task<Exception?> RunAsync(IReadOnlyList<TransmitTarget> targets,
        ReadOnlyMemory<short> samples, GeneratedToneSequence? sequence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targets);
        PendingOperation operation;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref closing) != 0, this);
            operation = new(targets.ToArray(),
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token));
            pending.Add(operation);
        }
        // Join the serial queue before dispatch so withdrawal reaches pending
        // work and worker scheduling cannot reorder sends.
        try
        {
            await gate.WaitAsync(operation.Cancellation.Token).ConfigureAwait(false);
            try
            {
                return await Task.Run(() => ExecuteAsync(operation.Targets, samples, sequence,
                    operation.Cancellation.Token), CancellationToken.None).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }
        finally
        {
            Task cancellationFinished;
            lock (sync)
            {
                pending.Remove(operation);
                cancellationFinished = operation.CancellationRequested ?? Task.CompletedTask;
            }
            try { await cancellationFinished.ConfigureAwait(false); }
            finally { operation.Cancellation.Dispose(); }
        }
    }

    /// <summary>Cancels captured work for withdrawn channels, including queued sends.</summary>
    public Task CancelTargetsAsync(IReadOnlyCollection<ChannelId> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (channels.Count == 0) return Task.CompletedTask;
        lock (sync)
        {
            return Task.WhenAll(pending.Where(operation =>
                operation.Targets.Any(target => channels.Contains(target.Channel.Id)))
                .Select(operation => operation.CancellationRequested ??= operation.Cancellation.CancelAsync())
                .ToArray());
        }
    }

    private async Task<Exception?> ExecuteAsync(IReadOnlyList<TransmitTarget> targets,
        ReadOnlyMemory<short> samples, GeneratedToneSequence? sequence, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await using IAsyncDisposable admission = await port.EnterTransmitAsync(token).ConfigureAwait(false);
        port.Validate(targets);
        if (sequence is not null)
            samples = sequence.RenderPcm();
        if (samples.IsEmpty)
            throw new ArgumentException("Tone audio cannot be empty.", nameof(samples));
        try
        {
            // Include acquisition in the restoration scope: muting can fail
            // after changing some routes, and failed rollback needs another release.
            await port.MuteReceiveAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Exception? monitorFailure = await GeneratedAudioMonitorSession.RunAsync(
                sequence is not null && port.MonitorEnabled,
                async monitorToken =>
                {
                    using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(monitorToken, token);
                    await port.MonitorAsync(samples, monitorCancellation.Token).ConfigureAwait(false);
                },
                () => port.TransmitAsync(targets, samples, sequence, token)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return monitorFailure;
        }
        finally
        {
            await port.RestoreReceiveAsync().ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref closing, 1);
        return disposal.RunAsync(DisposeCoreAsync);
    }

    // Session replacement can be canceled. Stop the old work without permanently
    // disabling this owner when the previous session is resumed.
    public Task CancelAndDrainAsync() => DrainAsync(final: false);

    private async Task DrainAsync(bool final)
    {
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            CancellationTokenSource previous;
            lock (sync)
            {
                if (!final && Volatile.Read(ref closing) != 0)
                    return;
                previous = lifetime;
                if (!final)
                    lifetime = new CancellationTokenSource();
            }
            try
            {
                await previous.CancelAsync().ConfigureAwait(false);
                await gate.WaitAsync().ConfigureAwait(false);
                gate.Release();
            }
            finally
            {
                previous.Dispose();
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private Task DisposeCoreAsync() => DrainAsync(final: true);
}
