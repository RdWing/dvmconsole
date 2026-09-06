// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Core.Settings;

public sealed class ToolbarClockSetting
{
    public bool Enabled { get; set; }
    public int UtcOffsetHours { get; set; }
    public string ColorHex { get; set; } = ToolbarClockColorPalette.DefaultColorHex;
}
