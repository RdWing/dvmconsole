// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>Retries incomplete listening activation before admitting ordinary recovery.</summary>
public sealed class ListeningActivationCoordinator(
    Func<CancellationToken, Task> activate,
    Func<CancellationToken, Task>? resume = null) : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task> activate = activate ?? throw new ArgumentNullException(nameof(activate));
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly AsyncDisposal disposal = new();
    private bool activated;
    private int stopping;

    public Task ActivateAsync(CancellationToken cancellationToken = default)
        => RunAsync(recover: false, cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken = default)
        => RunAsync(recover: true, cancellationToken);

    private async Task RunAsync(bool recover, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref stopping) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            if (!activated)
            {
                await activate(linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                activated = true;
            }
            else if (recover && resume is not null)
                await resume(linked.Token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public ValueTask DisposeAsync() => disposal.RunAsync(async () =>
    {
        Interlocked.Exchange(ref stopping, 1);
        try { await lifetime.CancelAsync().ConfigureAwait(false); }
        finally
        {
            await gate.WaitAsync().ConfigureAwait(false);
            // Keep the gate valid for callers already waiting with cancellation.
            gate.Release();
            lifetime.Dispose();
        }
    });
}
