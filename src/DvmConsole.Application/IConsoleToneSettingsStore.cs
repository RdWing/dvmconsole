// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Settings;

namespace DvmConsole.Application;

/// <summary>Owned tone-editor copies using the existing settings schema. Saves update presets, alert assets and local monitoring only.</summary>
public interface IConsoleToneSettingsStore
{
    ValueTask<UserSettings> LoadToneSettingsAsync(CancellationToken cancellationToken = default);
    ValueTask SaveToneSettingsAsync(UserSettings settings, CancellationToken cancellationToken = default);
}
