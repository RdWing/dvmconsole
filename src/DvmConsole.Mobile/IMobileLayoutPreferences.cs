// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Presentation;

namespace DvmConsole.Mobile;

/// <summary>Host-owned device preferences, scoped by stable configuration identity.</summary>
public interface IMobileLayoutPreferences
{
    ConsoleRendererPreference? Read(string configurationId);
    void Write(string configurationId, ConsoleRendererPreference preference);
}
