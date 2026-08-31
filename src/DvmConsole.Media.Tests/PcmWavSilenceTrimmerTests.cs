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
        using MemoryStream source = CreateWave([0, 0, 800, 900, 0]);
        byte[] original = source.ToArray();

        PcmWavTrimAnalysis analysis = PcmWavSilenceTrimmer.Analyze(
            source,
            PcmAudioFormat.Voice8KhzMono16Bit,
            paddingMilliseconds: 0,
            windowSamples: 1);

        Assert.Equal(2, analysis.StartSample);
        Assert.Equal(2, analysis.Result.OutputSamples);
        Assert.Equal(original, source.ToArray());
    }

    [Fact]
    public void AllSilenceIsRetainedByTheLegacyPolicy()
    {
        using MemoryStream source = CreateWave(new short[320]);
        using var destination = new MemoryStream();

        PcmWavTrimResult result = PcmWavSilenceTrimmer.Trim(
            source, destination, PcmAudioFormat.Voice8KhzMono16Bit, paddingMilliseconds: 0);

        Assert.Equal(320, result.OriginalSamples);
        Assert.Equal(320, result.OutputSamples);
        Assert.Equal(0, result.TrimTailMs);
        Assert.Equal(44 + (320 * sizeof(short)), destination.Length);
    }

    [Theory]
    [InlineData((short)399, false)]
    [InlineData((short)400, true)]
    [InlineData(short.MinValue, true)]
    public void UsesInclusiveThresholdAndSafelyMeasuresShortMinValue(short sample, bool active)
    {
        using MemoryStream source = CreateWave([0, sample, 0]);
        using var destination = new MemoryStream();
        PcmWavTrimResult result = PcmWavSilenceTrimmer.Trim(
            source,
            destination,
            PcmAudioFormat.Voice8KhzMono16Bit,
            paddingMilliseconds: 0,
            windowSamples: 1);

        Assert.Equal(active ? 1 : 0, result.ActiveSampleCount);
        Assert.Equal(active ? 1 : 3, result.OutputSamples);
        Assert.Equal(Math.Abs((int)sample), result.PeakAmplitude);
    }

    [Fact]
    public void PreservesLegacyShiftedTrailingWindowForNonDivisibleRecording()
    {
        short[] samples = new short[325];
        samples[165] = 1000;
        using MemoryStream source = CreateWave(samples);
        using var destination = new MemoryStream();

        PcmWavTrimResult result = PcmWavSilenceTrimmer.Trim(
            source,
            destination,
            PcmAudioFormat.Voice8KhzMono16Bit,
            windowSamples: 160,
            paddingMilliseconds: 0);

        Assert.Equal(160, result.TrimLeadMs * 8);
        Assert.Equal(165, result.OutputSamples);
    }

    [Theory]
    [InlineData("RIFX", "WAVE", "data")]
    [InlineData("RIFF", "NOPE", "data")]
    [InlineData("RIFF", "WAVE", "JUNK")]
    public void RejectsNoncanonicalHeadersWithoutReplacingRecording(string riff, string wave, string data)
    {
        using MemoryStream source = CreateWave([1, 2], riff, wave, data);
        using var destination = new MemoryStream();
        byte[] original = source.ToArray();

        Assert.Throws<InvalidDataException>(() => PcmWavSilenceTrimmer.Trim(
            source,
            destination,
            PcmAudioFormat.Voice8KhzMono16Bit));
        Assert.Equal(original, source.ToArray());
    }

    [Theory]
    [InlineData(8)]
    [InlineData(2)]
    [InlineData(5)]
    public void RejectsTruncatedOrSizeMismatchedDataWithoutReplacingRecording(int declaredBytes)
    {
        using MemoryStream source = CreateWave([1000, 0], declaredBytes: declaredBytes);
        using var destination = new MemoryStream();
        byte[] original = source.ToArray();

        Assert.Throws<InvalidDataException>(() => PcmWavSilenceTrimmer.Trim(
            source,
            destination,
            PcmAudioFormat.Voice8KhzMono16Bit));
        Assert.Equal(original, source.ToArray());
    }

    private static MemoryStream CreateWave(
        short[] samples,
        string riff = "RIFF",
        string wave = "WAVE",
        string data = "data",
        int? declaredBytes = null)
    {
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
        return new MemoryStream(bytes, writable: true);
    }
}
