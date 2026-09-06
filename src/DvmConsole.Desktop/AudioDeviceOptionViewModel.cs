// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only


namespace DvmConsole.Desktop;

public sealed record AudioDeviceOptionViewModel(
    string Id,
    string Name,
    bool IsDefault,
    bool? IsBluetooth = null) :
    DvmConsole.Presentation.IAudioDeviceOptionViewModel
{
    public string DisplayName => IsDefault ? $"{Name} (default)" : Name;
}
