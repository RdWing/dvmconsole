// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

internal sealed class DesktopAlertToneFiles : ILegacyAlertToneFiles
{
    public static DesktopAlertToneFiles Instance { get; } = new();
    private DesktopAlertToneFiles() { }
    public string GetFileName(string path) => Path.GetFileName(path);
    public bool Exists(string path) => File.Exists(path);
}
