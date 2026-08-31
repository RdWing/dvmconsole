using System.Buffers.Binary;
using DvmConsole.Audio;
using DvmConsole.Media;
using Xunit;

namespace DvmConsole.Media.Tests;

public sealed class PcmWavFileWriterTests
{
    [Fact]
    public async Task StreamsPcmAndFinalizesWaveHeader()
    {
        using var stream = new MemoryStream();
        await using (var writer = new PcmWavFileWriter(
                         stream,
                         PcmAudioFormat.Voice8KhzMono16Bit,
                         leaveOpen: true))
        {
            writer.Write(new short[] { -32768, 0, 32767 });
            Assert.Equal(3, writer.SamplesWritten);
        }

        byte[] file = stream.ToArray();
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(file, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(file, 8, 4));
        Assert.Equal("fmt ", System.Text.Encoding.ASCII.GetString(file, 12, 4));
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(file, 36, 4));
        Assert.Equal((uint)(file.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4, 4)));
        Assert.Equal((uint)(file.Length - 44), BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(40, 4)));
        Assert.Equal((short)-32768, BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(44, 2)));
        Assert.Equal((short)32767, BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(48, 2)));
    }

    [Fact]
    public void WritesToCallerOwnedStreamWithoutClosingIt()
    {
        using var stream = new MemoryStream();
        using (var writer = new PcmWavFileWriter(
                   stream,
                   PcmAudioFormat.Voice8KhzMono16Bit,
                   leaveOpen: true))
        {
            writer.Write(new short[] { -12, 34 });
        }

        Assert.True(stream.CanWrite);
        byte[] wave = stream.ToArray();
        Assert.Equal((uint)4, BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(40, 4)));
        Assert.Equal((short)-12, BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(44, 2)));
        Assert.Equal((short)34, BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(46, 2)));
    }

    [Fact]
    public void RejectsAReadOnlyStream()
    {
        using var stream = new MemoryStream([1, 2, 3], writable: false);

        Assert.Throws<ArgumentException>(() => new PcmWavFileWriter(
            stream,
            PcmAudioFormat.Voice8KhzMono16Bit));
    }

    [Fact]
    public void RepairsInterruptedHeaderFromPersistedPcmBytes()
    {
        using var stream = new MemoryStream();
        using (var writer = new PcmWavFileWriter(
                   stream,
                   PcmAudioFormat.Voice8KhzMono16Bit,
                   leaveOpen: true))
        {
            writer.Write(Enumerable.Repeat((short)1200, 800).ToArray());
        }

        stream.Position = 4;
        stream.Write(new byte[4]);
        stream.Position = 40;
        stream.Write(new byte[4]);

        long samples = PcmWavFileWriter.RepairInterruptedStream(
            stream,
            PcmAudioFormat.Voice8KhzMono16Bit);
        byte[] file = stream.ToArray();

        Assert.Equal(800, samples);
        Assert.Equal((uint)(file.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4, 4)));
        Assert.Equal((uint)(file.Length - 44), BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(40, 4)));
    }

    [Fact]
    public void TrimsSilenceWithLegacyPaddingAndReportsActivity()
    {
        using var source = new MemoryStream();
        short[] samples = new short[8000];
        samples[2000] = 1000;
        samples[5000] = -1000;
        using (var writer = new PcmWavFileWriter(
                   source,
                   PcmAudioFormat.Voice8KhzMono16Bit,
                   leaveOpen: true))
        {
            writer.Write(samples);
        }
        using var destination = new MemoryStream();

        PcmWavTrimResult result = PcmWavSilenceTrimmer.Trim(
            source,
            destination,
            PcmAudioFormat.Voice8KhzMono16Bit);

        Assert.Equal(8000, result.OriginalSamples);
        Assert.Equal(5120, result.OutputSamples);
        Assert.Equal(120, result.TrimLeadMs);
        Assert.Equal(240, result.TrimTailMs);
        Assert.Equal(1000, result.PeakAmplitude);
        Assert.Equal(2, result.ActiveSampleCount);
        Assert.Equal(44 + (5120 * sizeof(short)), destination.Length);
    }
}
