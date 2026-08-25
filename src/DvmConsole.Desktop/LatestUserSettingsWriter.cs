using DvmConsole.Core.Settings;

namespace DvmConsole.Desktop;

internal class LatestSnapshotWriter<TSnapshot> : IAsyncDisposable
    where TSnapshot : class
{
    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(50);
    private const int DefaultMaximumWriteAttempts = 3;
    private readonly object sync = new();
    private readonly Action<TSnapshot> writeSnapshot;
    private readonly Action<Exception>? faultHandler;
    private readonly TimeSpan debounce;
    private readonly TimeSpan retryDelay;
    private readonly int maximumWriteAttempts;
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly CancellationTokenSource shutdown = new();
    private readonly List<FlushWaiter> flushWaiters = [];
    private readonly Task worker;
    private CancellationTokenSource debounceInterrupt = new();
    private TSnapshot? latestSnapshot;
    private long requestedRevision;
    private long attemptedRevision;
    private long completedRevision;
    private long persistedRevision;
    private long forceThroughRevision;
    private Exception? completedFailure;
    private bool disposeStarted;

    public LatestSnapshotWriter(
        Action<TSnapshot> writeSnapshot,
        Action<Exception>? faultHandler = null,
        TimeSpan? debounce = null,
        int maximumWriteAttempts = DefaultMaximumWriteAttempts,
        TimeSpan? retryDelay = null)
    {
        ArgumentNullException.ThrowIfNull(writeSnapshot);
        this.writeSnapshot = writeSnapshot;
        this.faultHandler = faultHandler;
        this.debounce = debounce ?? DefaultDebounce;
        if (this.debounce < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(debounce));
        if (maximumWriteAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumWriteAttempts));
        this.maximumWriteAttempts = maximumWriteAttempts;
        this.retryDelay = retryDelay ?? DefaultRetryDelay;
        if (this.retryDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryDelay));
        worker = Task.Run(RunAsync);
    }

    public void Schedule(TSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposeStarted, this);
            latestSnapshot = snapshot;
            requestedRevision++;
            InterruptDebounceCore();
        }
        Signal();
    }

    public Task FlushAsync()
        => RequestFlush();

    public async ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposeStarted)
                return;
            disposeStarted = true;
        }

        Exception? flushFailure = null;
        try
        {
            await RequestFlush().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            flushFailure = exception;
        }
        finally
        {
            shutdown.Cancel();
            Signal();
            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
            }
            shutdown.Dispose();
            debounceInterrupt.Dispose();
            wake.Dispose();
        }

        if (flushFailure is not null)
            throw flushFailure;
    }

    private Task RequestFlush()
    {
        lock (sync)
        {
            if (requestedRevision <= completedRevision)
                return CreateCompletedFlushTask(requestedRevision);

            long targetRevision = requestedRevision;
            forceThroughRevision = Math.Max(forceThroughRevision, targetRevision);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            flushWaiters.Add(new FlushWaiter(targetRevision, completion));
            InterruptDebounceCore();
            Signal();
            return completion.Task;
        }
    }

    private async Task RunAsync()
    {
        CancellationToken cancellationToken = shutdown.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            await wake.WaitAsync(cancellationToken).ConfigureAwait(false);

            while (TryGetPending(out TSnapshot? snapshot, out long revision, out bool force))
            {
                if (!force && debounce > TimeSpan.Zero)
                {
                    CancellationToken interruptToken;
                    lock (sync)
                        interruptToken = debounceInterrupt.Token;
                    using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken,
                        interruptToken);
                    try
                    {
                        await Task.Delay(debounce, delayCancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (
                        !cancellationToken.IsCancellationRequested && interruptToken.IsCancellationRequested)
                    {
                        continue;
                    }
                    lock (sync)
                    {
                        if (requestedRevision != revision || forceThroughRevision > attemptedRevision)
                            continue;
                    }
                }

                WriteResult result = await WriteWithRetryAsync(
                    snapshot,
                    revision,
                    cancellationToken).ConfigureAwait(false);
                if (result.Superseded)
                    continue;

                if (result.Failure is not null)
                    ReportFailure(result.Failure);
                CompleteAttempt(revision, result.Failure);
            }
        }
    }

    private async Task<WriteResult> WriteWithRetryAsync(
        TSnapshot snapshot,
        long revision,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= maximumWriteAttempts; attempt++)
        {
            Exception? failure = null;
            try
            {
                writeSnapshot(snapshot);
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            lock (sync)
                attemptedRevision = Math.Max(attemptedRevision, revision);
            if (failure is null)
                return WriteResult.Succeeded;
            if (attempt == maximumWriteAttempts || !IsRetryable(failure))
                return new WriteResult(failure, Superseded: false);

            CancellationToken interruptToken;
            lock (sync)
            {
                if (requestedRevision != revision)
                    return WriteResult.SupersededResult;
                interruptToken = debounceInterrupt.Token;
            }

            if (retryDelay == TimeSpan.Zero)
                continue;

            using var retryCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                interruptToken);
            try
            {
                await Task.Delay(retryDelay, retryCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested && interruptToken.IsCancellationRequested)
            {
                lock (sync)
                {
                    if (requestedRevision != revision)
                        return WriteResult.SupersededResult;
                }
                // A flush forced this same revision. Retry immediately.
            }
        }

        throw new InvalidOperationException("The bounded settings write loop did not complete.");
    }

    private bool TryGetPending(
        out TSnapshot snapshot,
        out long revision,
        out bool force)
    {
        lock (sync)
        {
            if (requestedRevision <= completedRevision || latestSnapshot is null)
            {
                snapshot = null!;
                revision = 0;
                force = false;
                return false;
            }

            snapshot = latestSnapshot;
            revision = requestedRevision;
            force = forceThroughRevision > completedRevision;
            return true;
        }
    }

    private void CompleteAttempt(long revision, Exception? failure)
    {
        lock (sync)
        {
            completedRevision = Math.Max(completedRevision, revision);
            if (failure is null)
            {
                persistedRevision = Math.Max(persistedRevision, revision);
                if (revision >= completedRevision)
                    completedFailure = null;
            }
            else if (revision >= completedRevision)
            {
                completedFailure = failure;
            }

            for (int index = flushWaiters.Count - 1; index >= 0; index--)
            {
                FlushWaiter waiter = flushWaiters[index];
                if (waiter.TargetRevision > completedRevision)
                    continue;
                if (waiter.TargetRevision <= persistedRevision)
                    waiter.Completion.TrySetResult();
                else
                    waiter.Completion.TrySetException(
                        completedFailure ?? new IOException("The settings snapshot was not persisted."));
                flushWaiters.RemoveAt(index);
            }
        }
    }

    private Task CreateCompletedFlushTask(long targetRevision)
    {
        if (targetRevision <= persistedRevision)
            return Task.CompletedTask;
        return Task.FromException(
            completedFailure ?? new IOException("The settings snapshot was not persisted."));
    }

    private void ReportFailure(Exception failure)
    {
        try
        {
            faultHandler?.Invoke(failure);
        }
        catch
        {
            // Diagnostics must not terminate the persistence worker or strand
            // flush waiters after their result has already been published.
        }
    }

    private static bool IsRetryable(Exception exception)
        => exception is IOException or UnauthorizedAccessException;

    private void Signal()
    {
        try
        {
            wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // One pending wake represents every newer snapshot.
        }
        catch (ObjectDisposedException)
        {
            // Disposal already completed.
        }
    }

    private void InterruptDebounceCore()
    {
        debounceInterrupt.Cancel();
        debounceInterrupt.Dispose();
        debounceInterrupt = new CancellationTokenSource();
    }

    private sealed record FlushWaiter(long TargetRevision, TaskCompletionSource Completion);

    private readonly record struct WriteResult(Exception? Failure, bool Superseded)
    {
        public static WriteResult Succeeded { get; } = new(null, Superseded: false);
        public static WriteResult SupersededResult { get; } = new(null, Superseded: true);
    }
}

internal sealed class LatestUserSettingsWriter : LatestSnapshotWriter<UserSettingsSnapshot>
{
    public LatestUserSettingsWriter(
        Action<UserSettingsSnapshot> writeSnapshot,
        Action<Exception>? faultHandler = null,
        TimeSpan? debounce = null,
        int maximumWriteAttempts = 3,
        TimeSpan? retryDelay = null)
        : base(writeSnapshot, faultHandler, debounce, maximumWriteAttempts, retryDelay)
    {
    }
}

internal sealed class LatestOperatorViewWriter : LatestSnapshotWriter<OperatorViewSettings>
{
    public LatestOperatorViewWriter(
        Action<OperatorViewSettings> writeSnapshot,
        Action<Exception>? faultHandler = null,
        TimeSpan? debounce = null,
        int maximumWriteAttempts = 3,
        TimeSpan? retryDelay = null)
        : base(writeSnapshot, faultHandler, debounce, maximumWriteAttempts, retryDelay)
    {
    }
}
