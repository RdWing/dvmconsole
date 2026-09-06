// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Settings;

namespace DvmConsole.Desktop;

/// <summary>
/// Owns the selected system/channel identity and its persisted restoration.
/// Presentation notifications and persistence scheduling remain with the
/// binding facade.
/// </summary>
internal sealed class ChannelSelectionController
{
    private readonly IReadOnlyList<SystemViewModel> systems;
    private readonly UserSettings settings;

    public ChannelSelectionController(
        IReadOnlyList<SystemViewModel> systems,
        UserSettings settings)
    {
        this.systems = systems ?? throw new ArgumentNullException(nameof(systems));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public ChannelViewModel? SelectedChannel { get; private set; }
    public SystemViewModel? SelectedSystem { get; private set; }

    public void Restore()
    {
        SelectedChannel = settings.RestoreSelectedChannelsOnStartup
            ? systems
                .SelectMany(system => system.Channels)
                .FirstOrDefault(channel => channel.SettingsKey.Equals(
                    settings.LastSelectedChannelKey,
                    StringComparison.Ordinal))
            : null;
        SelectedSystem = settings.RestoreSelectedChannelsOnStartup
            ? systems.FirstOrDefault(system => system.Name.Equals(
                settings.LastSelectedSystemName,
                StringComparison.OrdinalIgnoreCase)) ??
              systems.FirstOrDefault(system =>
                  SelectedChannel is not null && system.Channels.Contains(SelectedChannel)) ??
              (systems.Count > 0 ? systems[0] : null)
            : systems.Count > 0 ? systems[0] : null;
        ApplySystemSelection();
    }

    public bool SelectSystem(SystemViewModel? system)
    {
        if (ReferenceEquals(SelectedSystem, system))
            return false;
        if (system is not null && !systems.Contains(system))
            throw new ArgumentException("The selected system does not belong to this session.", nameof(system));

        SelectedSystem = system;
        settings.LastSelectedSystemName = system?.Name;
        ApplySystemSelection();
        return true;
    }

    public bool SelectChannel(ChannelViewModel channel, bool selectionLocked)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (selectionLocked && SelectedChannel is not null && !ReferenceEquals(SelectedChannel, channel))
            return false;
        if (ReferenceEquals(SelectedChannel, channel))
            return false;

        SystemViewModel? owner = systems.FirstOrDefault(system => system.Channels.Contains(channel));
        if (owner is null)
            throw new ArgumentException("The selected channel does not belong to this session.", nameof(channel));

        SelectedChannel = channel;
        SelectedSystem = owner;
        settings.LastSelectedSystemName = owner.Name;
        settings.LastSelectedChannelKey = channel.SettingsKey;
        ApplySystemSelection();
        return true;
    }

    public bool ClearIfChannelUnavailable()
    {
        if (SelectedChannel is null || systems.SelectMany(system => system.Channels).Contains(SelectedChannel))
            return false;

        SelectedChannel = null;
        SelectedSystem = null;
        settings.LastSelectedSystemName = null;
        settings.LastSelectedChannelKey = null;
        ApplySystemSelection();
        return true;
    }

    private void ApplySystemSelection()
    {
        foreach (SystemViewModel system in systems)
            system.SetSelected(ReferenceEquals(system, SelectedSystem));
    }
}
