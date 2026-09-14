// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.FneClient;
using DvmConsole.FneIntegration;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class FneConnectionStateTests
{
    [Fact]
    public void EveryTransportStateHasAnExplicitPortableMapping()
    {
        foreach (FneConnectionState state in Enum.GetValues<FneConnectionState>())
        {
            RadioConnectionState portable = FneConnectionStateMapper.ToApplicationState(state);
            Assert.Equal(state.ToString(), portable.ToString());
            Assert.Equal(state, FneConnectionStateMapper.ToTransportState(portable));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FneConnectionStateMapper.ToTransportState((RadioConnectionState)int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FneConnectionStateMapper.ToApplicationState((FneConnectionState)int.MaxValue));
    }

    [Fact]
    public async Task ReadingConnectionFactsDoesNotStartTheTransport()
    {
        await using var radio = new FneRadioSessionAdapter(
            new("Offline", "test", "127.0.0.1", 62031, 1234, null, false, null), () => []);
        RadioConnectionSnapshot first = radio.ConnectionState;
        Assert.Equal(SystemId.FromName("Offline"), first.SystemId);
        Assert.Equal("Offline", first.Name);
        Assert.Equal(RadioConnectionState.Disconnected, first.State);
        Assert.Equal(radio.Status.Message, first.Message);
        Assert.Equal(radio.Status.ChangedAt, first.ChangedAt);
        Assert.Equal(first, radio.ConnectionState);
        Assert.False(radio.IsConnectionActive);
        Assert.False(radio.IsConnected);
    }
}
