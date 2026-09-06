// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Core.Settings;

// A user-profile alert asset. New imports use app-owned asset IDs. FilePath is
// retained only as a lazy migration source for pre-library desktop settings.
public sealed class AlertToneSetting
{
    public string Name { get; set; } = "Alert tone";
    public string? AssetId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
}
