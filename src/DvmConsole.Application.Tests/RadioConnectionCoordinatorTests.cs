// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class RadioConnectionCoordinatorTests
{
    [Fact]
    public async Task QueuedStartCannotEnterRetiredSessionButDisconnectStillCleansUp()
    {
        var stoppingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishStopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool retired = false;
        int starts = 0;
        int stops = 0;
        var coordinator = new RadioConnectionCoordinator(
            [new RadioConnectionEndpoint(SystemId.FromName("North"), "North", () => false,
                _ => { starts++; return ValueTask.CompletedTask; },
                _ => { stops++; return ValueTask.CompletedTask; })],
            _ => ValueTask.CompletedTask,
            async _ =>
            {
                stoppingEntered.TrySetResult();
                await finishStopping.Task;
            },
            () => { }, _ => { }, _ => { }, () => retired);

        Task disconnect = coordinator.DisconnectAsync();
        await stoppingEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Task queuedStart = coordinator.ConnectAsync();
        retired = true;
        finishStopping.SetResult();

        await disconnect.WaitAsync(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queuedStart);
        await coordinator.DisconnectAsync();
        Assert.Equal(0, starts);
        Assert.Equal(2, stops);
    }

    [Fact]
    public void EmergencyCloseAttemptsEveryEndpointAndReturnsFailuresWithSystemIdentity()
    {
        var closed = new List<string>();
        var failure = new IOException("Transport close failed");
        RadioConnectionEndpoint Endpoint(string name, Action? abort) => new(
            SystemId.FromName(name), name, () => true,
            _ => throw new InvalidOperationException("Emergency close must not start a radio."),
            _ => throw new InvalidOperationException("Emergency close must not await graceful stop."), abort);
        var coordinator = new RadioConnectionCoordinator([
            Endpoint("First", () => { closed.Add("First"); throw failure; }),
            Endpoint("Optional", null),
            Endpoint("Last", () => closed.Add("Last"))],
            _ => ValueTask.CompletedTask, _ => ValueTask.CompletedTask, () => { }, _ => { }, _ => { });

        RadioConnectionTransition result = Assert.Single(coordinator.Abort());

        Assert.Equal(["First", "Last"], closed);
        Assert.Equal(SystemId.FromName("First"), result.SystemId);
        Assert.Equal("First", result.SystemName);
        Assert.Same(failure, result.Exception);
    }

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

        Assert.Empty(coordinator.CaptureActiveSystemIds());
        await coordinator.ConnectAsync();
        Assert.Equal([systemId], coordinator.CaptureActiveSystemIds());
        await coordinator.ToggleAsync(systemId);
        Assert.Empty(coordinator.CaptureActiveSystemIds());

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

    [Fact]
    public async Task RestoreStartsOnlyThePreviouslyActiveSystemsAndThenSynchronizesDependents()
    {
        var operations = new List<string>();
        SystemId northId = SystemId.FromName("North");
        SystemId southId = SystemId.FromName("South");
        bool northActive = false;
        bool southActive = false;
        var coordinator = new RadioConnectionCoordinator(
            [
                CreateEndpoint(northId, "North", () => northActive, value => northActive = value, operations),
                CreateEndpoint(southId, "South", () => southActive, value => southActive = value, operations)
            ],
            _ =>
            {
                operations.Add("sync-dependent");
                return ValueTask.CompletedTask;
            },
            _ => ValueTask.CompletedTask,
            () => { },
            busy => operations.Add($"busy-{busy}"),
            transition => operations.Add($"transition-{transition.Kind}-{transition.SystemName}"));

        await coordinator.RestoreAsync([southId, southId]);

        Assert.False(northActive);
        Assert.True(southActive);
        Assert.Equal(
            [
                "busy-True",
                "transition-StartingSystem-South",
                "start-South",
                "sync-dependent",
                "busy-False"
            ],
            operations);
    }

    private static RadioConnectionEndpoint CreateEndpoint(
        SystemId id,
        string name,
        Func<bool> isActive,
        Action<bool> setActive,
        ICollection<string> operations)
        => new(
            id,
            name,
            isActive,
            _ =>
            {
                setActive(true);
                operations.Add($"start-{name}");
                return ValueTask.CompletedTask;
            },
            _ =>
            {
                setActive(false);
                operations.Add($"stop-{name}");
                return ValueTask.CompletedTask;
            });
}
