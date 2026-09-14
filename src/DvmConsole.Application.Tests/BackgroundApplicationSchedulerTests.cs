// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class BackgroundApplicationSchedulerTests
{
    [Fact]
    public async Task RestartWaitsForThePreviousCallbackAndDisposalDrainsIt()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        CancellationToken firstToken = default;
        var scheduler = new BackgroundApplicationScheduler(exception => throw exception);
        await using IScheduledWork work = scheduler.CreatePeriodic(
            TimeSpan.FromMilliseconds(1), async token =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstToken = token;
                    entered.SetResult();
                    await release.Task;
                }
                else
                {
                    restarted.TrySetResult();
                }
            });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        work.Stop();
        Assert.False(work.IsRunning);
        Assert.True(firstToken.IsCancellationRequested);
        work.Start();
        Assert.True(work.IsRunning);
        Assert.Equal(1, Volatile.Read(ref calls));
        release.SetResult();
        await restarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await work.DisposeAsync();
        Assert.False(work.IsRunning);
        Assert.Throws<ObjectDisposedException>(work.Start);
    }

    [Fact]
    public async Task FaultIsReportedAndLaterTicksStillRun()
    {
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var continued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new InvalidOperationException("tick failed");
        int calls = 0;
        var scheduler = new BackgroundApplicationScheduler(exception => reported.TrySetResult(exception));
        await using IScheduledWork work = scheduler.CreatePeriodic(
            TimeSpan.FromMilliseconds(1), _ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    throw expected;
                continued.TrySetResult();
                return ValueTask.CompletedTask;
            }, startImmediately: false);
        Assert.False(work.IsRunning);
        work.Start();
        Assert.Same(expected, await reported.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await continued.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DisposalCancelsAndAwaitsAnInFlightCallback()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new BackgroundApplicationScheduler(exception => throw exception);
        IScheduledWork work = scheduler.CreatePeriodic(TimeSpan.FromMilliseconds(1), async token =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { canceled.SetResult(); }
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await work.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(canceled.Task.IsCompletedSuccessfully);
        Assert.False(work.IsRunning);
        await work.DisposeAsync();
    }
}
