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
    {
        ArgumentNullException.ThrowIfNull(tick);
        var timer = new DispatcherTimer
        {
            Interval = interval
        };
        timer.Tick += tick;
        var registration = new DispatcherTimerRegistration(timer, tick);
        try
        {
            EnsureTimerOwnership();
            timers.AddAndStart(registration);
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

    private sealed class DispatcherTimerRegistration(
        DispatcherTimer timer,
        EventHandler tick) : IDisposable
    {
        public void Start()
            => timer.Start();

        public void Dispose()
        {
            timer.Stop();
            timer.Tick -= tick;
        }
    }

    private sealed class DispatcherTimerRegistrationGroup : IDisposable
    {
        private readonly object sync = new();
        private readonly List<DispatcherTimerRegistration> registrations = [];
        private bool disposed;

        public int Count
        {
            get
            {
                lock (sync)
                    return registrations.Count;
            }
        }

        public void AddAndStart(DispatcherTimerRegistration registration)
        {
            ArgumentNullException.ThrowIfNull(registration);
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                registration.Start();
                registrations.Add(registration);
            }
        }

        public void Dispose()
        {
            DispatcherTimerRegistration[] owned;
            lock (sync)
            {
                if (disposed)
                    return;
                disposed = true;
                owned = registrations.ToArray();
                registrations.Clear();
            }

            var cleanup = new AsyncCleanup();
            foreach (DispatcherTimerRegistration registration in owned)
                cleanup.Run(registration.Dispose);
            cleanup.ThrowIfFailed();
        }
    }
}
