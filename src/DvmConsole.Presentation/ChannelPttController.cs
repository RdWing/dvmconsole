using DvmConsole.Application;

namespace DvmConsole.Presentation;

// Renderer-neutral PTT intent reconciler shared by Cards and List. Every
// lifecycle exit converges on ReleaseAllAsync, which is safe to call more than
// once and while a slow transmitter is still starting.
public sealed class ChannelPttController : IAsyncDisposable
{
    private readonly Func<ChannelId, CancellationToken, ValueTask<bool>> start;
    private readonly Func<ChannelId, CancellationToken, ValueTask> stop;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private readonly HashSet<ChannelId> held = [];
    private readonly HashSet<ChannelId> latched = [];
    private readonly HashSet<ChannelId> active = [];
    private Task? disposeTask;
    private bool disposed;

    public ChannelPttController(
        Func<ChannelId, CancellationToken, ValueTask<bool>> start,
        Func<ChannelId, CancellationToken, ValueTask> stop)
    {
        this.start = start ?? throw new ArgumentNullException(nameof(start));
        this.stop = stop ?? throw new ArgumentNullException(nameof(stop));
    }

    public async ValueTask PressAsync(
        ChannelId channelId,
        CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            if (disposed || !held.Add(channelId))
                return;
        }
        await ReconcileAsync(channelId, cancellationToken);
    }

    public async ValueTask ReleaseAsync(
        ChannelId channelId,
        CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            if (disposed || !held.Remove(channelId))
                return;
        }
        await ReconcileAsync(channelId, cancellationToken);
    }

    public async ValueTask ToggleAsync(
        ChannelId channelId,
        CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            if (disposed)
                return;
            if (!latched.Add(channelId))
                latched.Remove(channelId);
        }
        await ReconcileAsync(channelId, cancellationToken);
    }

    public async ValueTask ReleaseAllAsync(CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            held.Clear();
            latched.Clear();
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            ChannelId[] activeChannels;
            lock (sync)
            {
                activeChannels = active.ToArray();
                active.Clear();
            }

            Exception? firstFailure = null;
            foreach (ChannelId channelId in activeChannels)
            {
                try
                {
                    await stop(channelId, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    firstFailure ??= exception;
                }
            }
            if (firstFailure is not null)
                throw firstFailure;
        }
        finally
        {
            gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            disposeTask ??= DisposeCoreAsync();
            return new ValueTask(disposeTask);
        }
    }

    private async ValueTask ReconcileAsync(ChannelId channelId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            bool shouldBeActive;
            bool isActive;
            lock (sync)
            {
                if (disposed)
                    return;
                shouldBeActive = held.Contains(channelId) || latched.Contains(channelId);
                isActive = active.Contains(channelId);
            }
            if (shouldBeActive == isActive)
                return;

            if (shouldBeActive)
            {
                bool started = await start(channelId, cancellationToken);
                lock (sync)
                {
                    if (started)
                        active.Add(channelId);
                    else
                        latched.Remove(channelId);
                }
            }
            else
            {
                await stop(channelId, cancellationToken);
                lock (sync)
                    active.Remove(channelId);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task DisposeCoreAsync()
    {
        lock (sync)
            disposed = true;
        await ReleaseAllAsync(CancellationToken.None);
    }
}
