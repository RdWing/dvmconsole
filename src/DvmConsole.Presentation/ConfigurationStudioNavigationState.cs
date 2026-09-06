// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Presentation;

/// <summary>
/// Owns Configuration Studio's section catalog and current section. The
/// facade remains responsible for committing editor state before a change and
/// for publishing binding notifications after it.
/// </summary>
internal sealed class ConfigurationStudioNavigationState
{
    public ConfigurationStudioNavigationState(ConfigurationStudioSection initialSection)
    {
        Items =
        [
            new(ConfigurationStudioSection.Overview, "Overview", "File status and validation"),
            new(ConfigurationStudioSection.Systems, "FNE Systems", "Connections and credentials"),
            new(ConfigurationStudioSection.Zones, "Zones & Channels", "CPS-style channel editor"),
            new(ConfigurationStudioSection.Streams, "Web Streams", "Streams across all zones"),
            new(ConfigurationStudioSection.Groups, "Groups", "Definitions and operator state"),
            new(ConfigurationStudioSection.EncryptionKeys, "Encryption Keys", "Referenced local key file"),
            new(ConfigurationStudioSection.Files, "Files & Interoperability", "Managed companions, YAML, and exports")
        ];
        Current = Find(initialSection);
    }

    public IReadOnlyList<ConfigurationStudioNavigationItem> Items { get; }
    public ConfigurationStudioNavigationItem Current { get; private set; }

    public bool Is(ConfigurationStudioSection section) => Current.Section == section;

    public bool Select(ConfigurationStudioNavigationItem? item)
    {
        if (item is null || ReferenceEquals(Current, item))
            return false;
        if (!Items.Contains(item))
            throw new ArgumentException("The selected section is not part of this Studio session.", nameof(item));
        Current = item;
        return true;
    }

    public ConfigurationStudioNavigationItem Find(ConfigurationStudioSection section)
        => Items.First(item => item.Section == section);
}
