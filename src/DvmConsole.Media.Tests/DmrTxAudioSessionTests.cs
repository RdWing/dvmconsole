using DvmConsole.Media;
using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Media.Tests;

[Collection("DMR wire codec")]
public sealed class DmrTxAudioSessionTests
{
    [Fact]
    public void AggregatesThreeEncodedCodewordsIntoOneVoicePacket()
    {
        var packets = new List<(byte[] Payload, ushort Sequence, uint Stream)>();
        using var session = new DmrTxAudioSession(
            sourceId: 0x010203,
            destinationId: 0xA0B0C0,
            slot: 1,
            streamId: 77,
            vocoder: new FakeVocoderSession(),
            send: (payload, sequence, stream) => packets.Add((payload.ToArray(), sequence, stream)));

        Assert.Equal(0, session.Process(new short[320]));
        Assert.Equal(1, session.Process(new short[160]));

        Assert.Equal(3, session.CodewordsEncoded);
        Assert.Equal(1, session.PacketsSent);
        var packet = Assert.Single(packets);
        Assert.Equal((ushort)0, packet.Sequence);
        Assert.Equal((uint)77, packet.Stream);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, packet.Payload[5..8]);
        Assert.Equal(new byte[] { 0xA0, 0xB0, 0xC0 }, packet.Payload[8..11]);
        Assert.Equal((byte)0x90, packet.Payload[15]);
        Assert.Equal(27, DmrVoicePacketCodec.ExtractAmbe(packet.Payload).Length);
    }

    [Fact]
    public void AdvancesPacketAndEmbeddedSequencesAcrossVoicePackets()
    {
        var packets = new List<(byte[] Payload, ushort Sequence, uint Stream)>();
        using var session = new DmrTxAudioSession(
            sourceId: 1,
            destinationId: 2,
            slot: 0,
            streamId: 3,
            vocoder: new FakeVocoderSession(),
            send: (payload, sequence, stream) => packets.Add((payload.ToArray(), sequence, stream)),
            packetSequence: 41,
            frameSequence: 9);

        session.Process(new short[960]);

        Assert.Equal(2, packets.Count);
        Assert.Equal((ushort)41, packets[0].Sequence);
        Assert.Equal((ushort)42, packets[1].Sequence);
        Assert.Equal((byte)0x10, packets[0].Payload[15]);
        Assert.Equal((byte)0x01, packets[1].Payload[15]);
        Assert.Equal((byte)9, packets[0].Payload[4]);
        Assert.Equal((byte)10, packets[1].Payload[4]);
    }

    private sealed class FakeVocoderSession : IVocoderSession
    {
        private byte nextCodeword;

        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword)
        {
            Assert.Equal(VocoderFrameSizes.PcmSamplesPerFrame, samples.Length);
            codeword.Fill(nextCodeword++);
            return codeword.Length;
        }

        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples) => 0;
        public void Dispose()
        {
        }
    }
}
