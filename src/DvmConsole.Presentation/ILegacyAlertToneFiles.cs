// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Presentation;

/// <summary>Host interpretation of legacy file references; managed tones use asset IDs.</summary>
public interface ILegacyAlertToneFiles
{
    string GetFileName(string path);
    bool Exists(string path);
}
