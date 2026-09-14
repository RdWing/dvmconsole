// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Frozen;

namespace DvmConsole.Application;

/// <summary>Binds live channel captures to the radio endpoints owned by this session.</summary>
internal sealed class TransmitTargetResolver
{
    private readonly ConsoleTransmitChannelDirectory channels;
    private readonly FrozenDictionary<string, IRadioTrafficEndpoint> systems;

    public TransmitTargetResolver(ConsoleTransmitChannelDirectory channels, IEnumerable<IRadioTrafficEndpoint> systems)
    {
        this.channels = channels ?? throw new ArgumentNullException(nameof(channels));
        ArgumentNullException.ThrowIfNull(systems);
        this.systems = systems.GroupBy(system => system.Name, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    public IRadioTrafficEndpoint? FindSystem(string name) => systems.GetValueOrDefault(name);

    public TransmitTarget[] Capture(IEnumerable<ChannelId> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        return selected.Distinct().Select(id =>
        {
            var channel = channels.Capture(id);
            if (!systems.TryGetValue(channel.Definition.SystemName, out var system))
                throw new InvalidOperationException($"The system '{channel.Definition.SystemName}' was not found.");
            return new TransmitTarget(channel, system);
        }).ToArray();
    }
}
