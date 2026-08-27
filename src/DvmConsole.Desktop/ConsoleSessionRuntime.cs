using Avalonia.Threading;

namespace DvmConsole.Desktop;

internal sealed class ConsoleSessionRuntime : IAsyncDisposable
{
    private readonly ConsoleSessionServices services;
    private readonly object timerOwnershipSync = new();
    private readonly DispatcherTimerRegistrationGroup timers = new();
    private bool timerOwnershipRegistered;

    public ConsoleSessionRuntime(ConsoleSessionServices services)
    {
        this.services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public void StartTimer(TimeSpan interval, EventHandler tick)
        => CreateTimer(interval, tick, startImmediately: true);

    public ConsoleSessionTimer CreateTimer(
        TimeSpan interval,
        EventHandler tick,
        bool startImmediately)
    {
        ArgumentNullException.ThrowIfNull(tick);
        var timer = new DispatcherTimer
        {
            Interval = interval
        };
        timer.Tick += tick;
        var registration = new ConsoleSessionTimer(timer, tick);
        try
        {
            EnsureTimerOwnership();
            if (startImmediately)
                registration.Start();
            timers.Add(registration);
            return registration;
        }
        catch
        {
            registration.Dispose();
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

    internal sealed class ConsoleSessionTimer(
        DispatcherTimer timer,
        EventHandler tick) : IDisposable
    {
        public bool IsRunning => timer.IsEnabled;

        public void Start()
            => timer.Start();

        public void Stop()
            => timer.Stop();

        public void Dispose()
        {
            timer.Stop();
            timer.Tick -= tick;
        }
    }

    private sealed class DispatcherTimerRegistrationGroup : IDisposable
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
