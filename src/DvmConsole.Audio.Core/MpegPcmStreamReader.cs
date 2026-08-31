using NLayer;

namespace DvmConsole.Audio;

// Adapts NLayer's synchronous floating-point MPEG decoder to the shared
// cancellable mono 16-bit PCM reader contract.
public sealed class MpegPcmStreamReader : IAudioPcmStreamReader
{
    private readonly Stream source;
    private readonly MpegFile mpegFile;
    private readonly object sync = new();
    private readonly object lifetimeSync = new();
    private readonly ExclusiveReaderOperationTracker operations = new();
    private float[] decoded = [];
    private Task? disposeTask;

    private MpegPcmStreamReader(Stream source)
    {
        this.source = source;
        mpegFile = new MpegFile(source)
        {
            StereoMode = StereoMode.DownmixToMono
        };
        SampleRate = mpegFile.SampleRate;
    }

    public int SampleRate { get; }

    public static async Task<MpegPcmStreamReader> OpenAsync(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        Task<MpegPcmStreamReader> openTask = Task.Run(
            () => new MpegPcmStreamReader(source),
            CancellationToken.None);
        using CancellationTokenRegistration cancellation = cancellationToken.Register(
            static state => ((Stream)state!).Dispose(),
            source);
        try
        {
            return await openTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ObserveCancelledOpen(openTask);
            throw;
        }
        catch
        {
            await source.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void ObserveCancelledOpen(Task<MpegPcmStreamReader> openTask)
    {
        _ = openTask.ContinueWith(
            static completed =>
            {
                if (completed.Status == TaskStatus.RanToCompletion)
                    completed.Result.DisposeAsync().AsTask().GetAwaiter().GetResult();
                else
                    _ = completed.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public async ValueTask<int> ReadSamplesAsync(
        Memory<short> destination,
        CancellationToken cancellationToken = default)
    {
        using IDisposable operation = operations.Begin(nameof(MpegPcmStreamReader));
        if (destination.IsEmpty)
            return 0;
        cancellationToken.ThrowIfCancellationRequested();

        EnsureBuffer(destination.Length);
        Task<int> decodeTask = Task.Run(
            () => Decode(destination, cancellationToken),
            CancellationToken.None);
        using CancellationTokenRegistration cancellation = cancellationToken.Register(
            static state => ((MpegPcmStreamReader)state!).source.Dispose(),
            this);
        try
        {
            return await decodeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                await decodeTask.ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                // Disposing the source is how cancellation interrupts NLayer's
                // synchronous read against a live HTTP response stream.
            }

            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (lifetimeSync)
            return new ValueTask(disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Task idle = operations.StopAccepting();
        source.Dispose();
        await idle.ConfigureAwait(false);
        lock (sync)
            mpegFile.Dispose();
        await source.DisposeAsync().ConfigureAwait(false);
    }

    private int Decode(Memory<short> destination, CancellationToken cancellationToken)
    {
        try
        {
            int count;
            lock (sync)
            {
                count = mpegFile.ReadSamples(decoded, 0, destination.Length);
            }

            for (int index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int sample = (int)Math.Round(decoded[index] * short.MaxValue, MidpointRounding.AwayFromZero);
                destination.Span[index] = (short)Math.Clamp(sample, short.MinValue, short.MaxValue);
            }

            return count;
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private void EnsureBuffer(int sampleCount)
    {
        lock (sync)
        {
            if (decoded.Length < sampleCount)
                decoded = new float[sampleCount];
        }
    }
}
