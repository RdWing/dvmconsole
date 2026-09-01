using System.Diagnostics;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class RecordingFinalizationQueueTests
{
    [Fact]
    public async Task EnqueueReturnsBeforeBlockedFinalizationCompletes()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalized = new TaskCompletionSource<RecordingFinalizationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var queue = new RecordingFinalizationQueue();
        queue.Finalized += (_, result) => finalized.TrySetResult(result);

        await queue.EnqueueAsync(new RecordingFinalizationJob(
            StreamId: 51,
            ExecuteAsync: async cancellationToken =>
            {
                await release.Task.WaitAsync(cancellationToken);
                return new RecordingFinalizationResult(null, 51, "done", null);
            }));

        Assert.False(finalized.Task.IsCompleted);
        release.TrySetResult();
        RecordingFinalizationResult result = await finalized.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal((uint)51, result.StreamId);
        Assert.Equal("done", result.Diagnostic);
    }

    [Fact]
    public async Task JobsFinalizeInEnqueueOrder()
    {
        var order = new List<uint>();
        var allFinalized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var queue = new RecordingFinalizationQueue();
        queue.Finalized += (_, result) =>
        {
            order.Add(result.StreamId);
            if (order.Count == 2)
                allFinalized.TrySetResult();
        };

        await queue.EnqueueAsync(Job(1));
        await queue.EnqueueAsync(Job(2));
        await allFinalized.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal([(uint)1, (uint)2], order);

        static RecordingFinalizationJob Job(uint streamId)
            => new(
                streamId,
                _ => Task.FromResult(new RecordingFinalizationResult(null, streamId, null, null)));
    }

    [Fact]
    public async Task TransientIoFailureRetriesWithoutReorderingLaterJobs()
    {
        int attempts = 0;
        var order = new List<uint>();
        var allFinalized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var queue = new RecordingFinalizationQueue();
        queue.Finalized += (_, result) =>
        {
            order.Add(result.StreamId);
            if (order.Count == 2)
                allFinalized.TrySetResult();
        };

        await queue.EnqueueAsync(new RecordingFinalizationJob(
            1,
            _ =>
            {
                attempts++;
                return Task.FromResult(attempts == 1
                    ? new RecordingFinalizationResult(null, 1, "retry", new IOException("temporary"))
                    : new RecordingFinalizationResult(null, 1, null, null));
            },
            RetryDelay: TimeSpan.FromMilliseconds(1)));
        await queue.EnqueueAsync(new RecordingFinalizationJob(
            2,
            _ => Task.FromResult(new RecordingFinalizationResult(null, 2, null, null))));

        await allFinalized.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, attempts);
        Assert.Equal([(uint)1, (uint)2], order);
        Assert.Equal(RecordingFinalizationQueue.DefaultCapacity, queue.Capacity);
    }

    [Fact]
    public async Task DisposalDoesNotWaitForGracePeriodAfterWorkerCompletes()
    {
        var finalized = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new RecordingFinalizationQueue(
            shutdownDrainTimeout: TimeSpan.FromSeconds(5));
        queue.Finalized += (_, _) => finalized.TrySetResult();

        await queue.EnqueueAsync(new RecordingFinalizationJob(
            71,
            _ => Task.FromResult(new RecordingFinalizationResult(null, 71, null, null))));
        await finalized.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stopwatch = Stopwatch.StartNew();
        await queue.DisposeAsync();
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"A completed worker took {stopwatch.Elapsed} to dispose.");
    }

    [Fact]
    public async Task DisposalCancelsBlockedFinalizationWithinConfiguredBounds()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new RecordingFinalizationQueue(
            shutdownDrainTimeout: TimeSpan.FromMilliseconds(50),
            cancellationAcknowledgementTimeout: TimeSpan.FromMilliseconds(50));

        await queue.EnqueueAsync(new RecordingFinalizationJob(
            72,
            async cancellationToken =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                finally
                {
                    cancellationObserved.TrySetResult();
                }
                return new RecordingFinalizationResult(null, 72, null, null);
            }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stopwatch = Stopwatch.StartNew();
        await queue.DisposeAsync();
        stopwatch.Stop();

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"A blocked worker took {stopwatch.Elapsed} to dispose.");
    }

    [Fact]
    public async Task DisposalDoesNotRemainBlockedWhenFinalizerIgnoresCancellation()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finalizerReturned = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int finalized = 0;
        var queue = new RecordingFinalizationQueue(
            shutdownDrainTimeout: TimeSpan.FromMilliseconds(50),
            cancellationAcknowledgementTimeout: TimeSpan.FromMilliseconds(50));
        queue.Finalized += (_, _) => Interlocked.Increment(ref finalized);

        await queue.EnqueueAsync(new RecordingFinalizationJob(
            73,
            async _ =>
            {
                started.TrySetResult();
                await release.Task;
                finalizerReturned.TrySetResult();
                return new RecordingFinalizationResult(null, 73, null, null);
            }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stopwatch = Stopwatch.StartNew();
        await queue.DisposeAsync();
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"A non-cooperative worker took {stopwatch.Elapsed} to dispose.");
        release.TrySetResult();
        await finalizerReturned.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await queue.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(0, Volatile.Read(ref finalized));
    }
}
