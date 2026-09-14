// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>
/// Coalesces repeated requests into one active pass and, at most, one latest
/// follow-up pass. The worker and its cancellation are owned through disposal.
/// </summary>
internal sealed class SingleFlightAsyncAction : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly Func<CancellationToken, Task> action;
    private readonly Action<Exception>? faultHandler;
    private readonly CancellationTokenSource lifetime = new();
    private Task worker = Task.CompletedTask;
    private readonly AsyncDisposal disposal = new();
    private bool running;
    private bool requested;
    private bool disposed;

    public SingleFlightAsyncAction(
        Func<CancellationToken, Task> action,
        Action<Exception>? faultHandler = null)
    {
        this.action = action ?? throw new ArgumentNullException(nameof(action));
        this.faultHandler = faultHandler;
    }

    public void Request()
    {
        lock (sync)
        {
            if (disposed)
                return;
            requested = true;
            if (!running)
            {
                running = true;
                worker = Task.Run(RunAsync);
            }
        }
    }

    public ValueTask DisposeAsync() => disposal.RunAsync(async () =>
    {
        Task pending;
        lock (sync)
        {
            disposed = true;
            requested = false;
            pending = worker;
        }
        var cleanup = new AsyncCleanup();
        try
        {
            await cleanup.RunTaskAsync(() => lifetime.CancelAsync()).ConfigureAwait(false);
            await cleanup.RunTaskAsync(() => pending).ConfigureAwait(false);
            cleanup.ThrowIfFailed();
        }
        finally { lifetime.Dispose(); }
    });

    private async Task RunAsync()
    {
        while (true)
        {
            lock (sync)
            {
                if (disposed || !requested)
                {
                    // Release ownership under the same lock used by Request;
                    // Task completion occurs later and cannot be the admission test.
                    running = false;
                    return;
                }
                requested = false;
            }

            try
            {
                await action(lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                lock (sync) running = false;
                return;
            }
            catch (Exception exception)
            {
                try { faultHandler?.Invoke(exception); }
                catch (Exception reportFailure)
                {
                    System.Diagnostics.Trace.TraceError("Background action failure reporting failed: {0}", reportFailure);
                }
            }
        }
    }
}
