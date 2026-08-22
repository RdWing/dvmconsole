namespace DvmConsole.Desktop;

internal sealed record LatestBooleanStateResult(bool Desired, Exception? Error);

internal sealed class LatestBooleanStateReconciler
{
    private readonly object sync = new();
    private readonly Func<bool, Task> applyAsync;
    private bool desired;
    private long generation;
    private bool running;
    private TaskCompletionSource<LatestBooleanStateResult> idle = Completed(false);

    public LatestBooleanStateReconciler(Func<bool, Task> applyAsync)
    {
        this.applyAsync = applyAsync ?? throw new ArgumentNullException(nameof(applyAsync));
    }

    public event EventHandler<LatestBooleanStateResult>? Reconciled;

    public Task SetDesired(bool value)
    {
        lock (sync)
        {
            desired = value;
            generation++;
            if (!running)
            {
                running = true;
                idle = new TaskCompletionSource<LatestBooleanStateResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                TaskObservation.Observe(Task.Run(RunAsync));
            }
            return idle.Task;
        }
    }

    public Task<LatestBooleanStateResult> WhenIdleAsync()
    {
        lock (sync)
            return idle.Task;
    }

    private async Task RunAsync()
    {
        while (true)
        {
            bool next;
            long applyingGeneration;
            lock (sync)
            {
                next = desired;
                applyingGeneration = generation;
            }

            Exception? error = null;
            try
            {
                await applyAsync(next).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                error = exception;
            }

            LatestBooleanStateResult result;
            lock (sync)
            {
                if (applyingGeneration != generation)
                    continue;

                running = false;
                result = new LatestBooleanStateResult(next, error);
                idle.TrySetResult(result);
            }

            try
            {
                Reconciled?.Invoke(this, result);
            }
            catch
            {
                // Status presentation must not change the applied state.
            }
            return;
        }
    }

    private static TaskCompletionSource<LatestBooleanStateResult> Completed(bool desired)
    {
        var completion = new TaskCompletionSource<LatestBooleanStateResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult(new LatestBooleanStateResult(desired, null));
        return completion;
    }
}
