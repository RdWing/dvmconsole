using Concentus;
using Concentus.Oggfile;

namespace DvmConsole.Audio;

// Managed Ogg Opus reader used for TAR playback. Decoding at the console's
// native voice rate avoids an unnecessary resampling pass.
public sealed class OpusOggPcmStreamReader : IAudioPcmStreamReader
{
    public const int OutputSampleRate = 8000;

    private readonly Stream source;
    private readonly IOpusOggPacketReader packetReader;
    private readonly object sync = new();
    private readonly object lifetimeSync = new();
    private readonly ExclusiveReaderOperationTracker operations = new();
    private short[] pending = [];
    private int pendingOffset;
    private Task? disposeTask;
    private bool packetReaderDisposed;

    private OpusOggPcmStreamReader(Stream source)
        : this(source, new ConcentusOpusOggPacketReader(source))
    {
    }

    internal OpusOggPcmStreamReader(
        Stream source,
        IOpusOggPacketReader packetReader)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.packetReader = packetReader ?? throw new ArgumentNullException(nameof(packetReader));
    }

    public int SampleRate => OutputSampleRate;

    public static async Task<OpusOggPcmStreamReader> OpenAsync(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException("The Opus source must be readable.", nameof(source));
        cancellationToken.ThrowIfCancellationRequested();

        Task<OpusOggPcmStreamReader> openTask = Task.Run(
            () => new OpusOggPcmStreamReader(source),
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

    public async ValueTask<int> ReadSamplesAsync(
        Memory<short> destination,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IDisposable? operation = operations.Begin(nameof(OpusOggPcmStreamReader));
        if (destination.IsEmpty)
        {
            operation.Dispose();
            return 0;
        }

        Task<int> decodeTask = Task.Run(
            () => Decode(destination, cancellationToken),
            CancellationToken.None);
        using CancellationTokenRegistration cancellation = cancellationToken.Register(
            static state => ((OpusOggPcmStreamReader)state!).source.Dispose(),
            this);
        try
        {
            return await decodeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ObserveBackgroundDecode(decodeTask, operation);
            operation = null;
            throw;
        }
        finally
        {
            if (decodeTask.IsCompleted)
                operation?.Dispose();
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
        DisposePacketReader();
        await source.DisposeAsync().ConfigureAwait(false);
    }

    private static void ObserveCancelledOpen(Task<OpusOggPcmStreamReader> openTask)
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

    private static void ObserveBackgroundDecode(Task<int> decodeTask, IDisposable operation)
    {
        _ = decodeTask.ContinueWith(
            static (completed, state) =>
            {
                _ = completed.Exception;
                ((IDisposable)state!).Dispose();
            },
            operation,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void DisposePacketReader()
    {
        lock (sync)
        {
            if (packetReaderDisposed)
                return;
            packetReaderDisposed = true;
            packetReader.Dispose();
        }
    }

    private int Decode(
        Memory<short> destination,
        CancellationToken cancellationToken)
    {
        try
        {
            lock (sync)
            {
                int written = 0;
                while (written < destination.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (pendingOffset < pending.Length)
                    {
                        int count = Math.Min(
                            destination.Length - written,
                            pending.Length - pendingOffset);
                        pending.AsSpan(pendingOffset, count).CopyTo(destination.Span[written..]);
                        pendingOffset += count;
                        written += count;
                        continue;
                    }

                    pending = [];
                    pendingOffset = 0;
                    if (!packetReader.HasNextPacket)
                        break;

                    cancellationToken.ThrowIfCancellationRequested();
                    pending = packetReader.DecodeNextPacket() ?? [];
                }

                return written;
            }
        }
        catch (Exception exception) when (
            cancellationToken.IsCancellationRequested &&
            exception is ObjectDisposedException or IOException)
        {
            throw new OperationCanceledException(
                "The Ogg Opus read was cancelled.",
                exception,
                cancellationToken);
        }
    }
}

internal interface IOpusOggPacketReader : IDisposable
{
    bool HasNextPacket { get; }

    short[]? DecodeNextPacket();
}

internal sealed class ConcentusOpusOggPacketReader : IOpusOggPacketReader
{
    private readonly IOpusDecoder decoder;
    private readonly OpusOggReadStream reader;
    private bool disposed;

    public ConcentusOpusOggPacketReader(Stream source)
    {
        decoder = OpusCodecFactory.CreateDecoder(OpusOggPcmStreamReader.OutputSampleRate, 1);
        try
        {
            reader = new OpusOggReadStream(decoder, source);
        }
        catch
        {
            decoder.Dispose();
            throw;
        }
    }

    public bool HasNextPacket => reader.HasNextPacket;

    public short[]? DecodeNextPacket() => reader.DecodeNextPacket();

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        try
        {
            reader.Close();
        }
        finally
        {
            decoder.Dispose();
        }
    }
}
