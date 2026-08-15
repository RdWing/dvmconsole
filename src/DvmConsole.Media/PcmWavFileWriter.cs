using System.Buffers.Binary;
using DvmConsole.Audio;

namespace DvmConsole.Media;

/// <summary>
/// Streams signed little-endian PCM samples to a RIFF/WAVE file and patches
/// the RIFF/data lengths when the recording ends. It contains no TAR policy;
/// callers own naming, retention, and call lifecycle decisions.
/// </summary>
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

    private void WriteHeader(uint audioBytes)
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

        stream.Position = 0;
        stream.Write(header);
        stream.Position = stream.Length;
    }
}
