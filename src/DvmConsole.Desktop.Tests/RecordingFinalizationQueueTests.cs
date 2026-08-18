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
}
