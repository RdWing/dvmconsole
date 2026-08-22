namespace DvmConsole.Desktop;

// Publishes one asynchronous cleanup operation to every caller. Disposal is a
// lifecycle boundary, so a later caller must observe completion and failure of
// the original cleanup rather than returning while resources are still live.
internal sealed class AsyncDisposal
{
    private readonly object sync = new();
    private TaskCompletionSource? completion;

    public ValueTask RunAsync(Func<Task> dispose)
    {
        ArgumentNullException.ThrowIfNull(dispose);
        TaskCompletionSource current;
        bool start = false;
        lock (sync)
        {
            if (completion is null)
            {
                completion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                start = true;
            }
            current = completion;
        }

        if (start)
            _ = ExecuteAsync(dispose, current);
        return new ValueTask(current.Task);
    }

    private static async Task ExecuteAsync(
        Func<Task> dispose,
        TaskCompletionSource completion)
    {
        try
        {
            await dispose().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }
}
