using DvmConsole.FneClient;
using DvmConsole.Media;
using Xunit;

namespace DvmConsole.Media.Tests;

public sealed class AnalogVoicePacketCodecTests
{
    [Fact]
    public void ExtractsMuLawPcmAndPreservesTheDvmhostWireLayout()
    {
        short[] source = new short[AnalogVoicePacketCodec.SamplesPerPacket];
        source[0] = -1234;
        source[^1] = 2345;
        byte[] packet = AnalogVoicePacketCodec.CreatePacket(
            AnalogAudioFrameType.Voice,
            sourceId: 0x010203,
            destinationId: 0x0A0B0C,
            source);

        short[] samples = AnalogVoicePacketCodec.ExtractPcm(packet);

        Assert.Equal((int)fnecore.Constants.AnalogPacketLength, packet.Length);
        Assert.Equal("ANOD", System.Text.Encoding.ASCII.GetString(packet, 0, 4));
        Assert.Equal(0x01, packet[AnalogVoicePacketCodec.SourceIdOffset]);
        Assert.Equal(0x0C, packet[AnalogVoicePacketCodec.DestinationIdOffset + 2]);
        Assert.Equal(AnalogVoicePacketCodec.EncodeMuLaw(source[0]), packet[AnalogVoicePacketCodec.AudioOffset]);
        Assert.Equal(AnalogVoicePacketCodec.EncodeMuLaw(source[^1]), packet[AnalogVoicePacketCodec.AudioOffset + 159]);
        Assert.Equal(AnalogVoicePacketCodec.SamplesPerPacket, samples.Length);
        Assert.Equal(AnalogVoicePacketCodec.DecodeMuLaw(AnalogVoicePacketCodec.EncodeMuLaw(source[0])), samples[0]);
        Assert.Equal(AnalogVoicePacketCodec.DecodeMuLaw(AnalogVoicePacketCodec.EncodeMuLaw(source[^1])), samples[^1]);
        Assert.All(packet.AsSpan(AnalogVoicePacketCodec.AudioOffset + AnalogVoicePacketCodec.EncodedAudioBytes, AnalogVoicePacketCodec.AudioBytes - AnalogVoicePacketCodec.EncodedAudioBytes).ToArray(), value => Assert.Equal(0, value));
        Assert.All(packet.AsSpan(AnalogVoicePacketCodec.HeaderBytes + AnalogVoicePacketCodec.AudioBytes, AnalogVoicePacketCodec.TrailerBytes).ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public void AnalogSelectorRejectsTerminatorAndWrongDestination()
    {
        var selector = new AnalogTrafficSelector(100);
        FneTrafficFrame voice = CreateTraffic(100, "VOICE");

        Assert.True(selector.Matches(voice));
        Assert.False(selector.Matches(CreateTraffic(101, "VOICE")));
        Assert.False(selector.Matches(CreateTraffic(100, "TERMINATOR")));
    }

    private static FneTrafficFrame CreateTraffic(uint destinationId, string frameType)
    {
        return new FneTrafficFrame(
            FneTrafficProtocol.Analog,
            1,
            2,
            destinationId,
            null,
            "GROUP",
            frameType,
            "VOICE",
            1,
            99,
            new byte[AnalogVoicePacketCodec.PacketBytes]);
    }
}
