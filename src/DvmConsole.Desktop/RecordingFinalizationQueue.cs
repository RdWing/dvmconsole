using System.Threading.Channels;

namespace DvmConsole.Desktop;

internal sealed class RecordingFinalizationQueue : IAsyncDisposable
{
    private readonly Channel<RecordingFinalizationJob> jobs = Channel.CreateUnbounded<RecordingFinalizationJob>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task worker;
    private int disposeStarted;

    public RecordingFinalizationQueue()
    {
        worker = Task.Run(ProcessAsync);
    }

    public event EventHandler<RecordingFinalizationResult>? Finalized;

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
            await worker.ConfigureAwait(false);
        }
        finally
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    private async Task ProcessAsync()
    {
        await foreach (RecordingFinalizationJob job in jobs.Reader.ReadAllAsync(cancellation.Token).ConfigureAwait(false))
        {
            RecordingFinalizationResult result;
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

            try
            {
                Finalized?.Invoke(this, result);
            }
            catch
            {
                // A completion observer must not prevent later recordings from
                // being finalized by the single-reader queue.
            }
        }
    }
}
