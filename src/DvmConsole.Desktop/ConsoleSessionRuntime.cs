using DvmConsole.Application;

namespace DvmConsole.Desktop;

internal sealed class ConsoleSessionRuntime : IAsyncDisposable
{
    private readonly ConsoleSessionServices services;
    private readonly IApplicationScheduler scheduler;
    private readonly object timerOwnershipSync = new();
    private readonly ScheduledWorkRegistrationGroup timers = new();
    private bool timerOwnershipRegistered;

    public ConsoleSessionRuntime(
        ConsoleSessionServices services,
        IApplicationScheduler? scheduler = null)
    {
        this.services = services ?? throw new ArgumentNullException(nameof(services));
        this.scheduler = scheduler ?? AvaloniaApplicationScheduler.Instance;
    }

    public void StartTimer(TimeSpan interval, EventHandler tick)
        => CreateTimer(interval, tick, startImmediately: true);

    public ConsoleSessionTimer CreateTimer(
        TimeSpan interval,
        EventHandler tick,
        bool startImmediately)
    {
        ArgumentNullException.ThrowIfNull(tick);
        ConsoleSessionTimer? registration = null;
        try
        {
            EnsureTimerOwnership();
            IScheduledWork work = scheduler.CreatePeriodic(
                interval,
                _ =>
                {
                    tick(this, EventArgs.Empty);
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
            services.Timers.Own("dispatcher-timers", timers);
            timerOwnershipRegistered = true;
        }
    }

    internal sealed class ConsoleSessionTimer(IScheduledWork work) : IDisposable
    {
        public bool IsRunning => work.IsRunning;

        public void Start()
            => work.Start();

        public void Stop()
            => work.Stop();

        public void Dispose()
        {
            work.Stop();
            work.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private sealed class ScheduledWorkRegistrationGroup : IDisposable
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

        public void Dispose()
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
                cleanup.Run(registration.Dispose);
            cleanup.ThrowIfFailed();
        }
    }
}
