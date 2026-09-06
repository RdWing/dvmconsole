// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;

namespace DvmConsole.Application;

// Serializes infrequent lifecycle notifications without running subscriber
// code inline with the state transition that produced them.
internal sealed class OrderedNotificationDispatcher : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly Action<Exception> reportFailure;
    private Task tail = Task.CompletedTask;
    private bool disposed;

    public OrderedNotificationDispatcher(Action<Exception>? reportFailure = null)
    {
        this.reportFailure = reportFailure ?? (exception =>
            Trace.TraceError("Lifecycle notification observer failed: {0}", exception));
    }

    public void Enqueue(Action notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        lock (sync)
        {
            if (disposed)
                return;

            Task predecessor = tail;
            tail = Task.Run(async () =>
            {
                try
                {
                    await predecessor.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    reportFailure(exception);
                }

                try
                {
                    notification();
                }
                catch (Exception exception)
                {
                    reportFailure(exception);
                }
            });
        }
    }

    public Task DrainAsync()
    {
        lock (sync)
            return tail;
    }

    public async ValueTask DisposeAsync()
    {
        Task pending;
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            pending = tail;
        }

        await pending.ConfigureAwait(false);
    }
}
