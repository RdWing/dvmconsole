namespace DvmConsole.Audio;

/// <summary>
/// Enforces the single-consumer reader contract and lets disposal wait until
/// an in-flight operation has released buffers and decoder state.
/// </summary>
internal sealed class ExclusiveReaderOperationTracker
{
    private readonly object sync = new();
    private bool operationActive;
    private bool stopping;
    private TaskCompletionSource? idleCompletion;

    public IDisposable Begin(string objectName)
    {
        lock (sync)
        {
            if (stopping)
                throw new ObjectDisposedException(objectName);
            if (operationActive)
                throw new InvalidOperationException("Concurrent audio reader operations are not supported.");

            operationActive = true;
            return new OperationLease(this);
        }
    }

    public Task StopAccepting()
    {
        lock (sync)
        {
            stopping = true;
            if (!operationActive)
                return Task.CompletedTask;

            idleCompletion ??= new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return idleCompletion.Task;
        }
    }

    private void Complete()
    {
        TaskCompletionSource? completion;
        lock (sync)
        {
            if (!operationActive)
                return;

            operationActive = false;
            completion = idleCompletion;
        }

        completion?.TrySetResult();
    }

    private sealed class OperationLease(ExclusiveReaderOperationTracker owner) : IDisposable
    {
        private ExclusiveReaderOperationTracker? owner = owner;

        public void Dispose()
            => Interlocked.Exchange(ref owner, null)?.Complete();
    }
}
