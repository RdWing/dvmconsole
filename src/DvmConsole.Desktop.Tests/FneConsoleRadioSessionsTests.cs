// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Configuration;
using DvmConsole.FneClient;
using DvmConsole.FneIntegration;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class FneConsoleRadioSessionsTests
{
    [Fact]
    public async Task RadioConstructionUsesApplicationStateInTopologyOrderWithoutViews()
    {
        var configuration = Configuration();
        var state = ConsoleSessionState.Create(configuration);
        state.Channels[state.Topology.Channels[1].Id].Operator.SetHasCallPriority(true);
        await using var radios = await FneConsoleRadioSessions.CreateAsync(state,
            configuration.Systems.Select(FneConnectionOptions.FromConfiguration),
            channel => new ChannelConfigurationAccess(channel.Runtime.Definition));
        var endpoint = Assert.IsAssignableFrom<IRadioTrafficEndpoint>(Assert.Single(radios.Sessions).Value);
        Assert.Equal(state.Topology.Channels.Select(channel => channel.Id), endpoint.ChannelIds);
        Assert.True(endpoint.ChannelDescriptors.Last().AllowsTransmitDuringReceive);
        Assert.False(endpoint.IsConnected);
    }

    [Fact]
    public async Task MismatchedSystemBindingsAreRejectedBeforeOpeningRadios()
    {
        var configuration = Configuration();
        var state = ConsoleSessionState.Create(configuration);
        var options = FneConnectionOptions.FromConfiguration(configuration.Systems[0]) with { Name = "Other" };
        await Assert.ThrowsAsync<ArgumentException>(() => FneConsoleRadioSessions.CreateAsync(
            state, [options], channel => new ChannelConfigurationAccess(channel.Runtime.Definition)).AsTask());
    }

    private static ConsoleConfiguration Configuration() => new()
    {
        Systems = [new SystemConfiguration { Name = "Test", Identity = "Console", Address = "127.0.0.1",
            Port = 62031, PeerId = 1, Rid = "1001" }],
        Zones = [new ZoneConfiguration { Name = "Dispatch", Channels =
            [new ChannelConfiguration { Name = "First", System = "Test", Mode = "p25", Tgid = "100" },
             new ChannelConfiguration { Name = "Second", System = "Test", Mode = "p25", Tgid = "101" }] }]
    };
}
