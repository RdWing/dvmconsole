using DvmConsole.FneClient;
using fnecore;
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
}
