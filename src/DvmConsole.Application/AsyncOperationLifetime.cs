namespace DvmConsole.Application;

internal sealed class AsyncOperationLifetime
{
    private readonly object sync = new();
    private readonly TaskCompletionSource idle =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int operations;
    private bool stopping;

    public bool TryAcquire()
    {
        lock (sync)
        {
            if (stopping)
                return false;

            operations++;
            return true;
        }
    }

    public void Release()
    {
        lock (sync)
        {
            operations--;
            if (stopping && operations == 0)
                idle.TrySetResult();
        }
    }

    public void BeginStop()
    {
        lock (sync)
        {
            stopping = true;
            if (operations == 0)
                idle.TrySetResult();
        }
    }

    public Task WaitForIdleAsync() => idle.Task;
}
