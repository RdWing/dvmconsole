// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>
/// Runs operational work independently of UI dispatch. This scheduler does not
/// grant an OS background execution entitlement; the host owns that policy.
/// </summary>
public sealed class BackgroundApplicationScheduler(
    Action<Exception> reportFault,
    TimeProvider? timeProvider = null) : IApplicationScheduler
{
    private readonly Action<Exception> reportFault = reportFault ??
        throw new ArgumentNullException(nameof(reportFault));
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public IScheduledWork CreatePeriodic(
        TimeSpan interval,
        Func<CancellationToken, ValueTask> callback,
        bool startImmediately = true)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));
        var work = new PeriodicWork(interval, callback, reportFault, timeProvider);
        if (startImmediately)
            work.Start();
        return work;
    }

    private sealed class PeriodicWork(
        TimeSpan interval,
        Func<CancellationToken, ValueTask> callback,
        Action<Exception> reportFault,
        TimeProvider timeProvider) : IScheduledWork
    {
        private readonly object sync = new();
        private WorkLifetime? active;
        private Task retired = Task.CompletedTask;
        private bool disposed;

        public bool IsRunning
        {
            get { lock (sync) return active is not null; }
        }

        public void Start()
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (active is not null)
                    return;
                var lifetime = new WorkLifetime();
                active = lifetime;
                Task predecessor = retired;
                // The predecessor is awaited even across Stop/Start, so a
                // slow callback can never overlap its replacement.
                retired = Task.Run(() => RunAsync(predecessor, lifetime));
            }
        }

        public void Stop()
        {
            lock (sync)
                StopCore();
        }

        private void StopCore()
        {
            WorkLifetime? lifetime = active;
            active = null;
            // CancelAsync closes admission immediately without invoking arbitrary
            // cancellation callbacks under the ownership lock.
            if (lifetime is not null)
                lifetime.Cancellation = lifetime.Source.CancelAsync();
        }

        public ValueTask DisposeAsync()
        {
            lock (sync)
            {
                disposed = true;
                StopCore();
                return new ValueTask(retired);
            }
        }

        private async Task RunAsync(Task predecessor, WorkLifetime lifetime)
        {
            try
            {
                await predecessor.ConfigureAwait(false);
                using var timer = new PeriodicTimer(interval, timeProvider);
                while (await timer.WaitForNextTickAsync(lifetime.Source.Token).ConfigureAwait(false))
                {
                    lifetime.Source.Token.ThrowIfCancellationRequested();
                    try
                    {
                        await callback(lifetime.Source.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (lifetime.Source.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception exception)
                    {
                        ReportFault(exception);
                    }
                }
            }
            catch (OperationCanceledException) when (lifetime.Source.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                ReportFault(exception);
            }
            finally
            {
                Task cancellation;
                lock (sync)
                {
                    if (ReferenceEquals(active, lifetime))
                        active = null;
                    cancellation = lifetime.Cancellation;
                }
                try { await cancellation.ConfigureAwait(false); }
                catch (Exception exception) { ReportFault(exception); }
                lifetime.Source.Dispose();
            }
        }

        private sealed class WorkLifetime
        {
            public CancellationTokenSource Source { get; } = new();
            public Task Cancellation { get; set; } = Task.CompletedTask;
        }

        private void ReportFault(Exception exception)
        {
            try { reportFault(exception); }
            catch (Exception reportingException)
            {
                System.Diagnostics.Trace.TraceError(
                    "Scheduled work fault reporting failed: {0}; original fault: {1}",
                    reportingException, exception);
            }
        }
    }
}
