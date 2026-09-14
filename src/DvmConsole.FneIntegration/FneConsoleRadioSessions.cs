// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.FneClient;

namespace DvmConsole.FneIntegration;

/// <summary>
/// Prepares FNE endpoints from application-owned channel state. Hosts supply
/// connection options and key access; no presentation object is retained.
/// </summary>
public static class FneConsoleRadioSessions
{
    public static ValueTask<ConsoleRadioSessions> CreateAsync(
        ConsoleSessionState state,
        IEnumerable<FneConnectionOptions> connections,
        Func<ConsoleChannelState, ChannelConfigurationAccess> createAccess,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConsoleRadioSessionPlan plan = Prepare(state, connections, createAccess);
        return ConsoleRadioSessions.CreateAsync(state, plan, cancellationToken);
    }

    public static ConsoleRadioSessionPlan Prepare(ConsoleSessionState state,
        IEnumerable<FneConnectionOptions> connections,
        Func<ConsoleChannelState, ChannelConfigurationAccess> createAccess)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(createAccess);
        // Prepare every system and channel before any endpoint is constructed.
        // Topology order preserves the established target ordering on desktop.
        var channels = state.Topology.Channels
            .Select(channel => state.Channels[channel.Id])
            .ToLookup(channel => SystemId.FromName(channel.Runtime.Definition.SystemName));
        var bindings = connections.Select(options =>
        {
            ArgumentNullException.ThrowIfNull(options);
            var targets = channels[SystemId.FromName(options.Name)]
                .Select(channel => (State: channel, Access: createAccess(channel)
                    ?? throw new InvalidOperationException("Channel access is required.")))
                .ToArray();
            var factory = new FneRadioSessionFactory(options,
                () => targets.Select(target => target.State.CaptureTransmitDescriptor(target.Access)).ToArray());
            return new ConsoleRadioSessionBinding(factory.Descriptor, factory);
        }).ToArray();
        return new ConsoleRadioSessionPlan(bindings);
    }
}
