// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ConnectionCueWorkerTests
{
    [Fact]
    public async Task RapidChangesKeepOnlyTheLatestPendingCue()
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var played = new List<bool>();
        await using var worker = new ConnectionCueWorker(() => true, async (connected, token) =>
        {
            played.Add(connected);
            if (played.Count == 1)
            {
                first.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            else second.TrySetResult();
        }, exception => throw exception);
        worker.Observe("Alpha", RadioConnectionState.Connected);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (int index = 0; index < 100; index++)
        {
            worker.Observe("Alpha", RadioConnectionState.Disconnected);
            worker.Observe("Alpha", RadioConnectionState.Connected);
        }
        worker.Observe("Alpha", RadioConnectionState.Disconnected);
        release.TrySetResult();
        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.DisposeAsync();
        Assert.Equal([true, false], played);
    }

    [Fact]
    public async Task CancellationStopsActivePlaybackAndDropsPendingEdges()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int plays = 0;
        await using var worker = new ConnectionCueWorker(() => true, async (_, token) =>
        {
            Interlocked.Increment(ref plays);
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, exception => throw exception);
        worker.Observe("Alpha", RadioConnectionState.Connected);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        worker.Observe("Alpha", RadioConnectionState.Disconnected);
        worker.Cancel();
        await worker.DisposeAsync();
        Assert.Equal(1, plays);
    }

    [Fact]
    public async Task EnablingDoesNotReplayAnEdgeObservedWhileDisabled()
    {
        bool enabled = false;
        var played = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var worker = new ConnectionCueWorker(() => enabled, (connected, _) =>
        {
            played.TrySetResult(connected);
            return Task.CompletedTask;
        }, exception => throw exception);
        worker.Observe("Alpha", RadioConnectionState.Connected);
        enabled = true;
        worker.Observe("Alpha", RadioConnectionState.Connected);
        worker.Observe("Alpha", RadioConnectionState.Disconnected);
        Assert.False(await played.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
