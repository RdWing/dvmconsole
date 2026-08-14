using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Vocoder.Tests;

public sealed class VoiceFramePipelineTests
{
    [Fact]
    public void EncoderTurnsArbitraryPcmChunksIntoFixedCodewords()
    {
        var session = new FakeVocoderSession();
        using var encoder = new VoiceFrameEncoder(session, VocoderMode.DmrAmbe);
        var codewords = new List<byte[]>();

        Assert.Equal(0, encoder.Process(new short[100], codeword => codewords.Add(codeword.ToArray())));
        Assert.Equal(1, encoder.Process(new short[100], codeword => codewords.Add(codeword.ToArray())));
        Assert.Equal(1, encoder.Process(new short[120], codeword => codewords.Add(codeword.ToArray())));

        Assert.Equal(2, session.EncodeCalls);
        Assert.Equal(2, codewords.Count);
        Assert.All(codewords, codeword => Assert.Equal(9, codeword.Length));
    }

    [Fact]
    public void DecoderEmitsOnePcmFrameAndReturnsNativeErrorCount()
    {
        var session = new FakeVocoderSession();
        using var decoder = new VoiceFrameDecoder(session, VocoderMode.P25Imbe);
        var frames = new List<short[]>();

        int errors = decoder.Process(new byte[] { 7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, frame => frames.Add(frame.ToArray()));

        Assert.Equal(0, errors);
        Assert.Single(frames);
        Assert.Equal(160, frames[0].Length);
        Assert.All(frames[0], sample => Assert.Equal((short)7, sample));
        Assert.Equal(1, session.DecodeCalls);
    }

    private sealed class FakeVocoderSession : IVocoderSession
    {
        public int EncodeCalls { get; private set; }
        public int DecodeCalls { get; private set; }

        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword)
        {
            Assert.Equal(160, samples.Length);
            EncodeCalls++;
            codeword.Fill((byte)EncodeCalls);
            return codeword.Length;
        }

        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples)
        {
            DecodeCalls++;
            samples.Fill(codeword[0]);
            return 0;
        }

        public void Dispose()
        {
        }
    }
}
