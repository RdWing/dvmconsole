namespace DvmConsole.Desktop;

// Owns mouse PTT intent independently from the window. A single reconciler
// serializes slow audio/vocoder transitions while held and latched input modes
// update only their own state.
public sealed class CardPttController : IAsyncDisposable
{
    private readonly Func<ChannelViewModel, Task<bool>> start;
    private readonly Func<ChannelViewModel, Task> stop;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private readonly HashSet<ChannelViewModel> held = [];
    private readonly HashSet<ChannelViewModel> latched = [];
    private readonly HashSet<ChannelViewModel> active = [];
    private TaskCompletionSource? disposeCompletion;
    private bool disposed;

    public CardPttController(
        Func<ChannelViewModel, Task<bool>> start,
        Func<ChannelViewModel, Task> stop)
    {
        this.start = start ?? throw new ArgumentNullException(nameof(start));
        this.stop = stop ?? throw new ArgumentNullException(nameof(stop));
    }

    public async Task PressAsync(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        lock (sync)
        {
            if (disposed)
                return;
            if (!held.Add(channel))
                return;
        }
        await ReconcileAsync(channel);
    }

    public async Task ReleaseAsync(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        lock (sync)
        {
            if (disposed)
                return;
            if (!held.Remove(channel))
                return;
        }

        await ReconcileAsync(channel);
    }

    public async Task ToggleAsync(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        lock (sync)
        {
            if (disposed)
                return;
            if (!latched.Add(channel))
                latched.Remove(channel);
        }

        await ReconcileAsync(channel);
    }

    private async Task ReconcileAsync(ChannelViewModel channel)
    {
        // PTT delegates update Avalonia-bound channel state. Preserve the
        // caller's UI synchronization context while serialized startup waits,
        // so CanExecuteChanged remains on the UI thread.
        await gate.WaitAsync();
        try
        {
            bool shouldBeActive;
            bool isActive;
            lock (sync)
            {
                if (disposed)
                    return;
                shouldBeActive = held.Contains(channel) || latched.Contains(channel);
                isActive = active.Contains(channel);
            }

            if (shouldBeActive == isActive)
                return;

            if (shouldBeActive)
            {
                bool started = await start(channel);
                lock (sync)
                {
                    if (started)
                    {
                        active.Add(channel);
                    }
                    else
                    {
                        // A rejected toggle (for example, while the channel is
                        // receiving) must not require a second click merely to
                        // clear an internal latch that never keyed the radio.
                        latched.Remove(channel);
                    }
                }
            }
            else
            {
                await stop(channel);
                lock (sync)
                    active.Remove(channel);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        bool startDisposal = false;
        lock (sync)
        {
            if (disposeCompletion is null)
            {
                disposeCompletion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                startDisposal = true;
            }
            completion = disposeCompletion;
        }

        if (startDisposal)
            TaskObservation.Observe(DisposeAndCompleteAsync(completion));
        return new ValueTask(completion.Task);
    }

    private async Task DisposeAndCompleteAsync(TaskCompletionSource completion)
    {
        try
        {
            await DisposeCoreAsync();
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task DisposeCoreAsync()
    {
        lock (sync)
        {
            disposed = true;
            held.Clear();
            latched.Clear();
        }

        await gate.WaitAsync();
        try
        {
            ChannelViewModel[] activeChannels;
            lock (sync)
            {
                activeChannels = active.ToArray();
                active.Clear();
            }

            Exception? failure = null;
            foreach (ChannelViewModel channel in activeChannels)
            {
                try
                {
                    await stop(channel);
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }
            }
            if (failure is not null)
                throw failure;
        }
        finally
        {
            gate.Release();
        }
    }
}
