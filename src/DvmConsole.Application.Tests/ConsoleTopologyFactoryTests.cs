// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Configuration;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ConsoleTopologyFactoryTests
{
    [Fact]
    public void TopologyDoesNotRetainMutableConfigurationCollectionsOrFields()
    {
        ConsoleConfiguration configuration = CreateConfiguration();
        var reference = new ConfigurationReference(ConfigurationId.New(), ConfigurationRevision.New());
        ConsoleTopologySnapshot topology = ConsoleTopologyFactory.Create(configuration, reference, ["test"]);
        configuration.Systems[0].Name = "Changed";
        configuration.Zones[0].Channels[0].Name = "Changed";
        configuration.Zones[0].Channels[0].CardSize = "large";
        configuration.Zones.Clear();
        configuration.Systems.Clear();
        Assert.Equal(reference, topology.Configuration);
        Assert.Equal("Test", Assert.Single(topology.Systems).Name);
        Assert.Equal("Dispatch", Assert.Single(topology.Zones).Name);
        ChannelDescriptor channel = Assert.Single(topology.Channels);
        Assert.Equal("Operations", channel.Name);
        Assert.Equal("normal", channel.CardSize);
        Assert.Equal((byte)1, channel.Slot);
        Assert.True(channel.AllowsTransmitDuringReceive);
        Assert.Equal(channel.Id, Assert.Single(topology.Zones[0].Channels));
    }

    [Fact]
    public void InvalidConfigurationIsRejectedBeforeTopologyIsPublished()
    {
        ConsoleConfiguration configuration = CreateConfiguration();
        configuration.Zones[0].Channels[0].Tgid = "invalid";
        Assert.Throws<InvalidDataException>(() => ConsoleTopologyFactory.Create(configuration));
    }

    [Fact]
    public void RebuildingTheSameConfigurationPreservesChannelIds()
    {
        ConsoleTopologySnapshot first = ConsoleTopologyFactory.Create(CreateConfiguration());
        ConsoleTopologySnapshot second = ConsoleTopologyFactory.Create(CreateConfiguration());
        Assert.Equal(first.Channels[0].Id, second.Channels[0].Id);
    }

    [Fact]
    public void PreparedStateOwnsIndependentRuntimeAcrossConfigurationReplacements()
    {
        ConsoleConfiguration configuration = CreateConfiguration();
        ConsoleSessionState first = ConsoleSessionState.Create(configuration, callPrioritySystemNames: ["Test"]);
        ConsoleSessionState second = ConsoleSessionState.Create(configuration);
        ConsoleChannelState channel = Assert.Single(first.Channels).Value;
        channel.Operator.SetRecordingEnabled(true);
        channel.Operator.SetAudioEnabled(true);
        configuration.Zones[0].Channels[0].Name = "Edited";

        Assert.Equal("Operations", channel.Runtime.Definition.Name);
        Assert.True(channel.Operator.Snapshot.HasCallPriority);
        Assert.Equal(Assert.Single(first.Topology.Channels).Id, channel.Id);
        Assert.False(second.Channels[channel.Id].Operator.Snapshot.RecordingEnabled);
        Assert.False(second.Channels[channel.Id].Operator.Snapshot.AudioEnabled);
        Assert.NotSame(channel.Runtime, second.Channels[channel.Id].Runtime);
    }

    private static ConsoleConfiguration CreateConfiguration() => new()
    {
        Systems = [new SystemConfiguration { Name = "Test", Identity = "Console", Address = "127.0.0.1", Port = 62031, PeerId = 1, Rid = "1001" }],
        Zones = [new ZoneConfiguration { Name = "Dispatch", Channels =
            [new ChannelConfiguration { Name = "Operations", System = "Test", Mode = "dmr", Tgid = "100", Slot = 2 }] }]
    };
}
