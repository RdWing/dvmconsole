// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;

namespace DvmConsole.Application;

public sealed record ConsoleGroupDefinitionSnapshot(string Name, bool IsMultiSelect,
    ImmutableArray<ChannelId> Members, bool SavedEnabled, bool OneWay, int UnresolvedMembers);

/// <summary>Saved group definitions; enabled intent does not imply active forwarding.</summary>
public interface IConsoleGroupSettings
{
    IReadOnlyList<ConsoleGroupDefinitionSnapshot> SavedGroups { get; }
    bool RestorePatchesOnStartup { get; }
    IReadOnlySet<string> EnabledPatchGroups { get; }
    Task SaveGroupAsync(string name, IReadOnlyList<ChannelId> members, bool enabled, bool oneWay,
        CancellationToken cancellationToken = default);
    Task SetRestorePatchesAsync(bool restore, CancellationToken cancellationToken = default);
}
