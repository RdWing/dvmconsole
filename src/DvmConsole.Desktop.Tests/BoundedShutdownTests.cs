// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class BoundedShutdownTests
{
    [Fact]
    public async Task CompletesWhenCleanupFinishesWithinTheDeadline()
    {
        await BoundedShutdown.RunAsync(
            [new ShutdownPhase(
                "cleanup",
                TimeSpan.FromSeconds(1),
                _ => Task.CompletedTask)],
            TimeSpan.FromSeconds(1),
            () => { });
    }

    [Fact]
    public async Task TimedOutPhaseIsCancelledAndDoesNotBlockLaterSafetyWork()
    {
        var neverCompletes = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bool laterPhaseStarted = false;
        bool safetyFenceRan = false;

        AggregateException exception = await Assert.ThrowsAsync<AggregateException>(() =>
            BoundedShutdown.RunAsync(
                [
                    new ShutdownPhase(
                        "stalled",
                        TimeSpan.FromMilliseconds(25),
                        cancellationToken =>
                        {
                            cancellationToken.Register(() => cancelled.TrySetResult());
                            return neverCompletes.Task;
                        }),
                    new ShutdownPhase(
                        "later",
                        TimeSpan.FromSeconds(1),
                        _ =>
                        {
                            laterPhaseStarted = true;
                            return Task.CompletedTask;
                        })
                ],
                TimeSpan.FromSeconds(1),
                () =>
                {
                    safetyFenceRan = true;
                }));

        Assert.Contains("failures", exception.Message, StringComparison.OrdinalIgnoreCase);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(neverCompletes.Task.IsCompleted);
        Assert.True(laterPhaseStarted);
        Assert.True(safetyFenceRan);
    }

    [Fact]
    public async Task ThrowingPhaseDoesNotPreventLaterPhases()
    {
        bool laterStepStarted = false;
        bool safetyFenceRan = false;

        AggregateException exception = await Assert.ThrowsAsync<AggregateException>(() =>
            BoundedShutdown.RunAsync(
                [
                    new ShutdownPhase(
                        "failed",
                        TimeSpan.FromSeconds(1),
                        _ => Task.FromException(new IOException("test failure"))),
                    new ShutdownPhase(
                        "later",
                        TimeSpan.FromSeconds(1),
                        _ =>
                        {
                            laterStepStarted = true;
                            return Task.CompletedTask;
                        })
                ],
                TimeSpan.FromSeconds(2),
                () =>
                {
                    safetyFenceRan = true;
                }));

        Assert.Contains("failed", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(laterStepStarted);
        Assert.True(safetyFenceRan);
    }

    [Fact]
    public async Task SynchronouslyBlockingPhaseCannotConsumeTheOverallDeadlineOrSkipTheFence()
    {
        using var release = new ManualResetEventSlim();
        var blockingPhaseFinished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var laterPhaseStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bool safetyFenceRan = false;

        try
        {
            AggregateException exception = await Assert.ThrowsAsync<AggregateException>(() =>
                BoundedShutdown.RunAsync(
                    [
                        new ShutdownPhase(
                            "synchronous-block",
                            TimeSpan.FromMilliseconds(25),
                            _ =>
                            {
                                release.Wait(CancellationToken.None);
                                blockingPhaseFinished.TrySetResult();
                                return Task.CompletedTask;
                            }),
                        new ShutdownPhase(
                            "later",
                            TimeSpan.FromSeconds(1),
                            _ =>
                            {
                                laterPhaseStarted.TrySetResult();
                                return Task.CompletedTask;
                            })
                    ],
                    TimeSpan.FromMilliseconds(500),
                    () => safetyFenceRan = true).WaitAsync(TimeSpan.FromSeconds(10)));

            // The blocked phase is still held, so completion proves that it
            // cannot prevent the fence. A busy worker pool may start later
            // cleanup after the deadline; await that observable attempt.
            Assert.False(blockingPhaseFinished.Task.IsCompleted);
            Assert.True(safetyFenceRan);
            Assert.Contains("synchronous-block", exception.ToString(), StringComparison.Ordinal);
            await laterPhaseStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.Set();
            await blockingPhaseFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task TimedOutWorkRemainsOwnedUntilItEventuallySettles()
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new ShutdownBackgroundWorkRegistry();

        await Assert.ThrowsAsync<AggregateException>(() =>
            BoundedShutdown.RunAsync(
                [new ShutdownPhase(
                    "abandoned",
                    TimeSpan.FromMilliseconds(20),
                    _ => completion.Task)],
                TimeSpan.FromMilliseconds(500),
                () => { },
                registry.Register));

        Assert.Equal(1, registry.Count);
        completion.TrySetResult();
        for (int attempt = 0; attempt < 100 && registry.Count != 0; attempt++)
            await Task.Delay(5);
        Assert.Equal(0, registry.Count);
    }
}
