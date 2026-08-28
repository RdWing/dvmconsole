using DvmConsole.Core.Configuration;
using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class CardPttControllerTests
{
    [Fact]
    public async Task ReleaseWaitsForStartupThenStopsTheSameCall()
    {
        ChannelViewModel channel = CreateChannel();
        var startupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowStartup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<string>();
        var controller = new CardPttController(
            async _ =>
            {
                events.Add("start");
                startupEntered.SetResult();
                await allowStartup.Task;
                events.Add("started");
                return true;
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
        var controller = new CardPttController(
            _ =>
            {
                starts++;
                return Task.FromResult(true);
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
    public async Task ToggleRemainsLatchedAcrossPointerReleaseUntilNextPress()
    {
        ChannelViewModel channel = CreateChannel();
        int starts = 0;
        int stops = 0;
        var controller = new CardPttController(
            _ =>
            {
                starts++;
                return Task.FromResult(true);
            },
            _ =>
            {
                stops++;
                return Task.CompletedTask;
            });

        await controller.ToggleAsync(channel);
        await controller.ReleaseAsync(channel);

        Assert.Equal(1, starts);
        Assert.Equal(0, stops);

        await controller.ToggleAsync(channel);

        Assert.Equal(1, starts);
        Assert.Equal(1, stops);
    }

    [Fact]
    public async Task DisposalStopsALatchedCall()
    {
        ChannelViewModel channel = CreateChannel();
        int stops = 0;
        var controller = new CardPttController(
            _ => Task.FromResult(true),
            _ =>
            {
                stops++;
                return Task.CompletedTask;
            });

        await controller.ToggleAsync(channel);
        await controller.DisposeAsync();

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
        var controller = new CardPttController(
            async _ =>
            {
                startupEntered.SetResult();
                await allowStartup.Task;
                return true;
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

    [Fact]
    public async Task DisposalWaitsForStartupThenStopsThePressedChannel()
    {
        ChannelViewModel channel = CreateChannel();
        var startupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowStartup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource<ChannelViewModel>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new CardPttController(
            async _ =>
            {
                startupEntered.TrySetResult();
                await allowStartup.Task;
                return true;
            },
            stoppedChannel =>
            {
                stopped.TrySetResult(stoppedChannel);
                return Task.CompletedTask;
            });

        Task press = controller.PressAsync(channel);
        await startupEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Task dispose = controller.DisposeAsync().AsTask();

        Assert.False(dispose.IsCompleted);
        allowStartup.TrySetResult();
        await Task.WhenAll(press, dispose).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Same(channel, await stopped.Task.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task DisposedControllerIgnoresLaterPointerTransitions()
    {
        ChannelViewModel channel = CreateChannel();
        int starts = 0;
        int stops = 0;
        var controller = new CardPttController(
            _ =>
            {
                starts++;
                return Task.FromResult(true);
            },
            _ =>
            {
                stops++;
                return Task.CompletedTask;
            });
        await controller.DisposeAsync();

        await controller.PressAsync(channel);
        await controller.ReleaseAsync(channel);

        Assert.Equal(0, starts);
        Assert.Equal(0, stops);
    }

    [Fact]
    public async Task RejectedToggleDoesNotRemainLatched()
    {
        ChannelViewModel channel = CreateChannel();
        int attempts = 0;
        var controller = new CardPttController(
            _ => Task.FromResult(++attempts > 1),
            _ => Task.CompletedTask);

        await controller.ToggleAsync(channel);
        await controller.ToggleAsync(channel);

        Assert.Equal(2, attempts);
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
