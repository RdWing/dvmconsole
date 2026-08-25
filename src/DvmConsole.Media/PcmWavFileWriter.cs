using System.Buffers.Binary;
using DvmConsole.Audio;

namespace DvmConsole.Media;

// Streams signed little-endian PCM samples to a RIFF/WAVE file and patches
// the RIFF/data lengths when the recording ends. It contains no TAR policy;
// callers own naming, retention, and call lifecycle decisions.
public sealed class PcmWavFileWriter : IDisposable, IAsyncDisposable
{
    private const int HeaderLength = 44;
    private readonly FileStream stream;
    private readonly PcmAudioFormat format;
    private long dataBytes;
    private bool disposed;

    public PcmWavFileWriter(string path, PcmAudioFormat format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(format);
        if (format.BitsPerSample != 16)
            throw new ArgumentException("WAV recording requires 16-bit PCM.", nameof(format));

        Path = System.IO.Path.GetFullPath(path);
        string? directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        this.format = format;
        stream = new FileStream(
            Path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 16 * 1024,
            options: FileOptions.SequentialScan);
        WriteHeader(0);
    }

    public string Path { get; }
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

        byte[] bytes = new byte[bytesToWrite];
        for (int index = 0; index < samples.Length; index++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(index * sizeof(short), sizeof(short)), samples[index]);

        stream.Write(bytes);
        dataBytes += bytesToWrite;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        try
        {
            WriteHeader((uint)dataBytes);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
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
    /// actually reached disk. The fixed PCM format and RIFF signatures are
    /// validated before any data is changed.
    /// </summary>
    public static long RepairInterruptedFile(string path, PcmAudioFormat format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(format);
        if (format.BitsPerSample != 16)
            throw new ArgumentException("WAV recording requires 16-bit PCM.", nameof(format));

        using var source = new FileStream(
            System.IO.Path.GetFullPath(path),
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read);
        if (source.Length < HeaderLength)
            throw new InvalidDataException("Interrupted WAV is shorter than its header.");

        Span<byte> existingHeader = stackalloc byte[HeaderLength];
        source.ReadExactly(existingHeader);
        if (!existingHeader[..4].SequenceEqual("RIFF"u8) ||
            !existingHeader[8..12].SequenceEqual("WAVE"u8) ||
            !existingHeader[12..16].SequenceEqual("fmt "u8) ||
            !existingHeader[36..40].SequenceEqual("data"u8))
        {
            throw new InvalidDataException("Interrupted recording is not a supported PCM WAV file.");
        }

        long audioBytes = source.Length - HeaderLength;
        int blockAlignment = checked(format.Channels * (format.BitsPerSample / 8));
        if (audioBytes > uint.MaxValue || audioBytes % blockAlignment != 0)
            throw new InvalidDataException("Interrupted WAV has an invalid PCM payload length.");

        WriteHeader(source, format, checked((uint)audioBytes));
        source.Flush(flushToDisk: true);
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
