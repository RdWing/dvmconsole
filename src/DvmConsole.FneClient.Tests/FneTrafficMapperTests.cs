using DvmConsole.FneClient;
using fnecore;
using fnecore.DMR;
using Xunit;

namespace DvmConsole.FneClient.Tests;

public sealed class FneTrafficMapperTests
{
    [Theory]
    [InlineData(FneTrafficProtocol.Dmr, Constants.NET_PROTOCOL_SUBFUNC_DMR)]
    [InlineData(FneTrafficProtocol.P25, Constants.NET_PROTOCOL_SUBFUNC_P25)]
    [InlineData(FneTrafficProtocol.Nxdn, Constants.NET_PROTOCOL_SUBFUNC_NXDN)]
    [InlineData(FneTrafficProtocol.Analog, Constants.NET_PROTOCOL_SUBFUNC_ANALOG)]
    public void MapsProtocolToExactFneOpcode(FneTrafficProtocol protocol, byte expectedSubFunction)
    {
        Tuple<byte, byte> opcode = FneTrafficMapper.ToOpcode(protocol);

        Assert.Equal(Constants.NET_FUNC_PROTOCOL, opcode.Item1);
        Assert.Equal(expectedSubFunction, opcode.Item2);
    }

    [Fact]
    public void RejectsUnknownProtocol()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => FneTrafficMapper.ToOpcode((FneTrafficProtocol)byte.MaxValue));

    [Theory]
    [InlineData((byte)0xE1, "VOICE_LC_HEADER")]
    [InlineData((byte)0xE2, "TERMINATOR_WITH_LC")]
    public void NormalizesDmrDataTypeWithoutSlotOrPrivateCallFlags(
        byte control,
        string expectedSubtype)
    {
        var payload = new byte[55];
        payload[15] = control;
        var received = new DMRDataReceivedEvent(
            peerId: 1,
            srcId: 2,
            dstId: 3,
            slot: 1,
            callType: CallType.PRIVATE,
            frameType: FrameType.DATA_SYNC,
            dataType: (DMRDataType)control,
            n: 0,
            pktSeq: 4,
            streamId: 5,
            data: payload);

        FneTrafficFrame traffic = FneTrafficMapper.FromDmr(received, 6, 7);

        Assert.Equal(expectedSubtype, traffic.Subtype);
    }
}
