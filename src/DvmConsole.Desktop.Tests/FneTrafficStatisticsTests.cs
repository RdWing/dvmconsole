using DvmConsole.FneClient;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class FneTrafficStatisticsTests
{
    [Fact]
    public void TracksStableConnectionTotalsAndResetsACompletedStreamSummary()
    {
        var statistics = new FneTrafficStatistics();

        statistics.ObserveReceive(Traffic(1, payloadBytes: 1_500));
        statistics.ObserveReceive(Traffic(2, payloadBytes: 500, terminator: true));
        statistics.ObserveSend(2_500_000);

        Assert.Equal("Media this connection · RX 2.0 KB · TX 2.5 MB", statistics.TotalsText);
        Assert.Contains("Last RX stream", statistics.StreamText);
        Assert.Contains("2 packets / 2.0 KB", statistics.StreamText);
        Assert.Contains("ended", statistics.StreamText);

        statistics.Reset();

        Assert.Equal("Media this connection · RX 0 B · TX 0 B", statistics.TotalsText);
        Assert.Equal("No RX media stream in this connection session.", statistics.StreamText);
    }

    [Theory]
    [InlineData(999, "999 B")]
    [InlineData(1_000, "1.0 KB")]
    [InlineData(1_000_000, "1.0 MB")]
    [InlineData(1_000_000_000, "1.0 GB")]
    public void FormatsConnectionTrafficUsingReadableUnits(long bytes, string expected)
        => Assert.Equal(expected, FneTrafficStatistics.FormatBytes(bytes));

    private static FneTrafficFrame Traffic(
        ushort sequence,
        int payloadBytes,
        bool terminator = false)
        => new(
            FneTrafficProtocol.P25,
            peerId: 1,
            sourceId: 100,
            destinationId: 200,
            slot: null,
            callType: "GROUP",
            frameType: terminator ? "TERMINATOR" : "VOICE",
            subtype: terminator ? "TDU" : "LDU1",
            packetSequence: sequence,
            streamId: 42,
            payload: new byte[payloadBytes]);
}
