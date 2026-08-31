using DvmConsole.Application;

namespace DvmConsole.Presentation;

/// <summary>
/// Converges host deactivation, suspension, and shutdown on the same
/// renderer-neutral PTT release operation. A host may raise these events more
/// than once; ChannelPttController makes every release idempotent.
/// </summary>
public sealed class ChannelPttLifecycleBinding : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly IApplicationLifecycle lifecycle;
    private readonly Func<CancellationToken, ValueTask> releaseAll;
    private readonly Action<Exception>? faultHandler;
    private Task releaseTail = Task.CompletedTask;
    private int disposed;

    public ChannelPttLifecycleBinding(
        IApplicationLifecycle lifecycle,
        Func<CancellationToken, ValueTask> releaseAll,
        Action<Exception>? faultHandler = null)
    {
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.releaseAll = releaseAll ?? throw new ArgumentNullException(nameof(releaseAll));
        this.faultHandler = faultHandler;
        lifecycle.Deactivated += HandleReleaseBoundary;
        lifecycle.Suspending += HandleReleaseBoundary;
        lifecycle.Stopping += HandleReleaseBoundary;
    }

    public Task WaitForIdleAsync()
    {
        lock (sync)
            return releaseTail;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        lifecycle.Deactivated -= HandleReleaseBoundary;
        lifecycle.Suspending -= HandleReleaseBoundary;
        lifecycle.Stopping -= HandleReleaseBoundary;
        ScheduleRelease();
        await WaitForIdleAsync().ConfigureAwait(false);
    }

    private void HandleReleaseBoundary(object? sender, EventArgs args)
        => ScheduleRelease();

    private void ScheduleRelease()
    {
        lock (sync)
            releaseTail = ReleaseAfterAsync(releaseTail);
    }

    private async Task ReleaseAfterAsync(Task previous)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // The prior failure was already reported. A later lifecycle event
            // must still retry release for any active channel.
        }

        try
        {
            await releaseAll(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            faultHandler?.Invoke(exception);
        }
    }
}
