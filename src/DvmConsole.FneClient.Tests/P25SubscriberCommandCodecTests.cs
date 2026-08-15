using DvmConsole.FneClient;
using fnecore.P25;
using Xunit;

namespace DvmConsole.FneClient.Tests;

public sealed class P25SubscriberCommandCodecTests
{
    [Fact]
    public void BuildsCallAlertWithConsoleAndTargetIds()
    {
        P25SubscriberCommandMessage message = P25SubscriberCommandCodec.Build(
            P25SubscriberCommand.CallAlert,
            890,
            12345);

        Assert.Equal(P25Defines.TSBK_IOSP_CALL_ALRT, message.LinkControlOpcode);
        Assert.Equal(
            [0x00, 0x00, 0x00, 0x30, 0x39, 0x00, 0x03, 0x7A],
            message.Tsbk[2..10]);
    }

    [Theory]
    [InlineData(P25SubscriberCommand.RadioCheck, ExtendedFunction.CHECK, 890u)]
    [InlineData(P25SubscriberCommand.Inhibit, ExtendedFunction.INHIBIT, P25Defines.WUID_FNE)]
    [InlineData(P25SubscriberCommand.Uninhibit, ExtendedFunction.UNINHIBIT, P25Defines.WUID_FNE)]
    public void BuildsExtendedFunctionCommands(
        P25SubscriberCommand command,
        ExtendedFunction function,
        uint expectedArgument)
    {
        P25SubscriberCommandMessage message = P25SubscriberCommandCodec.Build(command, 890, 12345);

        Assert.Equal(P25Defines.TSBK_IOSP_EXT_FNCT, message.LinkControlOpcode);
        Assert.Equal((byte)((ushort)function >> 8), message.Tsbk[2]);
        Assert.Equal((byte)(ushort)function, message.Tsbk[3]);
        Assert.Equal((byte)(expectedArgument >> 16), message.Tsbk[4]);
        Assert.Equal((byte)(expectedArgument >> 8), message.Tsbk[5]);
        Assert.Equal((byte)expectedArgument, message.Tsbk[6]);
        Assert.Equal([0x00, 0x30, 0x39], message.Tsbk[7..10]);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x1000000u)]
    public void RejectsInvalidP25Destination(uint destinationId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => P25SubscriberCommandCodec.Build(
            P25SubscriberCommand.CallAlert,
            890,
            destinationId));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x1000000u)]
    public void RejectsInvalidP25Source(uint sourceId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => P25SubscriberCommandCodec.Build(
            P25SubscriberCommand.RadioCheck,
            sourceId,
            12345));
    }

    [Theory]
    [InlineData("1", 1u)]
    [InlineData(" 16777215 ", 16777215u)]
    public void ParsesValidP25SubscriberIds(string text, uint expected)
    {
        Assert.True(P25SubscriberCommandCodec.TryParseSubscriberId(text, out uint actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("16777216")]
    [InlineData("1.5")]
    public void RejectsInvalidP25SubscriberIdText(string? text)
    {
        Assert.False(P25SubscriberCommandCodec.TryParseSubscriberId(text, out _));
    }
}
