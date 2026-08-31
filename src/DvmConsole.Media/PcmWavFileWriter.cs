using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DvmConsole.Audio;

namespace DvmConsole.Media;

// Streams signed little-endian PCM samples to a RIFF/WAVE file and patches
// the RIFF/data lengths when the recording ends. It contains no TAR policy;
// callers own naming, retention, and call lifecycle decisions.
public sealed class PcmWavFileWriter : IDisposable, IAsyncDisposable
{
    private const int HeaderLength = 44;
    private readonly Stream stream;
    private readonly PcmAudioFormat format;
    private readonly bool leaveOpen;
    private long dataBytes;
    private bool disposed;

    public PcmWavFileWriter(
        Stream stream,
        PcmAudioFormat format,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(format);
        if (!stream.CanWrite || !stream.CanSeek)
        {
            throw new ArgumentException(
                "WAV recording requires a writable, seekable stream.",
                nameof(stream));
        }
        if (format.BitsPerSample != 16)
            throw new ArgumentException("WAV recording requires 16-bit PCM.", nameof(format));

        this.stream = stream;
        this.format = format;
        this.leaveOpen = leaveOpen;
        stream.SetLength(0);
        WriteHeader(0);
    }

    public PcmAudioFormat Format => format;
    public long SamplesWritten => dataBytes / (format.BitsPerSample / 8);

    public void Write(ReadOnlySpan<short> samples)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (samples.IsEmpty)
            return;

        long bytesToWrite = checked((long)samples.Length * sizeof(short));
        if (dataBytes > uint.MaxValue - bytesToWrite)
            throw new InvalidOperationException("The WAV recording exceeds the RIFF file-size limit.");

        if (BitConverter.IsLittleEndian)
        {
            stream.Write(MemoryMarshal.AsBytes(samples));
        }
        else
        {
            const int ChunkSamples = 2_048;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkSamples * sizeof(short));
            try
            {
                for (int offset = 0; offset < samples.Length; offset += ChunkSamples)
                {
                    ReadOnlySpan<short> chunk = samples.Slice(
                        offset,
                        Math.Min(ChunkSamples, samples.Length - offset));
                    Span<byte> bytes = buffer.AsSpan(0, chunk.Length * sizeof(short));
                    for (int index = 0; index < chunk.Length; index++)
                    {
                        BinaryPrimitives.WriteInt16LittleEndian(
                            bytes.Slice(index * sizeof(short), sizeof(short)),
                            chunk[index]);
                    }
                    stream.Write(bytes);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        dataBytes += bytesToWrite;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        try
        {
            WriteHeader((uint)dataBytes);
            stream.Flush();
        }
        finally
        {
            if (!leaveOpen)
                stream.Dispose();
            disposed = true;
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Repairs the length fields of an interrupted writer from the bytes that
    /// actually reached the durable stream. The fixed PCM format and RIFF signatures are
    /// validated before any data is changed.
    /// </summary>
    public static long RepairInterruptedStream(Stream stream, PcmAudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(format);
        if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek)
        {
            throw new ArgumentException(
                "Interrupted WAV repair requires a readable, writable, seekable stream.",
                nameof(stream));
        }
        if (format.BitsPerSample != 16)
            throw new ArgumentException("WAV recording requires 16-bit PCM.", nameof(format));
        if (stream.Length < HeaderLength)
            throw new InvalidDataException("Interrupted WAV is shorter than its header.");

        Span<byte> existingHeader = stackalloc byte[HeaderLength];
        stream.Position = 0;
        stream.ReadExactly(existingHeader);
        if (!existingHeader[..4].SequenceEqual("RIFF"u8) ||
            !existingHeader[8..12].SequenceEqual("WAVE"u8) ||
            !existingHeader[12..16].SequenceEqual("fmt "u8) ||
            !existingHeader[36..40].SequenceEqual("data"u8))
        {
            throw new InvalidDataException("Interrupted recording is not a supported PCM WAV file.");
        }

        long audioBytes = stream.Length - HeaderLength;
        int blockAlignment = checked(format.Channels * (format.BitsPerSample / 8));
        if (audioBytes > uint.MaxValue || audioBytes % blockAlignment != 0)
            throw new InvalidDataException("Interrupted WAV has an invalid PCM payload length.");

        WriteHeader(stream, format, checked((uint)audioBytes));
        stream.Flush();
        return audioBytes / blockAlignment;
    }

    private void WriteHeader(uint audioBytes)
        => WriteHeader(stream, format, audioBytes);

    private static void WriteHeader(Stream target, PcmAudioFormat format, uint audioBytes)
    {
        Span<byte> header = stackalloc byte[HeaderLength];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)(36 + audioBytes)));
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], checked((ushort)format.Channels));
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], checked((uint)format.SampleRate));
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[28..],
            checked((uint)(format.SampleRate * format.Channels * (format.BitsPerSample / 8))));
        BinaryPrimitives.WriteUInt16LittleEndian(
            header[32..],
            checked((ushort)(format.Channels * (format.BitsPerSample / 8))));
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], checked((ushort)format.BitsPerSample));
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], audioBytes);

        target.Position = 0;
        target.Write(header);
        target.Position = target.Length;
    }
}
