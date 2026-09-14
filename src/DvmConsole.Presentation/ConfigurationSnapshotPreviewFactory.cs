// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Runtime;

namespace DvmConsole.Presentation;

/// <summary>Offline draft cards use the same snapshot renderer as the mobile console.</summary>
public sealed class ConfigurationSnapshotPreviewFactory : IConfigurationStudioPreviewFactory
{
    public IConfigurationChannelPreviewViewModel Create(ChannelConfiguration channel,
        double x, double y, double cardHeight, bool darkMode)
    {
        var definition = ChannelRuntimeDefinition.FromConfiguration(channel);
        var state = new ConsoleChannelState(definition);
        var descriptor = new ChannelDescriptor(state.Id, new SystemId(definition.SystemName),
            new ZoneId("studio"), definition.Name, definition.DestinationId, definition.Protocol.ToString(),
            definition.Slot, definition.RxOnly, false, channel.CardSize);
        var snapshot = ConsoleChannelSnapshotProjector.Capture(state, new RadioAliasIndex([]),
            new ChannelConfigurationAccess(definition), new ChannelSnapshotContext());
        var card = new ChannelSnapshotCardViewModel(new ChannelListItemViewModel(
            descriptor, new NoOpConsoleCommands(), snapshot));
        card.SetDarkMode(darkMode);
        return new ConfigurationChannelPreviewViewModel(channel, card, x, y, cardHeight);
    }
}
