namespace DvmConsole.Desktop;

// Serializes a card's press-and-hold PTT lifecycle so release cannot race
// ahead of slower audio/vocoder startup and leave a call keyed.
public sealed class PressAndHoldPttController
{
    private readonly Func<ChannelViewModel, Task> start;
    private readonly Func<ChannelViewModel, Task> stop;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private readonly HashSet<ChannelViewModel> pressed = [];

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
            if (!pressed.Add(channel))
                return;
        }

        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (sync)
            {
                if (!pressed.Contains(channel))
                    return;
            }
            await start(channel).ConfigureAwait(false);
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
            if (!pressed.Remove(channel))
                return;
        }

        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await stop(channel).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
