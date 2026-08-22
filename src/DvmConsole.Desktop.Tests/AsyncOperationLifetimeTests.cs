using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class AsyncOperationLifetimeTests
{
    [Fact]
    public async Task StopWaitsForEveryAcquiredOperation()
    {
        var lifetime = new AsyncOperationLifetime();
        Assert.True(lifetime.TryAcquire());
        Assert.True(lifetime.TryAcquire());

        lifetime.BeginStop();
        Task idle = lifetime.WaitForIdleAsync();
        Assert.False(idle.IsCompleted);
        Assert.False(lifetime.TryAcquire());

        lifetime.Release();
        Assert.False(idle.IsCompleted);
        lifetime.Release();

        await idle.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task StopWithoutOperationsCompletesImmediately()
    {
        var lifetime = new AsyncOperationLifetime();

        lifetime.BeginStop();

        await lifetime.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(lifetime.TryAcquire());
    }
}
