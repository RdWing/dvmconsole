using DvmConsole.Application;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class RadioConnectionCoordinatorTests
{
    [Fact]
    public async Task OwnsEndpointLifecycleAndDependentServiceOrderingBySystemId()
    {
        var operations = new List<string>();
        bool active = false;
        SystemId systemId = SystemId.FromName("North");
        var coordinator = new RadioConnectionCoordinator(
            [new RadioConnectionEndpoint(
                systemId,
                "North",
                () => active,
                _ =>
                {
                    active = true;
                    operations.Add("start-radio");
                    return ValueTask.CompletedTask;
                },
                _ =>
                {
                    active = false;
                    operations.Add("stop-radio");
                    return ValueTask.CompletedTask;
                })],
            _ =>
            {
                operations.Add("sync-dependent");
                return ValueTask.CompletedTask;
            },
            _ =>
            {
                operations.Add("stop-dependent");
                return ValueTask.CompletedTask;
            },
            () => operations.Add("stop-forwarding"),
            busy => operations.Add($"busy-{busy}"),
            transition => operations.Add($"transition-{transition.Kind}"));

        await coordinator.ConnectAsync();
        await coordinator.ToggleAsync(systemId);

        Assert.Equal(
            [
                "busy-True",
                "transition-StartingAll",
                "start-radio",
                "sync-dependent",
                "transition-StartedAll",
                "busy-False",
                "transition-StoppingSystem",
                "stop-radio",
                "transition-SystemStopped"
            ],
            operations);
        Assert.False(active);
    }

    [Fact]
    public async Task DisconnectCancelsInFlightStartupBeforeStoppingDependencies()
    {
        var startupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new RadioConnectionCoordinator(
            [],
            async cancellationToken =>
            {
                startupEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
            _ =>
            {
                stopObserved.TrySetResult();
                return ValueTask.CompletedTask;
            },
            () => { },
            _ => { },
            _ => { });

        Task startup = coordinator.ConnectAsync();
        await startupEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await coordinator.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup);
        Assert.True(stopObserved.Task.IsCompletedSuccessfully);
    }
}
