using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class SessionUiCallbackGateTests
{
    [Fact]
    public void CallbackQueuedBeforeDisposalDoesNotRunAfterDisposalStarts()
    {
        var dispatcher = new QueuedUiDispatcher();
        bool callbackRan = false;
        var gate = new SessionUiCallbackGate(dispatcher);

        gate.Post(() => callbackRan = true);
        gate.Close();
        dispatcher.RunNext();

        Assert.False(callbackRan);
    }

    [Fact]
    public void ActiveSessionCallbackRunsThroughTheDispatcher()
    {
        var dispatcher = new QueuedUiDispatcher();
        bool callbackRan = false;
        var gate = new SessionUiCallbackGate(dispatcher);

        gate.Post(() => callbackRan = true);
        dispatcher.RunNext();

        Assert.True(callbackRan);
    }

    [Fact]
    public void CallbackPostedAfterCloseIsIgnored()
    {
        var dispatcher = new QueuedUiDispatcher();
        var gate = new SessionUiCallbackGate(dispatcher);

        gate.Close();
        gate.Post(() => Assert.Fail("A closed session must not accept callbacks."));

        Assert.False(dispatcher.HasPendingCallbacks);
    }

    [Fact]
    public async Task CloseWaitsForAnExecutingCallback()
    {
        var dispatcher = new QueuedUiDispatcher();
        var gate = new SessionUiCallbackGate(dispatcher);
        using var callbackStarted = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        using var closeStarted = new ManualResetEventSlim();
        using var closeCompleted = new ManualResetEventSlim();
        gate.Post(() =>
        {
            callbackStarted.Set();
            releaseCallback.Wait();
        });

        Task callbackTask = Task.Run(dispatcher.RunNext);
        Assert.True(callbackStarted.Wait(TimeSpan.FromSeconds(5)));
        Task closeTask = Task.Run(() =>
        {
            closeStarted.Set();
            gate.Close();
            closeCompleted.Set();
        });
        Assert.True(closeStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(closeCompleted.Wait(TimeSpan.FromMilliseconds(100)));

        releaseCallback.Set();
        await Task.WhenAll(callbackTask, closeTask);

        Assert.True(closeCompleted.IsSet);
    }

    private sealed class QueuedUiDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> callbacks = new();

        public bool HasPendingCallbacks => callbacks.Count > 0;

        public bool CheckAccess() => false;

        public void Post(Action action, bool background = false)
            => callbacks.Enqueue(action);

        public ValueTask InvokeAsync(Action action)
        {
            callbacks.Enqueue(action);
            return ValueTask.CompletedTask;
        }

        public void RunNext()
            => Assert.IsType<Action>(callbacks.Dequeue())();
    }
}
