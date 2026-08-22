using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class LatestBooleanStateReconcilerTests
{
    [Fact]
    public async Task AppliesTheLatestDesiredStateAfterAnInflightRequest()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = new List<bool>();
        int calls = 0;
        var reconciler = new LatestBooleanStateReconciler(async desired =>
        {
            applied.Add(desired);
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
            }
        });

        _ = reconciler.SetDesired(true);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        _ = reconciler.SetDesired(false);
        releaseFirst.TrySetResult();
        LatestBooleanStateResult result = await reconciler.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal([true, false], applied);
        Assert.False(result.Desired);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task StaleFailureDoesNotReplaceTheNewerSuccessfulResult()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var reconciler = new LatestBooleanStateReconciler(async _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
                throw new IOException("stale failure");
            }
        });

        _ = reconciler.SetDesired(true);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        _ = reconciler.SetDesired(false);
        releaseFirst.TrySetResult();
        LatestBooleanStateResult result = await reconciler.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(result.Desired);
        Assert.Null(result.Error);
    }
}
