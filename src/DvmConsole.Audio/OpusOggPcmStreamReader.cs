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
    private short[] pending = [];
    private int pendingOffset;
    private Task<int>? activeDecodeTask;
    private bool packetReaderDisposed;
    private int disposed;

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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (destination.IsEmpty)
            return 0;

        Task<int> decodeTask;
        lock (lifetimeSync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (activeDecodeTask is { IsCompleted: false })
                throw new InvalidOperationException("Concurrent Ogg Opus reads are not supported.");
            decodeTask = Task.Run(
                () => Decode(destination, cancellationToken),
                CancellationToken.None);
            activeDecodeTask = decodeTask;
        }
        using CancellationTokenRegistration cancellation = cancellationToken.Register(
            static state => ((OpusOggPcmStreamReader)state!).source.Dispose(),
            this);
        try
        {
            return await decodeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ObserveBackgroundDecode(decodeTask);
            throw;
        }
        finally
        {
            if (decodeTask.IsCompleted)
                ClearActiveDecode(decodeTask);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return ValueTask.CompletedTask;

        source.Dispose();
        Task<int>? decodeTask;
        lock (lifetimeSync)
            decodeTask = activeDecodeTask;
        if (decodeTask is { IsCompleted: false })
            ObserveBackgroundDecode(decodeTask);
        else
            DisposePacketReader();
        return ValueTask.CompletedTask;
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

    private void ObserveBackgroundDecode(Task<int> decodeTask)
    {
        _ = decodeTask.ContinueWith(
            static (completed, state) =>
            {
                var owner = (OpusOggPcmStreamReader)state!;
                _ = completed.Exception;
                owner.ClearActiveDecode(completed);
                if (Volatile.Read(ref owner.disposed) != 0)
                    owner.DisposePacketReader();
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ClearActiveDecode(Task decodeTask)
    {
        lock (lifetimeSync)
        {
            if (ReferenceEquals(activeDecodeTask, decodeTask))
                activeDecodeTask = null;
        }
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
                ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
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
