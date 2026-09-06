// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class P25KeyRequestCoordinatorTests
{
    [Fact]
    public async Task WaitsForConnectionSettlingAndSpacesEveryConfiguredRequest()
    {
        var delays = new List<TimeSpan>();
        var sent = new List<(byte AlgorithmId, ushort KeyId)>();
        await using var coordinator = new P25KeyRequestCoordinator((delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });
        (byte AlgorithmId, ushort KeyId)[] requests =
        [
            (0x84, 0x0020),
            (0x84, 0x0050),
            (0x84, 0x069D)
        ];

        await coordinator.Schedule(
            "SKYNET",
            requests,
            () => true,
            (algorithmId, keyId) => sent.Add((algorithmId, keyId)),
            (_, _) => true,
            (_, _) => throw new InvalidOperationException("No answered key should be retried."));

        Assert.Equal(requests, sent);
        Assert.Equal(
            [
                P25KeyRequestCoordinator.StartupDelay,
                P25KeyRequestCoordinator.RequestSpacing,
                P25KeyRequestCoordinator.RequestSpacing,
                P25KeyRequestCoordinator.RetryDelay
            ],
            delays);
    }

    [Fact]
    public async Task StopsPacedRequestsWhenTheConnectionIsLost()
    {
        bool connected = true;
        int delayCount = 0;
        var sent = new List<ushort>();
        await using var coordinator = new P25KeyRequestCoordinator((_, _) =>
        {
            delayCount++;
            if (delayCount == 2)
                connected = false;
            return Task.CompletedTask;
        });

        await coordinator.Schedule(
            "SKYNET",
            [(0x84, 0x0020), (0x84, 0x0050)],
            () => connected,
            (_, keyId) => sent.Add(keyId),
            (_, _) => false,
            (_, keyId) => sent.Add(keyId));

        Assert.Equal([(ushort)0x0020], sent);
    }

    [Fact]
    public async Task AFailedRequestDoesNotPreventLaterKeysFromBeingRequested()
    {
        var sent = new List<ushort>();
        var failures = new List<Exception>();
        await using var coordinator = new P25KeyRequestCoordinator((_, _) => Task.CompletedTask);

        await coordinator.Schedule(
            "SKYNET",
            [(0x84, 0x0020), (0x84, 0x0050)],
            () => true,
            (_, keyId) =>
            {
                if (keyId == 0x0020)
                    throw new InvalidOperationException("first request failed");
                sent.Add(keyId);
            },
            (_, _) => true,
            (_, _) => throw new InvalidOperationException("No answered key should be retried."),
            failures.Add);

        Assert.Equal([(ushort)0x0050], sent);
        Assert.IsType<InvalidOperationException>(Assert.Single(failures));
    }

    [Fact]
    public async Task AQueuedStatusCallbackCannotScheduleAfterSessionDisposal()
    {
        var sent = new List<ushort>();
        var coordinator = new P25KeyRequestCoordinator((_, _) => Task.CompletedTask);
        await coordinator.DisposeAsync();

        await coordinator.Schedule(
            "SKYNET",
            [(0x84, 0x0020)],
            () => true,
            (_, keyId) => sent.Add(keyId),
            (_, _) => false,
            (_, keyId) => sent.Add(keyId));

        Assert.Empty(sent);
    }

    [Fact]
    public async Task DisposalCancelsAnOutstandingRequestDelay()
    {
        var delayStarted = new TaskCompletionSource<TimeSpan>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new P25KeyRequestCoordinator((delay, cancellationToken) =>
        {
            delayStarted.TrySetResult(delay);
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        Task schedule = coordinator.Schedule(
            "SKYNET",
            [(0x84, 0x019B)],
            () => true,
            (_, _) => { },
            (_, _) => false,
            (_, _) => { });
        Assert.Equal(
            P25KeyRequestCoordinator.StartupDelay,
            await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)));

        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(schedule.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task RetriesOnlyTheFirstUnansweredKeyAfterTheInitialBatch()
    {
        var delays = new List<TimeSpan>();
        var received = new HashSet<(byte AlgorithmId, ushort KeyId)>();
        var initial = new List<ushort>();
        var retried = new List<ushort>();
        await using var coordinator = new P25KeyRequestCoordinator((delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });
        (byte AlgorithmId, ushort KeyId)[] requests =
        [
            (0x84, 0x019B),
            (0xAA, 0x06F1),
            (0x84, 0x5608)
        ];

        await coordinator.Schedule(
            "SKYNET",
            requests,
            () => true,
            (algorithmId, keyId) =>
            {
                initial.Add(keyId);
                if (keyId != 0x019B)
                    received.Add((algorithmId, keyId));
            },
            (algorithmId, keyId) => received.Contains((algorithmId, keyId)),
            (algorithmId, keyId) =>
            {
                retried.Add(keyId);
                received.Add((algorithmId, keyId));
            });

        Assert.Equal(requests.Select(request => request.KeyId), initial);
        Assert.Equal([(ushort)0x019B], retried);
        Assert.Equal(
            [
                P25KeyRequestCoordinator.StartupDelay,
                P25KeyRequestCoordinator.RequestSpacing,
                P25KeyRequestCoordinator.RequestSpacing,
                P25KeyRequestCoordinator.RetryDelay
            ],
            delays);
    }
}
