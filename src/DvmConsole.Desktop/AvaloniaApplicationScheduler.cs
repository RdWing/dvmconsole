using Avalonia.Threading;
using DvmConsole.Application;

namespace DvmConsole.Desktop;

internal sealed class AvaloniaApplicationScheduler : IApplicationScheduler
{
    public static AvaloniaApplicationScheduler Instance { get; } = new();

    private AvaloniaApplicationScheduler()
    {
    }

    public IScheduledWork CreatePeriodic(
        TimeSpan interval,
        Func<CancellationToken, ValueTask> callback,
        bool startImmediately = true)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));
        ArgumentNullException.ThrowIfNull(callback);
        return new AvaloniaScheduledWork(interval, callback, startImmediately);
    }

    private sealed class AvaloniaScheduledWork : IScheduledWork
    {
        private readonly DispatcherTimer timer;
        private readonly Func<CancellationToken, ValueTask> callback;
        private readonly CancellationTokenSource cancellation = new();
        private int callbackRunning;
        private bool disposed;

        public AvaloniaScheduledWork(
            TimeSpan interval,
            Func<CancellationToken, ValueTask> callback,
            bool startImmediately)
        {
            this.callback = callback;
            timer = new DispatcherTimer { Interval = interval };
            timer.Tick += HandleTick;
            if (startImmediately)
                timer.Start();
        }

        public bool IsRunning => timer.IsEnabled;

        public void Start()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            timer.Start();
        }

        public void Stop()
        {
            if (!disposed)
                timer.Stop();
        }

        public ValueTask DisposeAsync()
        {
            if (disposed)
                return ValueTask.CompletedTask;
            disposed = true;
            timer.Stop();
            timer.Tick -= HandleTick;
            cancellation.Cancel();
            cancellation.Dispose();
            return ValueTask.CompletedTask;
        }

        private async void HandleTick(object? sender, EventArgs args)
        {
            if (Interlocked.Exchange(ref callbackRunning, 1) != 0)
                return;
            try
            {
                await callback(cancellation.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Disposal owns scheduler cancellation.
            }
            finally
            {
                Volatile.Write(ref callbackRunning, 0);
            }
        }
    }
}
