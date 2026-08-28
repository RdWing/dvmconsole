using DvmConsole.Core.Diagnostics;
using DvmConsole.FneClient;
using fnecore;
using Xunit;

namespace DvmConsole.FneClient.Tests;

public sealed class FneLogInterpreterTests
{
    [Theory]
    [InlineData(LogLevel.DEBUG, DebugLogSeverity.Debug)]
    [InlineData(LogLevel.WARNING, DebugLogSeverity.Warning)]
    [InlineData(LogLevel.ERROR, DebugLogSeverity.Error)]
    [InlineData(LogLevel.FATAL, DebugLogSeverity.Fatal)]
    [InlineData(LogLevel.INFO, DebugLogSeverity.Info)]
    public void MapsUpstreamLogSeverity(LogLevel level, DebugLogSeverity expected)
        => Assert.Equal(expected, FneLogInterpreter.MapSeverity(level));

    [Theory]
    [InlineData("Sending login request", FneConnectionState.WaitingForLogin, "FNE login request sent")]
    [InlineData("login ACK received", FneConnectionState.Authenticating, "FNE login acknowledgement received")]
    [InlineData("master NAK", FneConnectionState.Faulted, "FNE master rejected the connection")]
    [InlineData("SOCKET ERROR: reset", FneConnectionState.Faulted, "FNE socket error or connection loss")]
    [InlineData("Not connected or lost connection", FneConnectionState.Faulted, "FNE socket error or connection loss")]
    public void InterpretsLifecycleSignals(
        string message,
        FneConnectionState expectedState,
        string expectedMessage)
    {
        FneLogStatusUpdate update = Assert.IsType<FneLogStatusUpdate>(
            FneLogInterpreter.InterpretStatus(message, FneConnectionState.Configuring));

        Assert.Equal(expectedState, update.State);
        Assert.Equal(expectedMessage, update.Message);
    }

    [Theory]
    [InlineData("Network Sent packet", "FNE traffic packet sent")]
    [InlineData("Network Received packet", "FNE traffic packet received")]
    public void TrafficSignalsRetainCurrentState(string message, string expectedMessage)
    {
        FneLogStatusUpdate update = Assert.IsType<FneLogStatusUpdate>(
            FneLogInterpreter.InterpretStatus(message, FneConnectionState.Connected));

        Assert.Equal(FneConnectionState.Connected, update.State);
        Assert.Equal(expectedMessage, update.Message);
    }

    [Fact]
    public void ProtocolDiagnosticsDoNotChangeConnectionState()
        => Assert.Null(FneLogInterpreter.InterpretStatus(
            "Unknown master opcode 7F / 00",
            FneConnectionState.Connected));

    [Theory]
    [InlineData("(DVMCONSOLE) RPTPING sent to MASTER 127.0.0.1:62031")]
    [InlineData("(DVMCONSOLE) PEER 1001 MSTPONG received, pongs since connected 9")]
    public void RecognizesRoutineHealthyKeepalives(string message)
        => Assert.True(FneLogInterpreter.IsRoutineHealthyKeepalive(message));

    [Fact]
    public void DoesNotClassifyConnectionFailuresAsRoutineKeepalives()
        => Assert.False(FneLogInterpreter.IsRoutineHealthyKeepalive(
            "RPTPING failed because the socket disconnected"));

    [Theory]
    [InlineData("PEER 123 login ACK received with ID 456", true)]
    [InlineData("Sending login request to MASTER", false)]
    public void RecognizesLoginAcknowledgements(string message, bool expected)
        => Assert.Equal(expected, FneLogInterpreter.IsLoginAcknowledgement(message));
}
