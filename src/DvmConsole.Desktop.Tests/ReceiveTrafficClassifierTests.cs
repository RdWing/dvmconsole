using DvmConsole.Application;
using DvmConsole.FneClient;
using DvmConsole.Media;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ReceiveTrafficClassifierTests
{
    [Fact]
    public void P25GrantDemandRemainsATerminatorControlRequest()
    {
        byte[] payload = P25DfsiFrameCodec.CreateTduPayload(10, 20, grantDemand: true);
        FneTrafficFrame traffic = Create(
            FneTrafficProtocol.P25,
            "TERMINATOR",
            "TDU",
            payload,
            packetSequence: P25DfsiFrameCodec.RtpCallEndSequence);

        Assert.True(ReceiveTrafficClassifier.IsTerminator(traffic));
        Assert.False(ReceiveTrafficClassifier.IsDefinitiveStart(traffic));
        Assert.Equal(
            ReceiveJitterPacketKind.Terminator,
            ReceiveTrafficClassifier.GetJitterPacketKind(traffic));
    }

    [Fact]
    public void InitialP25Ldu1StartsTheCallWithoutLeavingVoiceCadence()
    {
        byte[] payload = P25DfsiFrameCodec.CreateLdu1Payload(
            10,
            20,
            new byte[P25DfsiFrameCodec.ImbeBytes]);
        FneTrafficFrame traffic = Create(
            FneTrafficProtocol.P25,
            "VOICE",
            "LDU1",
            payload,
            packetSequence: 0);

        Assert.True(ReceiveTrafficClassifier.IsDefinitiveStart(traffic));
        Assert.True(ReceiveTrafficClassifier.CarriesEncodedVoicePayload(traffic));
        Assert.Equal(
            ReceiveJitterPacketKind.Voice,
            ReceiveTrafficClassifier.GetJitterPacketKind(traffic));
    }

    [Theory]
    [InlineData("LDU1", 2)]
    [InlineData("LDU2", 1)]
    public void LaterP25VoiceSupportsContinuationAndLateEntry(
        string subtype,
        ushort packetSequence)
    {
        byte[] payload = subtype == "LDU1"
            ? P25DfsiFrameCodec.CreateLdu1Payload(
                10,
                20,
                new byte[P25DfsiFrameCodec.ImbeBytes])
            : P25DfsiFrameCodec.CreateLdu2Payload(
                10,
                20,
                new byte[P25DfsiFrameCodec.ImbeBytes]);
        FneTrafficFrame traffic = Create(
            FneTrafficProtocol.P25,
            "VOICE",
            subtype,
            payload,
            packetSequence);

        Assert.False(ReceiveTrafficClassifier.IsDefinitiveStart(traffic));
        Assert.True(ReceiveTrafficClassifier.CarriesEncodedVoicePayload(traffic));
        Assert.Equal(
            ReceiveJitterPacketKind.Voice,
            ReceiveTrafficClassifier.GetJitterPacketKind(traffic));
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
        byte[] payload,
        ushort packetSequence = 0)
        => new(
            protocol,
            peerId: 1,
            sourceId: 10,
            destinationId: 20,
            slot: null,
            callType: "GROUP",
            frameType,
            subtype,
            packetSequence,
            streamId: 30,
            payload);
}
