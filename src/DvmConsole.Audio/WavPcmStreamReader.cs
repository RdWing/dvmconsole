using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace DvmConsole.Audio;

// Reads uncompressed PCM WAV data from a seekable or non-seekable stream and
// exposes mono 16-bit samples at the file's native sample rate. This is the
// portable web-stream decoder boundary; compressed formats are rejected
// instead of being routed through a platform-specific media framework.
public sealed class WavPcmStreamReader : IAudioPcmStreamReader
{
    private readonly Stream source;
    private readonly ArrayPool<byte> bufferPool;
    private readonly int blockAlign;
    private readonly int bytesPerSample;
    private byte[]? readBuffer;
    private long dataBytesRemaining;
    private bool disposed;

    private WavPcmStreamReader(
        Stream source,
        int sampleRate,
        int channels,
        int bitsPerSample,
        int blockAlign,
        long dataBytesRemaining,
        ArrayPool<byte> bufferPool)
    {
        this.source = source;
        this.bufferPool = bufferPool;
        SampleRate = sampleRate;
        Channels = channels;
        BitsPerSample = bitsPerSample;
        this.blockAlign = blockAlign;
        bytesPerSample = bitsPerSample / 8;
        this.dataBytesRemaining = dataBytesRemaining;
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public int BitsPerSample { get; }
    public bool EndOfStream => dataBytesRemaining < blockAlign;

    public static Task<WavPcmStreamReader> OpenAsync(
        Stream source,
        CancellationToken cancellationToken = default)
        => OpenAsync(source, ArrayPool<byte>.Shared, cancellationToken);

    internal static async Task<WavPcmStreamReader> OpenAsync(
        Stream source,
        ArrayPool<byte> bufferPool,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(bufferPool);
        if (!source.CanRead)
            throw new ArgumentException("The WAV source must be readable.", nameof(source));

        try
        {
            byte[] riffHeader = new byte[12];
            await ReadExactlyAsync(source, riffHeader, cancellationToken).ConfigureAwait(false);
            if (!riffHeader.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
                !riffHeader.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            {
                throw new InvalidDataException("The stream is not a RIFF/WAVE file.");
            }

            int sampleRate = 0;
            int channels = 0;
            int bitsPerSample = 0;
            int blockAlign = 0;
            bool hasFormat = false;

            while (true)
            {
                byte[] chunkHeader = new byte[8];
                if (!await TryReadChunkHeaderAsync(source, chunkHeader, cancellationToken).ConfigureAwait(false))
                    throw new InvalidDataException("The WAV stream has no data chunk.");

                string chunkId = Encoding.ASCII.GetString(chunkHeader, 0, 4);
                long chunkBytes = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.AsSpan(4, 4));
                if (chunkId == "fmt ")
                {
                    if (chunkBytes < 16)
                        throw new InvalidDataException("The WAV format chunk is truncated.");

                    byte[] format = new byte[16];
                    await ReadExactlyAsync(source, format, cancellationToken).ConfigureAwait(false);
                    await DiscardAsync(source, chunkBytes - 16, cancellationToken).ConfigureAwait(false);

                    ushort formatTag = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(0, 2));
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(2, 2));
                    sampleRate = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4, 4)));
                    blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(12, 2));
                    bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(14, 2));
                    if (formatTag != 1)
                        throw new NotSupportedException("Only uncompressed PCM WAV streams are supported.");
                    if (channels is < 1 or > 2 || sampleRate <= 0 || bitsPerSample is not (8 or 16))
                        throw new NotSupportedException("WAV streams must be 8/16-bit mono or stereo PCM.");
                    if (blockAlign != checked(channels * (bitsPerSample / 8)))
                        throw new InvalidDataException("The WAV block alignment is invalid.");
                    hasFormat = true;
                }
                else if (chunkId == "data")
                {
                    if (!hasFormat)
                        throw new InvalidDataException("The WAV data chunk appears before its format chunk.");
                    return new WavPcmStreamReader(
                        source,
                        sampleRate,
                        channels,
                        bitsPerSample,
                        blockAlign,
                        chunkBytes,
                        bufferPool);
                }
                else
                {
                    await DiscardAsync(source, chunkBytes, cancellationToken).ConfigureAwait(false);
                }

                if ((chunkBytes & 1) != 0)
                    await DiscardAsync(source, 1, cancellationToken).ConfigureAwait(false);
            }
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
        ObjectDisposedException.ThrowIf(disposed, this);
        if (destination.IsEmpty || dataBytesRemaining < blockAlign)
            return 0;

        int frameCount = (int)Math.Min(destination.Length, dataBytesRemaining / blockAlign);
        int byteCount = checked(frameCount * blockAlign);
        byte[] buffer = GetReadBuffer(byteCount);
        int bytesRead = await ReadAtMostAsync(
            source,
            buffer.AsMemory(0, byteCount),
            cancellationToken).ConfigureAwait(false);
        int completeBytes = bytesRead - (bytesRead % blockAlign);
        int completeFrames = completeBytes / blockAlign;
        dataBytesRemaining = Math.Max(0, dataBytesRemaining - completeBytes);
        if (bytesRead < byteCount || completeBytes != bytesRead)
            dataBytesRemaining = 0;

        for (int frame = 0; frame < completeFrames; frame++)
        {
            int offset = frame * blockAlign;
            int sample = ReadSample(buffer, offset);
            if (Channels == 2)
                sample = (sample + ReadSample(buffer, offset + bytesPerSample)) / 2;
            destination.Span[frame] = (short)Math.Clamp(sample, short.MinValue, short.MaxValue);
        }

        return completeFrames;
    }

    public async ValueTask<long> SkipSamplesAsync(
        long sampleCount,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (sampleCount < 0)
            throw new ArgumentOutOfRangeException(nameof(sampleCount));

        long frames = Math.Min(sampleCount, dataBytesRemaining / blockAlign);
        long bytes = checked(frames * blockAlign);
        if (bytes == 0)
            return 0;

        if (source.CanSeek)
            source.Seek(bytes, SeekOrigin.Current);
        else
            await DiscardDataAsync(bytes, cancellationToken).ConfigureAwait(false);
        dataBytesRemaining -= bytes;
        return frames;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        byte[]? buffer = readBuffer;
        readBuffer = null;
        try
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (buffer is not null)
                bufferPool.Return(buffer);
        }
    }

    private int ReadSample(byte[] buffer, int offset)
        => BitsPerSample == 16
            ? BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(offset, 2))
            : (buffer[offset] - 128) << 8;

    private byte[] GetReadBuffer(int minimumLength)
    {
        if (readBuffer is not null && readBuffer.Length >= minimumLength)
            return readBuffer;

        byte[] replacement = bufferPool.Rent(minimumLength);
        if (readBuffer is not null)
            bufferPool.Return(readBuffer);
        readBuffer = replacement;
        return replacement;
    }

    private async Task DiscardDataAsync(long bytes, CancellationToken cancellationToken)
    {
        byte[] buffer = GetReadBuffer((int)Math.Min(bytes, 4096));
        while (bytes > 0)
        {
            int requested = (int)Math.Min(bytes, buffer.Length);
            int read = await source.ReadAsync(
                buffer.AsMemory(0, requested),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("The WAV stream ended unexpectedly.");
            bytes -= read;
        }
    }

    private static async Task<bool> TryReadChunkHeaderAsync(
        Stream source,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int first = await source.ReadAsync(buffer[..1], cancellationToken).ConfigureAwait(false);
        if (first == 0)
            return false;
        await ReadExactlyAsync(source, buffer[1..], cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task<int> ReadAtMostAsync(
        Stream source,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await source.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
        }

        return total;
    }

    private static async Task ReadExactlyAsync(
        Stream source,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await source.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("The WAV stream ended unexpectedly.");
            total += read;
        }
    }

    private static async Task DiscardAsync(
        Stream source,
        long bytes,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];
        while (bytes > 0)
        {
            int requested = (int)Math.Min(bytes, buffer.Length);
            int read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("The WAV stream ended unexpectedly.");
            bytes -= read;
        }
    }
}
