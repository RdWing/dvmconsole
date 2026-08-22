using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Vocoder.Tests;

public sealed class SoftwareVocoderTests
{
    [Theory]
    [InlineData(VocoderMode.DmrAmbe, 9)]
    [InlineData(VocoderMode.P25Imbe, 11)]
    [InlineData(VocoderMode.NxdnAmbe, 9)]
    [InlineData(VocoderMode.P25Phase2Ambe, 9)]
    public void ReportsWireFrameSizes(VocoderMode mode, int expectedCodewordBytes)
    {
        Assert.Equal(160, VocoderFrameSizes.PcmSamplesPerFrame);
        Assert.Equal(expectedCodewordBytes, VocoderFrameSizes.CodewordBytes(mode));
    }

    [Theory]
    [InlineData(VocoderMode.DmrAmbe)]
    [InlineData(VocoderMode.P25Imbe)]
    [InlineData(VocoderMode.NxdnAmbe)]
    [InlineData(VocoderMode.P25Phase2Ambe)]
    public void RequiredNativeBackendEncodesDecodesAndFlushes(VocoderMode mode)
    {
        short[] samples = Enumerable.Range(0, VocoderFrameSizes.PcmSamplesPerFrame)
            .Select(index => (short)(Math.Sin(index * 0.15) * 12000))
            .ToArray();
        byte[] codeword = new byte[VocoderFrameSizes.CodewordBytes(mode)];
        short[] decodedSamples = new short[VocoderFrameSizes.PcmSamplesPerFrame];

        using var backend = new SoftwareVocoderBackend();
        using IVocoderSession session = backend.CreateSession(mode);

        Assert.Equal(codeword.Length, session.Encode(samples, codeword));
        Assert.Equal(0, session.Decode(codeword, decodedSamples));
        Assert.Equal(0, session.DecodeLost(decodedSamples));
        Assert.Equal(codeword.Length, session.FlushEncode(codeword));
        Assert.Equal(0, session.FlushEncode(codeword));
    }

    [Fact]
    public void ReceiveProcessingStagesCanBeConfiguredPerMode()
    {
        var options = new Dictionary<VocoderMode, ReceiveAudioProcessingOptions>
        {
            [VocoderMode.DmrAmbe] = new()
            {
                HighPassFilterEnabled = false,
                PeakingFilterEnabled = false,
                CompressorEnabled = true,
                CompressorRatio = 4,
                CompressorThresholdDbfs = -22,
                CompressorMakeupGainDb = 5
            }
        };
        using var backend = new SoftwareVocoderBackend(options);
        using IVocoderSession session = backend.CreateSession(VocoderMode.DmrAmbe);
        short[] samples = new short[VocoderFrameSizes.PcmSamplesPerFrame];

        Assert.Equal(0, session.Decode(
            Convert.FromHexString("ACAA40200044408080"),
            samples));
    }

    [Fact]
    public void ReceiveProcessingRejectsUnsupportedValues()
    {
        var options = new Dictionary<VocoderMode, ReceiveAudioProcessingOptions>
        {
            [VocoderMode.DmrAmbe] = new() { HighPassFrequencyHz = 525 }
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new SoftwareVocoderBackend(options));
    }

    [Fact]
    public void P25GeneratedTonesUseExplicitLookupFrames()
    {
        using var backend = new SoftwareVocoderBackend();
        using var session = Assert.IsAssignableFrom<IP25GeneratedToneVocoderSession>(
            backend.CreateSession(VocoderMode.P25Imbe));
        byte[] codeword = new byte[VocoderFrameSizes.CodewordBytes(VocoderMode.P25Imbe)];

        Assert.Equal(codeword.Length, session.EncodeSingleTone(1000, codeword));
        Assert.Equal("09230B0DC4A5CAE8280A32", Convert.ToHexString(codeword));

    }

