// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections;

namespace DvmConsole.Presentation;

public interface IPttSettingsViewModel
{
    bool HasHardwarePttCapabilities { get; }
    bool IsKeyboardPttCapabilityAvailable { get; }
    bool IsKeyboardPermissionRequestAvailable { get; }
    IEnumerable KeyboardPttKeyOptions { get; }
    string SelectedGlobalPttKeyName { get; set; }
    string SelectedActiveSystemPttKeyName { get; set; }
    bool TogglePttMode { get; set; }
    bool IsSerialPttCapabilityAvailable { get; }
    bool SerialPttEnabled { get; set; }
    bool SerialPttActiveSystemOnly { get; set; }
    IEnumerable SerialPttPortOptions { get; }
    string SerialPttPortName { get; set; }
    IEnumerable SerialPttBaudRates { get; }
    int SerialPttBaudRate { get; set; }
    string SerialPttStatusText { get; }
}
