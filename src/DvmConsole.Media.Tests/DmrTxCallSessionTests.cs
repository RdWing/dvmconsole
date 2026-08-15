using DvmConsole.Media;
using DvmConsole.Vocoder;
using fnecore.DMR;
using Xunit;

namespace DvmConsole.Media.Tests;

[Collection("DMR wire codec")]
public sealed class DmrTxCallSessionTests
{
    [Fact]
    public void EmitsVoiceHeaderVoicePacketAndTerminatorForOneCall()
    {
        var packets = new List<(byte[] Payload, ushort Sequence, uint Stream)>();
        using var session = new DmrTxCallSession(
            sourceId: 0x010203,
            destinationId: 0xA0B0C0,
            slot: 0,
            streamId: 77,
            vocoder: new FakeVocoderSession(),
            send: (payload, sequence, stream) => packets.Add((payload.ToArray(), sequence, stream)));

        session.Start();
        var headerPacket = Assert.Single(packets);
        Assert.Equal((ushort)0, headerPacket.Sequence);
        Assert.Equal((uint)77, headerPacket.Stream);
        Assert.Equal((byte)0x22, headerPacket.Payload[15]);

        LC headerLc = Assert.IsType<LC>(FullLC.Decode(
            headerPacket.Payload[20..],
            DMRDataType.VOICE_LC_HEADER));
        Assert.Equal((uint)0x010203, headerLc.SrcId);
        Assert.Equal((uint)0xA0B0C0, headerLc.DstId);

        Assert.Equal(1, session.Process(new short[480]));
        session.End();

        Assert.Equal(8, packets.Count);
        Assert.Equal((ushort)1, packets[1].Sequence);
        Assert.Equal((byte)0x10, packets[1].Payload[15]);
        Assert.Equal(
            new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 },
            packets.Skip(2).Take(5).Select(packet => packet.Payload[15]));
        Assert.Equal((ushort)7, packets[^1].Sequence);
        Assert.Equal((byte)0x22, packets[^1].Payload[15]);
        Assert.True(session.IsEnded);
    }

    [Fact]
    public void FlushesPartialPcmAndAmbeBeforeCompletingSuperframe()
    {
        var packets = new List<byte[]>();
        using var session = new DmrTxCallSession(
            sourceId: 1,
            destinationId: 2,
            slot: 0,
            streamId: 3,
            vocoder: new FakeVocoderSession(),
            send: (payload, _, _) => packets.Add(payload.ToArray()));

        session.Start();
        Assert.Equal(0, session.Process(new short[200]));
        session.End();

        Assert.Equal(8, packets.Count);
        Assert.Equal((byte)0x10, packets[1][15]);
        Assert.Equal((byte)0x22, packets[^1][15]);
    }

    [Fact]
    public void RequiresAnExplicitStartBeforeProcessingAudio()
    {
        using var session = new DmrTxCallSession(
            sourceId: 1,
            destinationId: 2,
            slot: 1,
            streamId: 3,
            vocoder: new FakeVocoderSession(),
            send: (_, _, _) => { });

        Assert.Throws<InvalidOperationException>(() => session.Process(new short[160]));
    }

    [Fact]
    public void ReservesTheRtpCallEndSequenceWhenAdvancing()
    {
        var sequence = new DmrTxPacketSequence(packetSequence: 0xFFFE, frameSequence: 4);

        sequence.Advance();

        Assert.Equal((ushort)0, sequence.PacketSequence);
        Assert.Equal((byte)5, sequence.FrameSequence);
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
