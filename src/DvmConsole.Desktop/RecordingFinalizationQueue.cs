using System.Threading.Channels;

namespace DvmConsole.Desktop;

internal sealed class RecordingFinalizationQueue : IAsyncDisposable
{
    public const int DefaultCapacity = 256;
    internal static readonly TimeSpan DefaultShutdownDrainTimeout = TimeSpan.FromMilliseconds(750);
    internal static readonly TimeSpan DefaultCancellationAcknowledgementTimeout = TimeSpan.FromMilliseconds(250);
    private readonly Channel<RecordingFinalizationJob> jobs;
    private readonly int capacity;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task worker;
    private readonly TimeSpan shutdownDrainTimeout;
    private readonly TimeSpan cancellationAcknowledgementTimeout;
    private int disposeStarted;

    public RecordingFinalizationQueue(
        int capacity = DefaultCapacity,
        TimeSpan? shutdownDrainTimeout = null,
        TimeSpan? cancellationAcknowledgementTimeout = null)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
        this.shutdownDrainTimeout = shutdownDrainTimeout ?? DefaultShutdownDrainTimeout;
        if (this.shutdownDrainTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(shutdownDrainTimeout));
        this.cancellationAcknowledgementTimeout = cancellationAcknowledgementTimeout
            ?? DefaultCancellationAcknowledgementTimeout;
        if (this.cancellationAcknowledgementTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cancellationAcknowledgementTimeout));
        jobs = Channel.CreateBounded<RecordingFinalizationJob>(
            new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        worker = Task.Run(ProcessAsync);
    }

    public event EventHandler<RecordingFinalizationResult>? Finalized;
    public int Capacity => capacity;
    internal Task Completion => worker;

    public ValueTask EnqueueAsync(
        RecordingFinalizationJob job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeStarted) != 0, this);
        return jobs.Writer.WriteAsync(job, cancellationToken);
    }

    public bool TryEnqueue(RecordingFinalizationJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeStarted) != 0, this);
        return jobs.Writer.TryWrite(job);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
            return;

        jobs.Writer.TryComplete();
        bool disposeCancellationHere = true;
        try
        {
            if (!await WaitForWorkerAsync(shutdownDrainTimeout).ConfigureAwait(false))
            {
                cancellation.Cancel();
                if (!await WaitForWorkerAsync(cancellationAcknowledgementTimeout).ConfigureAwait(false))
                {
                    disposeCancellationHere = false;
                    TaskObservation.Observe(AwaitWorkerAndDisposeCancellationAsync());
                    return;
                }
            }

            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Durable descriptors remain available for the next startup.
            }
        }
        finally
        {
            if (disposeCancellationHere)
            {
                cancellation.Cancel();
                cancellation.Dispose();
            }
        }
    }

    private async Task<bool> WaitForWorkerAsync(TimeSpan timeout)
    {
        if (worker.IsCompleted)
            return true;

        using var timeoutCancellation = new CancellationTokenSource();
        Task timeoutTask = Task.Delay(timeout, timeoutCancellation.Token);
        Task completed = await Task.WhenAny(worker, timeoutTask).ConfigureAwait(false);
        if (ReferenceEquals(completed, worker))
            timeoutCancellation.Cancel();
        return ReferenceEquals(completed, worker);
    }

    private async Task AwaitWorkerAndDisposeCancellationAsync()
    {
        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Durable descriptors remain available for the next startup.
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (RecordingFinalizationJob job in jobs.Reader.ReadAllAsync(cancellation.Token).ConfigureAwait(false))
            {
                RecordingFinalizationResult? result = null;
                for (int attempt = 1; attempt <= Math.Max(1, job.MaximumAttempts); attempt++)
                {
                    try
                    {
                        result = await job.ExecuteAsync(cancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        result = new RecordingFinalizationResult(null, job.StreamId, exception.Message, exception);
                    }

                    if (cancellation.IsCancellationRequested)
                        return;

                    bool retry = result.Error is (IOException or UnauthorizedAccessException) &&
                        attempt < job.MaximumAttempts;
                    if (!retry)
                        break;
                    await Task.Delay(job.EffectiveRetryDelay, cancellation.Token).ConfigureAwait(false);
                }

                try
                {
                    Finalized?.Invoke(this, result!);
                }
                catch
                {
                    // A completion observer must not prevent later recordings from
                    // being finalized by the single-reader queue.
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A bounded shutdown leaves durable work for the next process.
        }
    }
}
