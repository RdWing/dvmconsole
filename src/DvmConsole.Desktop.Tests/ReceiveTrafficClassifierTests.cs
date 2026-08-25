using DvmConsole.FneClient;
using DvmConsole.Media;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ReceiveTrafficClassifierTests
{
    [Fact]
    public void P25GrantDemandIsMetadataInsteadOfATerminator()
    {
        byte[] payload = P25DfsiFrameCodec.CreateTduPayload(10, 20, grantDemand: true);
        FneTrafficFrame traffic = Create(
            FneTrafficProtocol.P25,
            "TERMINATOR",
            "TDU",
            payload);

        Assert.False(ReceiveTrafficClassifier.IsTerminator(traffic));
        Assert.True(ReceiveTrafficClassifier.IsP25GrantDemand(traffic));
        Assert.Equal(ReceiveJitterPacketKind.Metadata, ReceiveTrafficClassifier.GetJitterPacketKind(traffic));
    }

    [Fact]
    public void NxdnVoiceCallControlDoesNotConsumeVoiceCadence()
    {
        byte[] payload = NxdnVoicePacketCodec.CreateCallControlPacket(
            sourceId: 10,
            destinationId: 20,
            group: true,
            messageType: NxdnVoicePacketCodec.VoiceCallMessageType,
            frameSequence: 0);
        FneTrafficFrame traffic = Create(
            FneTrafficProtocol.Nxdn,
            "VOICE",
            "MESSAGE_TYPE_VCALL",
            payload);

        Assert.True(ReceiveTrafficClassifier.CarriesVoicePayload(traffic));
        Assert.False(ReceiveTrafficClassifier.CarriesEncodedVoicePayload(traffic));
        Assert.Equal(ReceiveJitterPacketKind.Metadata, ReceiveTrafficClassifier.GetJitterPacketKind(traffic));
    }

    private static FneTrafficFrame Create(
        FneTrafficProtocol protocol,
        string frameType,
        string subtype,
        byte[] payload)
        => new(
            protocol,
            peerId: 1,
            sourceId: 10,
            destinationId: 20,
            slot: null,
            callType: "GROUP",
            frameType,
            subtype,
            packetSequence: 0,
            streamId: 30,
            payload);
}
