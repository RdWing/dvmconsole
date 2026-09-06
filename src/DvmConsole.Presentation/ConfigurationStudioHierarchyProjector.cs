// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using DvmConsole.Core.Configuration;

namespace DvmConsole.Presentation;

internal sealed record ConfigurationStudioHierarchyProjection(
    IReadOnlyList<ConfigurationHierarchyNode> Roots,
    ConfigurationHierarchyNode? SelectedNode);

internal sealed class ConfigurationStudioHierarchyProjector
{
    private readonly Dictionary<SystemConfiguration, ConfigurationHierarchyNode> systemNodes = [];
    private readonly Dictionary<ZoneConfiguration, ConfigurationHierarchyNode> zoneNodes = [];
    private readonly Dictionary<ChannelConfiguration, ConfigurationHierarchyNode> channelNodes = [];
    private readonly ConfigurationHierarchyNode unassignedNode =
        new("Unassigned or mixed", isExpanded: true);

    public ConfigurationStudioHierarchyProjector()
        => unassignedNode.PropertyChanged += ForwardNodePropertyChanged;

    public event PropertyChangedEventHandler? NodePropertyChanged;

    public bool TryGetChannelNode(
        ChannelConfiguration channel,
        out ConfigurationHierarchyNode? node)
        => channelNodes.TryGetValue(channel, out node);

    public bool TryGetZoneNode(
        ZoneConfiguration zone,
        out ConfigurationHierarchyNode? node)
        => zoneNodes.TryGetValue(zone, out node);

    public ConfigurationHierarchyNode? FindSystemNode(ConfigurationHierarchyNode zoneNode)
        => systemNodes.Values.FirstOrDefault(node => node.Children.Contains(zoneNode));

