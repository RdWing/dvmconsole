using System.Buffers;
using System.Buffers.Binary;
using DvmConsole.Audio;
using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class WavPcmStreamReaderTests
{
    [Fact]
    public async Task ReadsStereoSixteenBitPcmFromNonSeekableWavStream()
    {
        byte[] wav = CreateWav(
            sampleRate: 8000,
            channels: 2,
            bitsPerSample: 16,
            samples:
            [
                1000, 3000,
                -2000, 0
            ]);

        await using var reader = await WavPcmStreamReader.OpenAsync(new NonSeekableStream(wav));
        short[] samples = new short[4];

        int count = await reader.ReadSamplesAsync(samples);

        Assert.Equal(2, count);
        Assert.Equal([2000, -1000], samples[..count]);
        Assert.True(reader.EndOfStream);
    }

    [Fact]
    public async Task ReadsEightBitMonoPcmAndSkipsUnknownChunks()
    {
        byte[] wav = CreateWav(
            sampleRate: 16000,
            channels: 1,
            bitsPerSample: 8,
            samples: [0, 128, 255],
            includeJunkChunk: true);

        await using var reader = await WavPcmStreamReader.OpenAsync(new MemoryStream(wav));
        short[] samples = new short[3];

        int count = await reader.ReadSamplesAsync(samples);

        Assert.Equal(3, count);
        Assert.Equal([-32768, 0, 32512], samples);
        Assert.Equal(16000, reader.SampleRate);
    }

    [Fact]
    public async Task RejectsCompressedWavInsteadOfUsingPlatformDecoder()
    {
        byte[] wav = CreateWav(sampleRate: 8000, channels: 1, bitsPerSample: 16, samples: [0], formatTag: 3);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            WavPcmStreamReader.OpenAsync(new MemoryStream(wav)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SkipsPcmFramesOnSeekableAndStreamingSources(bool nonSeekable)
    {
        byte[] wav = CreateWav(
            sampleRate: 8000,
            channels: 1,
            bitsPerSample: 16,
            samples: [100, 200, 300, 400]);
        Stream source = nonSeekable
            ? new NonSeekableStream(wav)
            : new MemoryStream(wav);
        await using var reader = await WavPcmStreamReader.OpenAsync(source);

        long skipped = await reader.SkipSamplesAsync(2);
        short[] samples = new short[2];
        int count = await reader.ReadSamplesAsync(samples);

        Assert.Equal(2, skipped);
        Assert.Equal(2, count);
        Assert.Equal([300, 400], samples);
    }

    [Fact]
    public async Task ReusesAndReturnsDecodeBufferAcrossReads()
    {
        byte[] wav = CreateWav(
            sampleRate: 8000,
            channels: 1,
            bitsPerSample: 16,
            samples: [100, 200, 300, 400]);
        var pool = new TrackingArrayPool();

        await using (var reader = await WavPcmStreamReader.OpenAsync(
            new MemoryStream(wav),
            pool))
        {
            short[] sample = new short[1];
            Assert.Equal(1, await reader.ReadSamplesAsync(sample));
            Assert.Equal(100, sample[0]);
            Assert.Equal(1, await reader.ReadSamplesAsync(sample));
            Assert.Equal(200, sample[0]);
            Assert.Equal(1, pool.RentCount);
            Assert.Equal(0, pool.ReturnCount);
        }

        Assert.Equal(1, pool.RentCount);
        Assert.Equal(1, pool.ReturnCount);
    }

    private static byte[] CreateWav(
        int sampleRate,
        int channels,
        int bitsPerSample,
        IReadOnlyList<short> samples,
        bool includeJunkChunk = false,
        ushort formatTag = 1)
    {
        int bytesPerSample = bitsPerSample / 8;
        int blockAlign = channels * bytesPerSample;
        byte[] data = new byte[samples.Count * bytesPerSample];
        for (int index = 0; index < samples.Count; index++)
        {
            if (bitsPerSample == 8)
                data[index] = (byte)Math.Clamp((int)samples[index], 0, 255);
            else
                BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(index * 2, 2), samples[index]);
        }

        using var output = new MemoryStream();
        WriteAscii(output, "RIFF");
        WriteUInt32(output, 0);
        WriteAscii(output, "WAVE");
        if (includeJunkChunk)
        {
            WriteAscii(output, "JUNK");
            WriteUInt32(output, 3);
            output.Write([1, 2, 3]);
            output.WriteByte(0);
        }

        WriteAscii(output, "fmt ");
        WriteUInt32(output, 16);
        WriteUInt16(output, formatTag);
        WriteUInt16(output, (ushort)channels);
        WriteUInt32(output, (uint)sampleRate);
        WriteUInt32(output, (uint)(sampleRate * blockAlign));
        WriteUInt16(output, (ushort)blockAlign);
        WriteUInt16(output, (ushort)bitsPerSample);
        WriteAscii(output, "data");
        WriteUInt32(output, (uint)data.Length);
        output.Write(data);

        byte[] result = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), (uint)(result.Length - 8));
        return result;
    }

    private static void WriteAscii(Stream output, string value)
        => output.Write(System.Text.Encoding.ASCII.GetBytes(value));

    private static void WriteUInt16(Stream output, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        output.Write(bytes);
    }

    private static void WriteUInt32(Stream output, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        output.Write(bytes);
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
    }

    private sealed class TrackingArrayPool : ArrayPool<byte>
    {
        public int RentCount { get; private set; }
        public int ReturnCount { get; private set; }

        public override byte[] Rent(int minimumLength)
        {
            RentCount++;
            return new byte[Math.Max(1, minimumLength)];
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            ArgumentNullException.ThrowIfNull(array);
            ReturnCount++;
        }
    }
}
