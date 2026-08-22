using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Vocoder.Tests;

public sealed class VoiceFramePipelineTests
{
    [Fact]
    public void EncoderRetainsOnlyOneFixedPcmFrameAcrossArbitraryChunks()
    {
        var session = new RecordingSession();
        using var encoder = new VoiceFrameEncoder(session, VocoderMode.DmrAmbe);
        short[] samples = Enumerable.Range(0, 480).Select(static value => (short)value).ToArray();

        Assert.Equal(0, encoder.Process(samples.AsSpan(0, 17), static _ => { }));
        Assert.Equal(2, encoder.Process(samples.AsSpan(17, 303), static _ => { }));
        Assert.Equal(1, encoder.Process(samples.AsSpan(320), static _ => { }));

        Assert.Equal(samples.AsSpan(0, 160).ToArray(), session.EncodedFrames[0]);
        Assert.Equal(samples.AsSpan(160, 160).ToArray(), session.EncodedFrames[1]);
        Assert.Equal(samples.AsSpan(320, 160).ToArray(), session.EncodedFrames[2]);
    }

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

    [Fact]
    public void DecoderFillsCallerOwnedPcmWithoutAllocatingAFrameCallback()
    {
        var session = new FakeVocoderSession();
        using var decoder = new VoiceFrameDecoder(session, VocoderMode.P25Imbe);
        var samples = new short[VocoderFrameSizes.PcmSamplesPerFrame];

        int errors = decoder.Process(
            new byte[] { 9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
            samples);

        Assert.Equal(0, errors);
        Assert.All(samples, sample => Assert.Equal((short)9, sample));
        Assert.Equal(1, session.DecodeCalls);
    }

    [Fact]
    public void EncoderFlushEmitsDelayedFrameOnce()
    {
        var session = new FakeVocoderSession { FlushValue = 0x5A };
        using var encoder = new VoiceFrameEncoder(session, VocoderMode.DmrAmbe);
        var codewords = new List<byte[]>();

        Assert.Equal(1, encoder.Process(new short[160], codeword => codewords.Add(codeword.ToArray())));
        Assert.Equal(1, encoder.Flush(codeword => codewords.Add(codeword.ToArray())));
        Assert.Equal(0, encoder.Flush(codeword => codewords.Add(codeword.ToArray())));

        Assert.Equal(2, codewords.Count);
        Assert.All(codewords[1], value => Assert.Equal(0x5A, value));
        Assert.Equal(1, session.FlushCalls);
    }

    [Fact]
    public void EncoderFlushRejectsIncompletePcmAndDoesNotInventEmptyFrame()
    {
        var session = new FakeVocoderSession { FlushValue = 0x5A };
        using var encoder = new VoiceFrameEncoder(session, VocoderMode.DmrAmbe);

        Assert.Equal(0, encoder.Flush(_ => throw new InvalidOperationException()));
        Assert.Equal(0, session.FlushCalls);
        encoder.Process(new short[1], _ => throw new InvalidOperationException());
        Assert.Throws<InvalidOperationException>(() => encoder.Flush(_ => { }));
        Assert.Equal(0, session.FlushCalls);
    }

    private sealed class FakeVocoderSession : IVocoderSession
    {
        public int EncodeCalls { get; private set; }
        public int DecodeCalls { get; private set; }
        public int FlushCalls { get; private set; }
        public byte? FlushValue { get; init; }

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

        public int FlushEncode(Span<byte> codeword)
        {
            FlushCalls++;
            if (FlushValue is not byte value)
                return 0;
            codeword.Fill(value);
            return codeword.Length;
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingSession : IVocoderSession
    {
        public List<short[]> EncodedFrames { get; } = [];

        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword)
        {
            EncodedFrames.Add(samples.ToArray());
            codeword.Clear();
            return codeword.Length;
        }

        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples)
            => throw new NotSupportedException();

        public int FlushEncode(Span<byte> codeword) => 0;

        public void Dispose()
        {
        }
    }
}
