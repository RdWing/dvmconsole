using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class BoundedShutdownTests
{
    [Fact]
    public async Task CompletesWhenCleanupFinishesWithinTheDeadline()
    {
        await BoundedShutdown.RunAsync(
            () => Task.CompletedTask,
            TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task TimesOutInsteadOfHoldingTheApplicationOpenForever()
    {
        var neverCompletes = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TimeoutException exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            BoundedShutdown.RunAsync(
                () => neverCompletes.Task,
                TimeSpan.FromMilliseconds(25)));

        Assert.Contains("cleanup", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(neverCompletes.Task.IsCompleted);
    }

    [Fact]
    public async Task DeadlineCoversTheEntireCleanupSequence()
    {
        var firstStep = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bool laterStepStarted = false;

        await Assert.ThrowsAsync<TimeoutException>(() =>
            BoundedShutdown.RunAsync(
                async () =>
                {
                    await firstStep.Task;
                    laterStepStarted = true;
                },
                TimeSpan.FromMilliseconds(25)));

        Assert.False(laterStepStarted);
    }
}