    public ConfigurationStudioHierarchyProjection Project(
        ConsoleConfiguration configuration,
        Func<ZoneConfiguration, string> getZoneSystemName,
        string? query,
        SystemConfiguration? selectedSystem,
        ZoneConfiguration? selectedZone,
        ChannelConfiguration? selectedChannel)
    {
        Prune(configuration);
        string normalized = (query ?? string.Empty).Trim();
        bool Matches(string value) => normalized.Length == 0 ||
            value.Contains(normalized, StringComparison.OrdinalIgnoreCase);

        var roots = new List<ConfigurationHierarchyNode>();
        foreach (SystemConfiguration system in configuration.Systems)
        {
            ConfigurationHierarchyNode systemNode = GetSystemNode(system);
            bool systemMatches = Matches(system.Name) || Matches(system.Address);
            var visibleZones = new List<ConfigurationHierarchyNode>();
            foreach (ZoneConfiguration zone in configuration.Zones.Where(zone =>
                         string.Equals(getZoneSystemName(zone), system.Name, StringComparison.OrdinalIgnoreCase)))
            {
                ConfigurationHierarchyNode zoneNode = GetZoneNode(zone, selectedZone);
                bool zoneMatches = Matches(zone.Name);
                ConfigurationHierarchyNode[] visibleChannels = zone.Channels
                    .Where(channel => normalized.Length == 0 || systemMatches || zoneMatches ||
                        Matches(channel.Name) || Matches(channel.Tgid) ||
                        Matches(ConfigurationProtocolCatalog.DisplayName(channel.Mode)))
                    .Select(channel => GetChannelNode(zone, channel))
                    .ToArray();
                Replace(zoneNode.Children, visibleChannels);
                zoneNode.Refresh();
                if (normalized.Length == 0 || systemMatches || zoneMatches || visibleChannels.Length > 0)
                    visibleZones.Add(zoneNode);
            }

            Replace(systemNode.Children, visibleZones);
            systemNode.Refresh();
            if (normalized.Length == 0 || systemMatches || visibleZones.Count > 0)
                roots.Add(systemNode);
        }

        ZoneConfiguration[] unassignedZones = configuration.Zones
            .Where(zone => string.IsNullOrWhiteSpace(getZoneSystemName(zone)) ||
                           !configuration.Systems.Any(system => string.Equals(
                               system.Name,
                               getZoneSystemName(zone),
                               StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var visibleUnassigned = new List<ConfigurationHierarchyNode>();
        foreach (ZoneConfiguration zone in unassignedZones)
        {
            ConfigurationHierarchyNode zoneNode = GetZoneNode(zone, selectedZone);
            bool zoneMatches = Matches(zone.Name);
            ConfigurationHierarchyNode[] visibleChannels = zone.Channels
                .Where(channel => normalized.Length == 0 || zoneMatches ||
                    Matches(channel.Name) || Matches(channel.Tgid))
                .Select(channel => GetChannelNode(zone, channel))
                .ToArray();
            Replace(zoneNode.Children, visibleChannels);
            zoneNode.Refresh();
            if (normalized.Length == 0 || zoneMatches || visibleChannels.Length > 0)
                visibleUnassigned.Add(zoneNode);
        }
        Replace(unassignedNode.Children, visibleUnassigned);
        unassignedNode.Refresh();
        if (visibleUnassigned.Count > 0)
            roots.Add(unassignedNode);

        ConfigurationHierarchyNode? selectedNode = selectedChannel is not null &&
                                                    channelNodes.TryGetValue(selectedChannel, out ConfigurationHierarchyNode? selectedChannelNode)
            ? selectedChannelNode
            : selectedZone is not null && zoneNodes.TryGetValue(selectedZone, out ConfigurationHierarchyNode? selectedZoneNode)
                ? selectedZoneNode
                : selectedSystem is not null && systemNodes.TryGetValue(selectedSystem, out ConfigurationHierarchyNode? selectedSystemNode)
                    ? selectedSystemNode
                    : null;
        return new ConfigurationStudioHierarchyProjection(roots, selectedNode);
    }

    private ConfigurationHierarchyNode GetSystemNode(SystemConfiguration system)
    {
        if (systemNodes.TryGetValue(system, out ConfigurationHierarchyNode? node))
            return node;
        node = new ConfigurationHierarchyNode(system.Name, system: system, isExpanded: true);
        node.PropertyChanged += ForwardNodePropertyChanged;
        systemNodes.Add(system, node);
        return node;
    }

    private ConfigurationHierarchyNode GetZoneNode(
        ZoneConfiguration zone,
        ZoneConfiguration? selectedZone)
    {
        if (zoneNodes.TryGetValue(zone, out ConfigurationHierarchyNode? node))
            return node;
        node = new ConfigurationHierarchyNode(
            zone.Name,
            zone: zone,
            isExpanded: ReferenceEquals(zone, selectedZone));
        node.PropertyChanged += ForwardNodePropertyChanged;
        zoneNodes.Add(zone, node);
        return node;
    }

    private ConfigurationHierarchyNode GetChannelNode(
        ZoneConfiguration zone,
        ChannelConfiguration channel)
    {
        if (!channelNodes.TryGetValue(channel, out ConfigurationHierarchyNode? node))
        {
            node = new ConfigurationHierarchyNode(channel.Name, zone: zone, channel: channel);
            channelNodes.Add(channel, node);
        }
        node.Refresh();
        return node;
    }

    private void Prune(ConsoleConfiguration configuration)
    {
        foreach (SystemConfiguration system in systemNodes.Keys
                     .Where(system => !configuration.Systems.Contains(system))
                     .ToArray())
        {
            if (systemNodes.Remove(system, out ConfigurationHierarchyNode? node))
                node.PropertyChanged -= ForwardNodePropertyChanged;
        }
        foreach (ZoneConfiguration zone in zoneNodes.Keys
                     .Where(zone => !configuration.Zones.Contains(zone))
                     .ToArray())
        {
            if (zoneNodes.Remove(zone, out ConfigurationHierarchyNode? node))
                node.PropertyChanged -= ForwardNodePropertyChanged;
        }
        HashSet<ChannelConfiguration> channels = configuration.Zones
            .SelectMany(zone => zone.Channels)
            .ToHashSet();
        foreach (ChannelConfiguration channel in channelNodes.Keys
                     .Where(channel => !channels.Contains(channel))
                     .ToArray())
        {
            channelNodes.Remove(channel);
        }
    }

    private void ForwardNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
        => NodePropertyChanged?.Invoke(sender, e);

    private static void Replace<T>(IList<T> target, IEnumerable<T> values)
    {
        T[] replacement = values.ToArray();
        // Keep realized tree items and their focus/expansion when a field commit
        // only refreshes labels. Rebuilding unchanged children resets selection.
        if (target.SequenceEqual(replacement))
            return;
        target.Clear();
        foreach (T value in replacement)
            target.Add(value);
    }
}
