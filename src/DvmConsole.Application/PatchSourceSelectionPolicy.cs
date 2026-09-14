// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>Selects decoder sources from committed group snapshots, never editor state.</summary>
internal static class PatchSourceSelectionPolicy
{
    public static ChannelId[] SelectEnabledSources(IEnumerable<ConsoleGroupDefinitionSnapshot> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var sources = new List<ChannelId>();
        var seen = new HashSet<ChannelId>();
        foreach (var group in groups)
        {
            if (group.IsMultiSelect || !group.SavedEnabled) continue;
            int count = group.OneWay ? Math.Min(1, group.Members.Length) : group.Members.Length;
            for (int index = 0; index < count; index++)
            {
                ChannelId id = group.Members[index];
                if (seen.Add(id)) sources.Add(id);
            }
        }
        return sources.ToArray();
    }
}
