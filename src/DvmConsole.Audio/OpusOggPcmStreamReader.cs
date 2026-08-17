using Concentus;
using Concentus.Oggfile;

namespace DvmConsole.Audio;

// Managed Ogg Opus reader used for TAR playback. Decoding at the console's
// native voice rate avoids an unnecessary resampling pass.
public sealed class OpusOggPcmStreamReader : IAudioPcmStreamReader
{
    public const int OutputSampleRate = 8000;

    private readonly Stream source;
    private readonly OpusOggReadStream reader;
    private short[] pending = [];
    private int pendingOffset;
    private bool disposed;

    private OpusOggPcmStreamReader(Stream source)
    {
        this.source = source;
        reader = new OpusOggReadStream(
            OpusCodecFactory.CreateDecoder(OutputSampleRate, 1),
            source);
    }

    public int SampleRate => OutputSampleRate;

    public static Task<OpusOggPcmStreamReader> OpenAsync(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException("The Opus source must be readable.", nameof(source));
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return Task.FromResult(new OpusOggPcmStreamReader(source));
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    public ValueTask<int> ReadSamplesAsync(
        Memory<short> destination,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (destination.IsEmpty)
            return ValueTask.FromResult(0);

        int written = 0;
        while (written < destination.Length)
        {
            if (pendingOffset < pending.Length)
            {
                int count = Math.Min(destination.Length - written, pending.Length - pendingOffset);
                pending.AsSpan(pendingOffset, count).CopyTo(destination.Span[written..]);
                pendingOffset += count;
                written += count;
                continue;
            }

            pending = [];
            pendingOffset = 0;
            if (!reader.HasNextPacket)
                break;

            pending = reader.DecodeNextPacket() ?? [];
        }

        return ValueTask.FromResult(written);
    }

    public ValueTask DisposeAsync()
    {
        if (disposed)
            return ValueTask.CompletedTask;
        disposed = true;
        reader.Close();
        source.Dispose();
        return ValueTask.CompletedTask;
    }
}
