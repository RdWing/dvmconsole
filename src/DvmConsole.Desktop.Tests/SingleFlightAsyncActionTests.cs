// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class SingleFlightAsyncActionTests
{
    [Fact]
    public async Task CoalescesRepeatedRequestsIntoOnePendingPass()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        await using var action = new SingleFlightAsyncAction(async cancellationToken =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
            }
        });

        action.Request();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (int index = 0; index < 20; index++)
            action.Request();
        releaseFirst.TrySetResult();

        await WaitForAsync(() => Volatile.Read(ref calls) == 2);
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task DisposalCancelsAndAwaitsTheOwnedWorker()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var action = new SingleFlightAsyncAction(async cancellationToken =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        action.Request();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await action.DisposeAsync();
        action.Request();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}
