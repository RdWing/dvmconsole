using DvmConsole.Media;
using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Media.Tests;

public sealed class P25TxCallSessionTests
{
    [Fact]
    public void EmitsGrantDemandVoiceLdUsAndFourTerminatorTdus()
    {
        var packets = new List<(byte[] Payload, ushort Sequence, uint Stream)>();
        using var session = new P25TxCallSession(
            sourceId: 0x010203,
            destinationId: 0xA0B0C0,
            streamId: 77,
            vocoder: new FakeVocoderSession(),
            send: (payload, sequence, stream) => packets.Add((payload.ToArray(), sequence, stream)));

        session.Start();

        Assert.Single(packets);
        Assert.Equal(P25DfsiFrameCodec.RtpCallEndSequence, packets[0].Sequence);
        Assert.Equal(P25DfsiFrameCodec.TduDuid, packets[0].Payload[22]);
        Assert.Equal((byte)0x80, packets[0].Payload[14]);

        Assert.Equal(2, session.Process(new short[18 * 160]));
        session.End();

        Assert.Equal(7, packets.Count);
        Assert.Equal((ushort)0, packets[1].Sequence);
        Assert.Equal(P25DfsiFrameCodec.Ldu1Duid, packets[1].Payload[22]);
        Assert.Equal((ushort)1, packets[2].Sequence);
        Assert.Equal(P25DfsiFrameCodec.Ldu2Duid, packets[2].Payload[22]);
        Assert.All(packets.Skip(3), packet =>
        {
            Assert.Equal(P25DfsiFrameCodec.RtpCallEndSequence, packet.Sequence);
            Assert.Equal(P25DfsiFrameCodec.TduDuid, packet.Payload[22]);
            Assert.Equal((byte)0, packet.Payload[14]);
        });
        Assert.True(session.IsEnded);
        Assert.Equal(18, session.CodewordsEncoded);
        Assert.Equal(2, session.LdusSent);
    }

    [Fact]
    public void RequiresAnExplicitStartBeforeProcessingAudio()
    {
        using var session = new P25TxCallSession(
            sourceId: 1,
            destinationId: 2,
            streamId: 3,
            vocoder: new FakeVocoderSession(),
            send: (_, _, _) => { });

        Assert.Throws<InvalidOperationException>(() => session.Process(new short[160]));
    }

    private sealed class FakeVocoderSession : IVocoderSession
    {
        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword)
        {
            codeword.Clear();
            return codeword.Length;
        }

        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples) => 0;
        public void Dispose()
        {
        }
    }
}
