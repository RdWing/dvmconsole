namespace DvmConsole.Desktop;

// Coalesces rapid requests and dispatches only the latest action to the UI
// thread. The delay function is injectable so callers can test timing policy
// without sleeping.
internal sealed class DebouncedUiAction : IDisposable
{
    private readonly object sync = new();
    private readonly TimeSpan interval;
    private readonly Action<Action> dispatch;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private CancellationTokenSource? pending;
    private long generation;
    private bool disposed;

    public DebouncedUiAction(
        TimeSpan interval,
        Action<Action> dispatch,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        if (interval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));
        this.interval = interval;
        this.dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        this.delayAsync = delayAsync ?? Task.Delay;
    }

    public void Schedule(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        CancellationTokenSource current;
        CancellationTokenSource? previous;
        long currentGeneration;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            previous = pending;
            current = new CancellationTokenSource();
            pending = current;
            currentGeneration = ++generation;
        }

        previous?.Cancel();
        previous?.Dispose();
        TaskObservation.Observe(ExecuteAsync(action, current, currentGeneration));
    }

    public void Cancel()
    {
        CancellationTokenSource? cancellation;
        lock (sync)
        {
            cancellation = pending;
            pending = null;
            generation++;
        }

        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
        }
        Cancel();
    }

    private async Task ExecuteAsync(
        Action action,
        CancellationTokenSource cancellation,
        long scheduledGeneration)
    {
        try
        {
            await delayAsync(interval, cancellation.Token).ConfigureAwait(false);
            lock (sync)
            {
                if (disposed ||
                    generation != scheduledGeneration ||
                    !ReferenceEquals(pending, cancellation))
                    return;
                pending = null;
            }

            dispatch(() =>
            {
                lock (sync)
                {
                    if (disposed || generation != scheduledGeneration)
                        return;
                }
                action();
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Superseded by a newer request or canceled during disposal.
        }
        finally
        {
            cancellation.Dispose();
        }
    }
}
