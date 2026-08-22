using Avalonia.Threading;

namespace DvmConsole.Desktop;

internal sealed class ConsoleSessionRuntime : IAsyncDisposable
{
    private readonly Func<Task> disposeSession;
    private readonly List<DispatcherTimerRegistration> timers = [];
    private readonly AsyncDisposal disposal = new();

    public ConsoleSessionRuntime(Func<Task> disposeSession)
    {
        this.disposeSession = disposeSession ?? throw new ArgumentNullException(nameof(disposeSession));
    }

    public void StartTimer(TimeSpan interval, EventHandler tick)
    {
        ArgumentNullException.ThrowIfNull(tick);
        var timer = new DispatcherTimer
        {
            Interval = interval
        };
        timer.Tick += tick;
        timers.Add(new DispatcherTimerRegistration(timer, tick));
        timer.Start();
    }

    public ValueTask DisposeAsync()
        => disposal.RunAsync(DisposeCoreAsync);

    private async Task DisposeCoreAsync()
    {
        var cleanup = new AsyncCleanup();
        foreach (DispatcherTimerRegistration timer in timers)
            cleanup.Run(timer.Dispose);
        timers.Clear();
        await cleanup.RunTaskAsync(disposeSession).ConfigureAwait(false);
        cleanup.ThrowIfFailed();
    }

    private sealed class DispatcherTimerRegistration(
        DispatcherTimer timer,
        EventHandler tick) : IDisposable
    {
        public void Dispose()
        {
            timer.Stop();
            timer.Tick -= tick;
        }
    }
}