    [Theory]
    [InlineData(VocoderMode.DmrAmbe)]
    [InlineData(VocoderMode.NxdnAmbe)]
    [InlineData(VocoderMode.P25Phase2Ambe)]
    public void HalfRateParameterBoundaryRoundTrips(VocoderMode mode)
    {
        using var backend = new SoftwareVocoderBackend();
        using var session = Assert.IsAssignableFrom<IHalfRateVocoderSession>(backend.CreateSession(mode));
        short[] samples = Enumerable.Range(0, VocoderFrameSizes.PcmSamplesPerFrame)
            .Select(index => (short)(Math.Sin(index * 0.11) * 14000))
            .ToArray();
        byte[] parameters = new byte[VocoderFrameSizes.HalfRateParameterBytes];
        byte[] codeword = new byte[VocoderFrameSizes.HalfRateCodewordBytes];
        byte[] recovered = new byte[VocoderFrameSizes.HalfRateParameterBytes];
        short[] decoded = new short[VocoderFrameSizes.PcmSamplesPerFrame];

        Assert.Equal(parameters.Length, session.EncodeParameters(samples, parameters));
        session.BuildCodeword(parameters, codeword);
        Assert.Equal(0, session.ExtractParameters(codeword, recovered));
        Assert.Equal(parameters, recovered);
        Assert.Equal(0, session.DecodeParameters(recovered, decoded));
        Assert.Equal(parameters.Length, session.FlushEncodeParameters(parameters));
        Assert.Equal(0, session.FlushEncodeParameters(parameters));
    }

    [Fact]
    public void DmrUncorrectableC0IsReportedAsLostFecStatus()
    {
        using var backend = new SoftwareVocoderBackend();
        using var session = Assert.IsAssignableFrom<IHalfRateVocoderSession>(
            backend.CreateSession(VocoderMode.DmrAmbe));
        byte[] parameters = Convert.FromHexString("123456789ABC80");
        byte[] codeword = new byte[VocoderFrameSizes.HalfRateCodewordBytes];
        session.BuildCodeword(parameters, codeword);
        // DMR c0 positions begin 0,4,8,12. Four flips are detected as an
        // extended-Golay erasure rather than misreported as a numeric count.
        codeword[0] ^= 0x88;
        codeword[1] ^= 0x88;
        byte[] recovered = new byte[VocoderFrameSizes.HalfRateParameterBytes];

        HalfRateFecStatus status = session.ExtractParametersWithStatus(codeword, recovered);

        Assert.True(status.Unrecoverable);
        Assert.Equal(15u, status.DecoderErrorMetric);
        short[] decoded = new short[VocoderFrameSizes.PcmSamplesPerFrame];
        Assert.Equal(0, session.DecodeParameters(
            recovered,
            decoded,
            status.DecoderErrorMetric,
            status.Unrecoverable));
    }

    [Fact]
    public void SessionKeepsNativeLibraryAliveAfterBackendIsDisposed()
    {
        var backend = new SoftwareVocoderBackend();
        IVocoderSession session = backend.CreateSession(VocoderMode.DmrAmbe);
        backend.Dispose();
        short[] samples = new short[VocoderFrameSizes.PcmSamplesPerFrame];
        byte[] codeword = new byte[VocoderFrameSizes.HalfRateCodewordBytes];

        Assert.Equal(codeword.Length, session.Encode(samples, codeword));
        Assert.Equal(0, session.Decode(codeword, samples));
        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => backend.CreateSession(VocoderMode.DmrAmbe));
    }

    [Fact]
    public void NativeEncodeUsesCallerBuffersWithoutPerFrameManagedAllocations()
    {
        using var backend = new SoftwareVocoderBackend();
        using IVocoderSession session = backend.CreateSession(VocoderMode.DmrAmbe);
        var samples = new short[VocoderFrameSizes.PcmSamplesPerFrame];
        var codeword = new byte[VocoderFrameSizes.HalfRateCodewordBytes];
        for (int index = 0; index < 100; index++)
            session.Encode(samples, codeword);

        const int iterations = 1_000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < iterations; index++)
            session.Encode(samples, codeword);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            allocated <= 1_024,
            $"Expected caller-owned native buffers; observed {allocated / (double)iterations:F1} managed bytes per encode.");
    }
}
