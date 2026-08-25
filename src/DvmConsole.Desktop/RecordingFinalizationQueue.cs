using System.Threading.Channels;

namespace DvmConsole.Desktop;

internal sealed class RecordingFinalizationQueue : IAsyncDisposable
{
    public const int DefaultCapacity = 256;
    private readonly Channel<RecordingFinalizationJob> jobs;
    private readonly int capacity;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task worker;
    private readonly TimeSpan shutdownDrainTimeout;
    private int disposeStarted;

    public RecordingFinalizationQueue(
        int capacity = DefaultCapacity,
        TimeSpan? shutdownDrainTimeout = null)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
        this.shutdownDrainTimeout = shutdownDrainTimeout ?? TimeSpan.FromSeconds(8);
        if (this.shutdownDrainTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(shutdownDrainTimeout));
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

    public ValueTask EnqueueAsync(
        RecordingFinalizationJob job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeStarted) != 0, this);
        return jobs.Writer.WriteAsync(job, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
            return;

        jobs.Writer.TryComplete();
        try
        {
            Task completed = await Task.WhenAny(
                worker,
                Task.Delay(shutdownDrainTimeout)).ConfigureAwait(false);
            if (!ReferenceEquals(completed, worker))
                cancellation.Cancel();
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
            cancellation.Cancel();
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
