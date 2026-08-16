using DvmConsole.Core.Configuration;
using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class PressAndHoldPttControllerTests
{
    [Fact]
    public async Task ReleaseWaitsForStartupThenStopsTheSameCall()
    {
        ChannelViewModel channel = CreateChannel();
        var startupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowStartup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<string>();
        var controller = new PressAndHoldPttController(
            async _ =>
            {
                events.Add("start");
                startupEntered.SetResult();
                await allowStartup.Task;
                events.Add("started");
            },
            _ =>
            {
                events.Add("stop");
                return Task.CompletedTask;
            });

        Task press = controller.PressAsync(channel);
        await startupEntered.Task;
        Task release = controller.ReleaseAsync(channel);

        Assert.False(release.IsCompleted);
        allowStartup.SetResult();
        await Task.WhenAll(press, release);
        Assert.Equal(["start", "started", "stop"], events);
    }

    [Fact]
    public async Task DuplicatePointerTransitionsDoNotDuplicateCallLifecycle()
    {
        ChannelViewModel channel = CreateChannel();
        int starts = 0;
        int stops = 0;
        var controller = new PressAndHoldPttController(
            _ =>
            {
                starts++;
                return Task.CompletedTask;
            },
            _ =>
            {
                stops++;
                return Task.CompletedTask;
            });

        await controller.PressAsync(channel);
        await controller.PressAsync(channel);
        await controller.ReleaseAsync(channel);
        await controller.ReleaseAsync(channel);

        Assert.Equal(1, starts);
        Assert.Equal(1, stops);
    }

    [Fact]
    public async Task EarlyReleasePreservesUiContextAfterWaitingForStartup()
    {
        ChannelViewModel channel = CreateChannel();
        var startupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowStartup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uiContext = new InlineSynchronizationContext();
        bool stopRanOnUiContext = false;
        var controller = new PressAndHoldPttController(
            async _ =>
            {
                startupEntered.SetResult();
                await allowStartup.Task;
            },
            _ =>
            {
                stopRanOnUiContext = ReferenceEquals(SynchronizationContext.Current, uiContext);
                return Task.CompletedTask;
            });

        SynchronizationContext? originalContext = SynchronizationContext.Current;
        Task press;
        Task release;
        try
        {
            SynchronizationContext.SetSynchronizationContext(uiContext);
            press = controller.PressAsync(channel);
            await startupEntered.Task;
            release = controller.ReleaseAsync(channel);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(originalContext);
        }

        Assert.False(release.IsCompleted);
        allowStartup.SetResult();
        await Task.WhenAll(press, release);

        Assert.True(stopRanOnUiContext);
        Assert.True(uiContext.PostCount > 0);
    }

    private static ChannelViewModel CreateChannel()
        => new(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "dmr",
            Slot = 1
        });

    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        private int postCount;

        public int PostCount => Volatile.Read(ref postCount);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref postCount);
            SynchronizationContext? originalContext = Current;
            try
            {
                SetSynchronizationContext(this);
                callback(state);
            }
            finally
            {
                SetSynchronizationContext(originalContext);
            }
        }
    }
}
