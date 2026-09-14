// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Settings;

namespace DvmConsole.Application;

/// <summary>An owned copy of configuration-scoped group definitions and startup intent.</summary>
public sealed record ConsoleGroupPreferences(CodeplugGroupState Groups, bool RestorePatchesOnStartup);

public interface IConsoleGroupPreferences
{
    ValueTask<ConsoleGroupPreferences> LoadGroupsAsync(CancellationToken cancellationToken = default);
    ValueTask SaveGroupAsync(string name, IReadOnlyList<PatchMemberSetting> members, bool enabled, bool oneWay,
        CancellationToken cancellationToken = default);
    ValueTask SaveRestorePatchesAsync(bool restore, CancellationToken cancellationToken = default);
}
