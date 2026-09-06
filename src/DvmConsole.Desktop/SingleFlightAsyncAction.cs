// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Desktop;

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
            if (worker.IsCompleted)
                worker = Task.Run(RunAsync);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task pending;
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            requested = false;
            lifetime.Cancel();
            pending = worker;
        }

        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            lifetime.Dispose();
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            lock (sync)
            {
                if (disposed || !requested)
                    return;
                requested = false;
            }

            try
            {
                await action(lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                faultHandler?.Invoke(exception);
            }
        }
    }
}
