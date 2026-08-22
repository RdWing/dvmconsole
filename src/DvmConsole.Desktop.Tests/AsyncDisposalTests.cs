using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class AsyncDisposalTests
{
    [Fact]
    public async Task EveryCallerWaitsForTheSameCleanup()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposal = new AsyncDisposal();
        int calls = 0;

        Task first = disposal.RunAsync(async () =>
        {
            calls++;
            entered.TrySetResult();
            await release.Task;
        }).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Task second = disposal.RunAsync(() => Task.CompletedTask).AsTask();

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        release.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CleanupContinuesAndReportsAllFailures()
    {
        var cleanup = new AsyncCleanup();
        bool finalStepRan = false;

        cleanup.Run(() => throw new IOException("first"));
        await cleanup.RunTaskAsync(() => Task.FromException(
            new InvalidOperationException("second")));
        cleanup.Run(() => finalStepRan = true);

        AggregateException exception = Assert.Throws<AggregateException>(
            cleanup.ThrowIfFailed);
        Assert.True(finalStepRan);
        Assert.Collection(
            exception.InnerExceptions,
            failure => Assert.IsType<IOException>(failure),
            failure => Assert.IsType<InvalidOperationException>(failure));
    }
}
