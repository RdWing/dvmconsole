using System.Buffers.Binary;
using DvmConsole.Audio;
using DvmConsole.Media;
using Xunit;

namespace DvmConsole.Media.Tests;

public sealed class PcmWavSilenceTrimmerTests
{
    [Fact]
    public void AnalyzeReportsTrimRangeWithoutRewritingDurableWave()
    {
        string path = CreatePath();
        try
        {
            short[] samples = [0, 0, 800, 900, 0];
            WriteWave(path, samples);
            byte[] original = File.ReadAllBytes(path);

            PcmWavTrimAnalysis analysis = PcmWavSilenceTrimmer.AnalyzeFile(
                path,
                PcmAudioFormat.Voice8KhzMono16Bit,
                paddingMilliseconds: 0,
                windowSamples: 1);

            Assert.Equal(2, analysis.StartSample);
            Assert.Equal(2, analysis.Result.OutputSamples);
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void AllSilenceIsRetainedByTheLegacyPolicy()
    {
        string path = CreatePath();
        try
        {
            WriteWave(path, new short[320]);

            PcmWavTrimResult result = PcmWavSilenceTrimmer.TrimFile(
                path, PcmAudioFormat.Voice8KhzMono16Bit, paddingMilliseconds: 0);

            Assert.Equal(320, result.OriginalSamples);
            Assert.Equal(320, result.OutputSamples);
            Assert.Equal(0, result.TrimTailMs);
            Assert.Equal(44 + (320 * sizeof(short)), new FileInfo(path).Length);
        }
        finally { Cleanup(path); }
    }

    [Theory]
    [InlineData((short)399, false)]
    [InlineData((short)400, true)]
    [InlineData(short.MinValue, true)]
    public void UsesInclusiveThresholdAndSafelyMeasuresShortMinValue(short sample, bool active)
    {
        string path = CreatePath();
        try
        {
            WriteWave(path, [0, sample, 0]);
            PcmWavTrimResult result = PcmWavSilenceTrimmer.TrimFile(
                path, PcmAudioFormat.Voice8KhzMono16Bit, paddingMilliseconds: 0, windowSamples: 1);

            Assert.Equal(active ? 1 : 0, result.ActiveSampleCount);
            Assert.Equal(active ? 1 : 3, result.OutputSamples);
            Assert.Equal(Math.Abs((int)sample), result.PeakAmplitude);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void PreservesLegacyShiftedTrailingWindowForNonDivisibleRecording()
    {
        string path = CreatePath();
        try
        {
            short[] samples = new short[325];
            samples[165] = 1000;
            WriteWave(path, samples);

            PcmWavTrimResult result = PcmWavSilenceTrimmer.TrimFile(
                path, PcmAudioFormat.Voice8KhzMono16Bit, windowSamples: 160, paddingMilliseconds: 0);

            Assert.Equal(160, result.TrimLeadMs * 8);
            Assert.Equal(165, result.OutputSamples);
        }
        finally { Cleanup(path); }
    }

    [Theory]
    [InlineData("RIFX", "WAVE", "data")]
    [InlineData("RIFF", "NOPE", "data")]
    [InlineData("RIFF", "WAVE", "JUNK")]
    public void RejectsNoncanonicalHeadersWithoutReplacingRecording(string riff, string wave, string data)
    {
        string path = CreatePath();
        try
        {
            WriteWave(path, [1, 2], riff, wave, data);
            byte[] original = File.ReadAllBytes(path);

            Assert.Throws<InvalidDataException>(() => PcmWavSilenceTrimmer.TrimFile(path, PcmAudioFormat.Voice8KhzMono16Bit));
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally { Cleanup(path); }
    }

    [Theory]
    [InlineData(8)]
    [InlineData(2)]
    [InlineData(5)]
    public void RejectsTruncatedOrSizeMismatchedDataWithoutReplacingRecording(int declaredBytes)
    {
        string path = CreatePath();
        try
        {
            WriteWave(path, [1000, 0], declaredBytes: declaredBytes);
            byte[] original = File.ReadAllBytes(path);

            Assert.Throws<InvalidDataException>(() => PcmWavSilenceTrimmer.TrimFile(path, PcmAudioFormat.Voice8KhzMono16Bit));
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally { Cleanup(path); }
    }

    private static void WriteWave(string path, short[] samples, string riff = "RIFF", string wave = "WAVE", string data = "data", int? declaredBytes = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] bytes = new byte[44 + samples.Length * 2];
        System.Text.Encoding.ASCII.GetBytes(riff).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), (uint)(bytes.Length - 8));
        System.Text.Encoding.ASCII.GetBytes(wave).CopyTo(bytes, 8);
        System.Text.Encoding.ASCII.GetBytes("fmt ").CopyTo(bytes, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24, 4), 8000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28, 4), 16000);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34, 2), 16);
        System.Text.Encoding.ASCII.GetBytes(data).CopyTo(bytes, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40, 4), (uint)(declaredBytes ?? samples.Length * 2));
        for (int index = 0; index < samples.Length; index++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(44 + index * 2, 2), samples[index]);
        File.WriteAllBytes(path, bytes);
    }

    private static string CreatePath() => Path.Combine(Path.GetTempPath(), "dvmconsole-wav-trimmer-tests", $"{Guid.NewGuid():N}.wav");
    private static void Cleanup(string path) { if (File.Exists(path)) File.Delete(path); }
}
