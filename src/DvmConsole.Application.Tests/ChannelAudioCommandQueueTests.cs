// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ChannelAudioCommandQueueTests
{
    [Fact]
    public async Task FastStorageStillCoalescesABurstFollowingTheInitialValue()
    {
        var state = new ChannelOperatorState();
        List<double> saved = [];
        var controller = new ChannelAudioSettingsController(_ => state,
            (_, change, _) => { saved.Add(change.Gain!.Value); return ValueTask.CompletedTask; },
            (_, _, _) => Task.CompletedTask, (_, _, _) => Task.CompletedTask, () => false);
        var queue = new ChannelAudioCommandQueue((action, token) => new(action(token)), () => controller, TimeProvider.System, SystemApplicationDelay.Instance);
        await queue.SetAsync(default, .5, false, default);
        Task[] requests = Enumerable.Range(1, 100).Select(index => queue.SetAsync(default, index / 100d, false, default).AsTask()).ToArray();
        await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([.5, 1], saved);
        Assert.Equal(1, state.Snapshot.Gain);
    }

    [Fact]
    public async Task DragSharesPendingWritesAndFlushJoinsLatestDurableValue()
    {
        var firstSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new ChannelOperatorState();
        List<double> saved = [];
        var controller = new ChannelAudioSettingsController(_ => state, async (_, change, _) =>
        {
            saved.Add(change.Gain!.Value);
            await (saved.Count == 1 ? firstSave.Task : lastSave.Task);
        }, (_, _, _) => Task.CompletedTask, (_, _, _) => Task.CompletedTask, () => false);
        var queue = new ChannelAudioCommandQueue((action, token) => new(action(token)), () => controller, TimeProvider.System, SystemApplicationDelay.Instance);
        Task first = queue.SetAsync(default, 0.5, false, default).AsTask();
        Task[] drag = Enumerable.Range(1, 100).Select(index => queue.SetAsync(default, index / 100d, false, default).AsTask()).ToArray();
        Task flush = queue.FlushAsync(default);
        Assert.False(flush.IsCompleted);
        firstSave.SetResult();
        await first;
        Assert.False(flush.IsCompleted);
        lastSave.SetResult();
        await Task.WhenAll(drag).WaitAsync(TimeSpan.FromSeconds(5));
        await flush.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([0.5, 1], saved);
        Assert.Equal(1, state.Snapshot.Gain);
    }

    [Fact]
    public async Task FailedWriteDoesNotPublishAndCanceledPendingValueDoesNotReplaceLatestIntent()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new ChannelOperatorState();
        int saves = 0;
        var controller = new ChannelAudioSettingsController(_ => state, (_, _, _) =>
        {
            saves++;
            throw new IOException("write failed");
        }, (_, _, _) => Task.CompletedTask, (_, _, _) => Task.CompletedTask, () => false);
        var queue = new ChannelAudioCommandQueue(async (action, token) => { await gate.Task; await action(token); }, () => controller, TimeProvider.System, SystemApplicationDelay.Instance);
        Task first = queue.SetAsync(default, 2, false, default).AsTask();
        using var canceled = new CancellationTokenSource();
        Task second = queue.SetAsync(default, 3, false, canceled.Token).AsTask();
        canceled.Cancel();
        gate.SetResult();
        await Assert.ThrowsAsync<IOException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        await queue.FlushAsync(default);
        Assert.Equal(1, saves);
        Assert.Equal(1, state.Snapshot.Gain);
    }
}
