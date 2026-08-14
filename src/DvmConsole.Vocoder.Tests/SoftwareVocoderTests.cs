using DvmConsole.Vocoder;
using Xunit;
using Xunit.Sdk;

namespace DvmConsole.Vocoder.Tests;

public sealed class SoftwareVocoderTests
{
    [Theory]
    [InlineData(VocoderMode.DmrAmbe, 9)]
    [InlineData(VocoderMode.P25Imbe, 11)]
    public void ReportsLegacyFrameSizes(VocoderMode mode, int expectedCodewordBytes)
    {
        Assert.Equal(160, VocoderFrameSizes.PcmSamplesPerFrame);
        Assert.Equal(expectedCodewordBytes, VocoderFrameSizes.CodewordBytes(mode));
    }

    [Theory]
    [InlineData(VocoderMode.DmrAmbe)]
    [InlineData(VocoderMode.P25Imbe)]
    public void EncodesAndDecodesWhenNativeLibraryIsProvided(VocoderMode mode)
    {
        string? libraryPath = Environment.GetEnvironmentVariable("DVMVOCODER_LIBRARY");
        if (string.IsNullOrWhiteSpace(libraryPath) || !File.Exists(libraryPath))
            throw SkipException.ForSkip("Set DVMVOCODER_LIBRARY to run the native software vocoder test.");

        short[] samples = Enumerable.Range(0, VocoderFrameSizes.PcmSamplesPerFrame)
            .Select(index => (short)(Math.Sin(index * 0.15) * 12000))
            .ToArray();
        byte[] codeword = new byte[VocoderFrameSizes.CodewordBytes(mode)];
        short[] decodedSamples = new short[VocoderFrameSizes.PcmSamplesPerFrame];

        using var backend = new SoftwareVocoderBackend(libraryPath);
        using IVocoderSession session = backend.CreateSession(mode);

        Assert.Equal(codeword.Length, session.Encode(samples, codeword));
        Assert.Equal(0, session.Decode(codeword, decodedSamples));
        Assert.Contains(decodedSamples, sample => sample != 0);
    }
}
