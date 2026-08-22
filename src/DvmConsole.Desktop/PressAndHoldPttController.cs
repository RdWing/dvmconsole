namespace DvmConsole.Desktop;

// Serializes a card's press-and-hold PTT lifecycle so release cannot race
// ahead of slower audio/vocoder startup and leave a call keyed.
public sealed class PressAndHoldPttController : IAsyncDisposable
{
    private readonly Func<ChannelViewModel, Task> start;
    private readonly Func<ChannelViewModel, Task> stop;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private readonly HashSet<ChannelViewModel> pressed = [];
    private TaskCompletionSource? disposeCompletion;
    private bool disposed;

    public PressAndHoldPttController(
        Func<ChannelViewModel, Task> start,
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
            if (!pressed.Add(channel))
                return;
        }

        // PTT delegates update Avalonia-bound channel state. Preserve the
        // caller's UI synchronization context when startup is slow (for
        // example while macOS changes an AirPods Bluetooth audio profile).
        // Otherwise an early release can resume on a pool thread and crash
        // when the stop delegate raises CanExecuteChanged.
        await gate.WaitAsync();
        try
        {
            lock (sync)
            {
                if (disposed || !pressed.Contains(channel))
                    return;
            }
            await start(channel);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ReleaseAsync(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        lock (sync)
        {
            if (disposed)
                return;
            if (!pressed.Remove(channel))
                return;
        }

        await gate.WaitAsync();
        try
        {
            await stop(channel);
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
        ChannelViewModel[] pressedChannels;
        lock (sync)
        {
            disposed = true;
            pressedChannels = pressed.ToArray();
            pressed.Clear();
        }

        await gate.WaitAsync();
        try
        {
            Exception? failure = null;
            foreach (ChannelViewModel channel in pressedChannels)
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
