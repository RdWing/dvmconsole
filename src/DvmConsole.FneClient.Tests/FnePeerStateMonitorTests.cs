// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.FneClient;
using fnecore;
using Xunit;

namespace DvmConsole.FneClient.Tests;

public sealed class FnePeerStateMonitorTests
{
    [Theory]
    [InlineData(ConnectionState.WAITING_LOGIN, FneConnectionState.WaitingForLogin, "Waiting for FNE login acknowledgement")]
    [InlineData(ConnectionState.WAITING_AUTHORISATION, FneConnectionState.Authenticating, "FNE login accepted; waiting for authorization")]
    [InlineData(ConnectionState.WAITING_CONFIG, FneConnectionState.Configuring, "FNE authorization accepted; sending configuration")]
    [InlineData(ConnectionState.RUNNING, FneConnectionState.Connected, "FNE peer connected")]
    public void InterpretsUpstreamPeerState(
        ConnectionState state,
        FneConnectionState expectedState,
        string expectedMessage)
    {
        FneMonitoredState monitored = FnePeerStateMonitor.Interpret(state);

        Assert.Equal(expectedState, monitored.State);
        Assert.Equal(expectedMessage, monitored.Message);
    }

    [Fact]
    public void RepublishesWhenPublishedStateWasTemporarilyOverridden()
    {
        Assert.True(FnePeerStateMonitor.ShouldPublish(
            FneConnectionState.Connected,
            FneConnectionState.Connected,
            FneConnectionState.Faulted));
        Assert.False(FnePeerStateMonitor.ShouldPublish(
            FneConnectionState.Connected,
            FneConnectionState.Connected,
            FneConnectionState.Connected));
    }
}
