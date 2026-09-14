// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class OperatorUndoControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposalJoinsCommitAlreadyStartedByReplacementOrExpiry(bool expire)
    {
        var delay = new ControlledDelay();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int committed = 0;
        var controller = new OperatorUndoController(action => action(), _ => { }, delayAsync: delay.WaitAsync);
        controller.Begin("Remove audio", static () => ValueTask.CompletedTask, async () =>
        {
            Interlocked.Increment(ref committed);
            started.TrySetResult();
            await release.Task;
        });
        try
        {
            if (expire) delay.Complete();
            else controller.Begin("Next edit", static () => ValueTask.CompletedTask);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task first = controller.DisposeAsync().AsTask();
            Task second = controller.DisposeAsync().AsTask();
            Assert.Same(first, second);
            Assert.False(first.IsCompleted);
            Assert.Throws<ObjectDisposedException>(() => controller.Begin("Retired", static () => ValueTask.CompletedTask));
            release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, committed);
        }
        finally
        {
            release.TrySetResult();
            await controller.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposalJoinsRestorationAndDoesNotCommitUndoneAction()
    {
        var delay = new ControlledDelay();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int committed = 0;
        var controller = new OperatorUndoController(action => action(), _ => { }, delayAsync: delay.WaitAsync);
        controller.Begin("Remove audio", () => new ValueTask(release.Task),
            () => { committed++; return ValueTask.CompletedTask; });
        Task<bool> restoring = controller.UndoAsync().AsTask();
        Task disposing = controller.DisposeAsync().AsTask();
        try
        {
            Assert.False(disposing.IsCompleted);
            release.TrySetResult();
            Assert.True(await restoring.WaitAsync(TimeSpan.FromSeconds(5)));
            await disposing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, committed);
        }
        finally { release.TrySetResult(); await controller.DisposeAsync(); }
    }

    [Fact]
    public async Task QueuedExpiryCannotCommitAgainAfterDisposal()
    {
        var delay = new ControlledDelay();
        var queued = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        int committed = 0;
        await using var controller = new OperatorUndoController(action => queued.TrySetResult(action),
            _ => { }, delayAsync: delay.WaitAsync);
        controller.Begin("Remove audio", static () => ValueTask.CompletedTask,
            () => { committed++; return ValueTask.CompletedTask; });
        delay.Complete();
        Action expire = await queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await controller.DisposeAsync();
        expire();
        Assert.Equal(1, committed);
    }

    [Fact]
    public async Task UndoRunsRestorationAndCancelsDeferredCommit()
    {
        var delay = new ControlledDelay();
        int undone = 0;
        int committed = 0;
        await using var controller = new OperatorUndoController(
            action => action(),
            exception => throw exception,
            TimeSpan.FromSeconds(8),
            delay.WaitAsync);

        controller.Begin(
            "Preset deleted.",
            () => { undone++; return ValueTask.CompletedTask; },
            () => { committed++; return ValueTask.CompletedTask; });

        Assert.True(controller.CanUndo);
        Assert.True(await controller.UndoAsync());
        Assert.False(controller.CanUndo);
        Assert.Equal(1, undone);
        delay.Complete();
        await Task.Yield();
        Assert.Equal(0, committed);
    }

    [Fact]
    public async Task NewActionCommitsPrecedingActionAndExpiryCommitsCurrentAction()
    {
        var delays = new Queue<ControlledDelay>();
        ControlledDelay CreateDelay()
        {
            var delay = new ControlledDelay();
            delays.Enqueue(delay);
            return delay;
        }
        ControlledDelay firstDelay = CreateDelay();
        ControlledDelay secondDelay = CreateDelay();
        int firstCommitted = 0;
        int secondCommitted = 0;
        await using var controller = new OperatorUndoController(
            action => action(),
            exception => throw exception,
            TimeSpan.FromSeconds(8),
            (_, cancellationToken) => delays.Dequeue().WaitAsync(default, cancellationToken));

        controller.Begin(
            "First",
            static () => ValueTask.CompletedTask,
            () => { firstCommitted++; return ValueTask.CompletedTask; });
        controller.Begin(
            "Second",
            static () => ValueTask.CompletedTask,
            () => { secondCommitted++; return ValueTask.CompletedTask; });

        await WaitUntilAsync(() => firstCommitted == 1);
        firstDelay.Complete();
        secondDelay.Complete();
        await WaitUntilAsync(() => secondCommitted == 1);
        Assert.False(controller.CanUndo);
    }

    [Fact]
    public async Task DisposalCommitsPendingActionExactlyOnce()
    {
        var delay = new ControlledDelay();
        int committed = 0;
        var controller = new OperatorUndoController(
            action => action(),
            exception => throw exception,
            TimeSpan.FromSeconds(8),
            delay.WaitAsync);
        controller.Begin(
            "Delete",
            static () => ValueTask.CompletedTask,
            () => { committed++; return ValueTask.CompletedTask; });

        await controller.DisposeAsync();
        await controller.DisposeAsync();

        Assert.Equal(1, committed);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(1);
        Assert.True(condition());
    }

    private sealed class ControlledDelay
    {
        private readonly TaskCompletionSource completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitAsync(TimeSpan _, CancellationToken cancellationToken = default)
            => completion.Task.WaitAsync(cancellationToken);

        public void Complete() => completion.TrySetResult();
    }
}
