// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Runtime;
using DvmConsole.Operations;

namespace DvmConsole.Application;

/// <summary>Freezes validated session topology before any host services are constructed.</summary>
public static class ConsoleTopologyFactory
{
    public static ConsoleTopologySnapshot Create(
        ConsoleConfiguration configuration,
        ConfigurationReference? reference = null,
        IReadOnlyCollection<string>? callPrioritySystemNames = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        IReadOnlyList<string> errors = ConfigurationValidator.Validate(configuration);
        if (errors.Count != 0)
            throw new InvalidDataException("Invalid console configuration: " + string.Join("; ", errors));

        var channels = new Dictionary<ChannelId, ChannelDescriptor>();
        var zones = ImmutableArray.CreateBuilder<ZoneDescriptor>();
        foreach (ZoneConfiguration zone in configuration.Zones)
        {
            ZoneId zoneId = ZoneId.FromName(zone.Name);
            var channelIds = ImmutableArray.CreateBuilder<ChannelId>();
            foreach (ChannelConfiguration channel in zone.Channels)
            {
                ChannelRuntimeDefinition definition = ChannelRuntimeDefinition.FromConfiguration(channel);
                ChannelDefinition identity = ChannelDefinition.FromRuntime(definition,
                    $"{definition.SystemName}\u001F{definition.Name}");
                var id = new ChannelId(identity.SessionId);
                channelIds.Add(id);
                channels.TryAdd(id, new ChannelDescriptor(
                    id, SystemId.FromName(definition.SystemName), zoneId, definition.Name,
                    definition.DestinationId, definition.Protocol.ToString(), definition.Slot,
                    definition.RxOnly,
                    callPrioritySystemNames?.Contains(definition.SystemName, StringComparer.OrdinalIgnoreCase) == true, channel.CardSize));
            }
            zones.Add(new ZoneDescriptor(zoneId, zone.Name, channelIds.ToImmutable(), zone.TabColor, zone.TabTextColor));
        }

        ImmutableArray<SystemDescriptor> systems = configuration.Systems.Select(system =>
        {
            SystemId id = SystemId.FromName(system.Name);
            string protocol = channels.Values.Where(channel => channel.SystemId == id)
                .Select(channel => channel.Protocol)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault() ?? "Unknown";
            return new SystemDescriptor(id, system.Name, protocol);
        }).ToImmutableArray();
        return new ConsoleTopologySnapshot(reference, systems, zones.ToImmutable(), channels.Values.ToImmutableArray());
    }
}
