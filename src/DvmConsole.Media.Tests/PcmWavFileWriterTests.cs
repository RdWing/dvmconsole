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
        string path = CreatePath();
        try
        {
            await using (var writer = new PcmWavFileWriter(path, PcmAudioFormat.Voice8KhzMono16Bit))
            {
                writer.Write(new short[] { -32768, 0, 32767 });
                Assert.Equal(3, writer.SamplesWritten);
            }

            byte[] file = File.ReadAllBytes(path);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(file, 0, 4));
            Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(file, 8, 4));
            Assert.Equal("fmt ", System.Text.Encoding.ASCII.GetString(file, 12, 4));
            Assert.Equal("data", System.Text.Encoding.ASCII.GetString(file, 36, 4));
            Assert.Equal((uint)(file.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4, 4)));
            Assert.Equal((uint)(file.Length - 44), BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(40, 4)));
            Assert.Equal((short)-32768, BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(44, 2)));
            Assert.Equal((short)32767, BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(48, 2)));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void DoesNotOverwriteAnExistingRecording()
    {
        string path = CreatePath();
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [1, 2, 3]);

            Assert.Throws<IOException>(() => new PcmWavFileWriter(path, PcmAudioFormat.Voice8KhzMono16Bit));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void TrimsSilenceWithLegacyPaddingAndReportsActivity()
    {
        string path = CreatePath();
        try
        {
            short[] samples = new short[8000];
            samples[2000] = 1000;
            samples[5000] = -1000;
            using (var writer = new PcmWavFileWriter(path, PcmAudioFormat.Voice8KhzMono16Bit))
                writer.Write(samples);

            PcmWavTrimResult result = PcmWavSilenceTrimmer.TrimFile(path, PcmAudioFormat.Voice8KhzMono16Bit);

            Assert.Equal(8000, result.OriginalSamples);
            Assert.Equal(5120, result.OutputSamples);
            Assert.Equal(120, result.TrimLeadMs);
            Assert.Equal(240, result.TrimTailMs);
            Assert.Equal(1000, result.PeakAmplitude);
            Assert.Equal(2, result.ActiveSampleCount);
            Assert.Equal(44 + (5120 * sizeof(short)), new FileInfo(path).Length);
        }
        finally
        {
            Cleanup(path);
        }
    }

    private static string CreatePath()
        => System.IO.Path.Combine(Path.GetTempPath(), "dvmconsole-wav-tests", $"{Guid.NewGuid():N}", "call.wav");

    private static void Cleanup(string path)
    {
        string? directory = System.IO.Path.GetDirectoryName(path);
        if (File.Exists(path))
            File.Delete(path);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
