// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

internal static class DesktopConsoleSnapshotProjector
{
    public static ConsoleTopologySnapshot BuildTopology(MainWindowViewModel owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.PreparedTopology is { } prepared)
            return prepared;
        return BuildTopology(owner.Systems, owner.Zones, owner.ConfigurationReference);
    }

    public static ConsoleTopologySnapshot BuildTopology(IReadOnlyList<SystemViewModel> systemViews,
        IReadOnlyList<ZoneViewModel> zoneViews, ConfigurationReference? reference)
    {
        // Directly constructed demo/test facades have no validated document.
        // Configured sessions use Application's topology prepared before services.
        SystemDescriptor[] systems = systemViews
            .Select(system => new SystemDescriptor(
                SystemId.FromName(system.Name),
                system.Name,
                ResolveProtocol(system.Channels)))
            .ToArray();
        ZoneDescriptor[] zones = zoneViews
            .Select(zone => new ZoneDescriptor(
                ZoneId.FromName(zone.Name),
                zone.Name,
                zone.Channels.Select(channel => new ChannelId(channel.SessionId)).ToArray()))
            .ToArray();
        ChannelDescriptor[] channelDescriptors = zoneViews
            .SelectMany(zone => zone.Channels.Select(channel => (Zone: zone, Channel: channel)))
            .GroupBy(pair => new ChannelId(pair.Channel.SessionId))
            .Select(group => ProjectDescriptor(group.Key, group.First().Zone, group.First().Channel))
            .ToArray();
        var described = channelDescriptors.Select(channel => channel.Id).ToHashSet();
        ChannelViewModel[] unassigned = systemViews.SelectMany(system => system.Channels)
            .Where(channel => described.Add(channel.Id)).ToArray();
        if (unassigned.Length > 0)
        {
            // Existing shells may have channels outside their visible zone collection.
            // Represent their routing metadata without manufacturing bound zone models.
            var existingZones = zones.Select(zone => zone.Id).ToHashSet();
            string zoneName = "Unassigned";
            int suffix = 1;
            while (existingZones.Contains(ZoneId.FromName(zoneName))) zoneName = $"Unassigned {++suffix}";
            var zoneId = ZoneId.FromName(zoneName);
            zones = [.. zones, new(zoneId, zoneName, unassigned.Select(channel => channel.Id).ToArray())];
            channelDescriptors = [.. channelDescriptors, .. unassigned.Select(channel => ProjectDescriptor(channel.Id,
                zoneId, channel))];
        }
        return new ConsoleTopologySnapshot(reference, systems, zones, channelDescriptors);
    }

    private static ChannelDescriptor ProjectDescriptor(
        ChannelId id,
        ZoneViewModel zone,
        ChannelViewModel channel)
        => ProjectDescriptor(id, ZoneId.FromName(zone.Name), channel);

    private static ChannelDescriptor ProjectDescriptor(ChannelId id, ZoneId zone, ChannelViewModel channel)
        => new(
            id,
            SystemId.FromName(channel.Definition.SystemName),
            zone,
            channel.Name,
            channel.Definition.DestinationId,
            channel.Definition.Protocol.ToString(),
            channel.Definition.Slot,
            channel.Definition.RxOnly,
            channel.HasCallPriority,
            channel.ConfiguredCardSize);

    private static string ResolveProtocol(IEnumerable<ChannelViewModel> channels)
        => channels.Select(channel => channel.Definition.Protocol.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? "Unknown";
}
