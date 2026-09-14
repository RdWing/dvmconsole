// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;

namespace DvmConsole.Application;

public sealed class ConsoleSessionRuntime : IAsyncDisposable
{
    private readonly ConsoleSessionServices services;
    private readonly IApplicationScheduler scheduler;
    private readonly Action<Exception> faultHandler;
    private readonly object timerOwnershipSync = new();
    private readonly ScheduledWorkRegistrationGroup timers = new();
    private bool timerOwnershipRegistered;

    public ConsoleSessionRuntime(
        ConsoleSessionServices services,
        IApplicationScheduler scheduler,
        Action<Exception>? faultHandler = null)
    {
        this.services = services ?? throw new ArgumentNullException(nameof(services));
        this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        this.faultHandler = faultHandler ??
            (exception => Trace.TraceError("Scheduled application work failed: {0}", exception));
    }

    public void StartTimer(TimeSpan interval, EventHandler tick)
        => CreateTimer(interval, tick, startImmediately: true);

    public ConsoleSessionTimer CreateTimer(
        TimeSpan interval,
        EventHandler tick,
        bool startImmediately,
        IApplicationScheduler? workScheduler = null)
    {
        ArgumentNullException.ThrowIfNull(tick);
        ConsoleSessionTimer? registration = null;
        try
        {
            EnsureTimerOwnership();
            IScheduledWork work = (workScheduler ?? scheduler).CreatePeriodic(
                interval,
                cancellationToken =>
                {
                    try
                    {
                        tick(this, EventArgs.Empty);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                    }
                    catch (Exception exception)
                    {
                        faultHandler(exception);
                    }
                    return ValueTask.CompletedTask;
                },
                startImmediately);
            registration = new ConsoleSessionTimer(work);
            timers.Add(registration);
            return registration;
        }
        catch
        {
            registration?.Dispose();
            throw;
        }
    }

    internal int ActiveTimerCount => timers.Count;

    public ValueTask DisposeAsync()
        => services.DisposeAsync();

    private void EnsureTimerOwnership()
    {
        lock (timerOwnershipSync)
        {
            if (timerOwnershipRegistered)
                return;
            services.Timers.OwnAsync("scheduled-work", timers);
            timerOwnershipRegistered = true;
        }
    }

    public sealed class ConsoleSessionTimer(IScheduledWork work) : IDisposable, IAsyncDisposable
    {
        public bool IsRunning => work.IsRunning;

        public void Start()
            => work.Start();

        public void Stop()
            => work.Stop();

        public void Dispose()
            => DisposeAsync().AsTask().GetAwaiter().GetResult();

        public ValueTask DisposeAsync()
        {
            work.Stop();
            return work.DisposeAsync();
        }
    }

    private sealed class ScheduledWorkRegistrationGroup : IAsyncDisposable
    {
        private readonly object sync = new();
        private readonly List<ConsoleSessionTimer> registrations = [];
        private bool disposed;

        public int Count
        {
            get
            {
                lock (sync)
                    return registrations.Count;
            }
        }

        public void Add(ConsoleSessionTimer registration)
        {
            ArgumentNullException.ThrowIfNull(registration);
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                registrations.Add(registration);
            }
        }

        public async ValueTask DisposeAsync()
        {
            ConsoleSessionTimer[] owned;
            lock (sync)
            {
                if (disposed)
                    return;
                disposed = true;
                owned = registrations.ToArray();
                registrations.Clear();
            }

            var cleanup = new AsyncCleanup();
            foreach (ConsoleSessionTimer registration in owned)
                cleanup.Run(registration.Stop);
            foreach (ConsoleSessionTimer registration in owned)
                await cleanup.RunTaskAsync(() => registration.DisposeAsync().AsTask()).ConfigureAwait(false);
            cleanup.ThrowIfFailed();
        }
    }
}
