using DvmConsole.Media;
using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Media.Tests;

public sealed class NxdnVoicePacketCodecTests
{
    [Fact]
    public void VoicePacketRoundTripsFourAmbeCodewords()
    {
        byte[] expected = Enumerable.Range(0, NxdnVoicePacketCodec.AmbeBytes)
            .Select(value => (byte)(0x40 + value))
            .ToArray();

        byte[] packet = NxdnVoicePacketCodec.CreateVoicePacket(1001, 2002, true, 7, expected);
        byte[] actual = new byte[NxdnVoicePacketCodec.AmbeBytes];

        Assert.True(NxdnVoicePacketCodec.TryExtractAmbe(packet, actual, out int count));
        Assert.Equal(4, count);
        Assert.Equal(expected, actual);
        Assert.Equal("NXDD", System.Text.Encoding.ASCII.GetString(packet, 0, 4));
        Assert.Equal(NxdnVoicePacketCodec.VoiceCallMessageType, packet[4]);
    }

    [Theory]
    [InlineData(NxdnVoicePacketCodec.VoiceCallMessageType)]
    [InlineData(NxdnVoicePacketCodec.TransmitReleaseMessageType)]
    public void CallControlRoundTripsFacchMetadata(byte messageType)
    {
        byte[] packet = NxdnVoicePacketCodec.CreateCallControlPacket(
            1001, 2002, false, messageType, 3, cipherType: 2, keyId: 5);

        Assert.True(NxdnVoicePacketCodec.TryExtractCallMetadata(packet, out var metadata));
        Assert.Equal(messageType, metadata.MessageType);
        Assert.Equal((ushort)1001, metadata.SourceId);
        Assert.Equal((ushort)2002, metadata.DestinationId);
        Assert.False(metadata.Group);
        Assert.Equal(messageType == NxdnVoicePacketCodec.VoiceCallMessageType ? (byte)2 : (byte)0, metadata.CipherType);
        Assert.Equal(messageType == NxdnVoicePacketCodec.VoiceCallMessageType ? (byte)5 : (byte)0, metadata.KeyId);
    }

    [Fact]
    public void CallSessionSendsHeaderVoiceAndReleaseInOrder()
    {
        var sent = new List<(byte[] Payload, ushort Sequence)>();
        using var call = new NxdnTxCallSession(
            1001,
            2002,
            group: true,
            streamId: 99,
            new FakeVocoderSession(),
            (payload, sequence, _) => sent.Add((payload.ToArray(), sequence)));

        call.Start();
        call.Process(new short[VocoderFrameSizes.PcmSamplesPerFrame * NxdnVoicePacketCodec.CodewordsPerFrame]);
        call.End();

        Assert.Equal(4, sent.Count);
        Assert.Equal(NxdnVoicePacketCodec.VoiceCallMessageType, sent[0].Payload[4]);
        Assert.Equal(NxdnVoicePacketCodec.VoiceCallMessageType, sent[1].Payload[4]);
        Assert.Equal(NxdnVoicePacketCodec.VoiceCallMessageType, sent[2].Payload[4]);
        Assert.Equal(NxdnVoicePacketCodec.TransmitReleaseMessageType, sent[3].Payload[4]);
        Assert.Equal([0, 1, 2, 3], sent.Select(item => (int)item.Sequence));
    }

    private sealed class FakeVocoderSession : IVocoderSession
    {
        private byte value;
        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword)
        {
            codeword.Fill(++value);
            return 0;
        }
        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples) => 0;
        public int FlushEncode(Span<byte> codeword)
        {
            codeword.Fill(++value);
            return codeword.Length;
        }
        public void Dispose() { }
    }
}
