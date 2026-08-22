using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ConsoleSessionRuntimeTests
{
    [Fact]
    public async Task DisposeAsync_PublishesOneCleanupToConcurrentCallers()
    {
        var cleanupStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int cleanupCount = 0;
        var runtime = new ConsoleSessionRuntime(async () =>
        {
            Interlocked.Increment(ref cleanupCount);
            cleanupStarted.SetResult();
            await allowCleanup.Task;
        });

        Task first = runtime.DisposeAsync().AsTask();
        await cleanupStarted.Task;
        Task second = runtime.DisposeAsync().AsTask();

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        allowCleanup.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, cleanupCount);
    }
}
