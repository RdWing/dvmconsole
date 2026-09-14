// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Mobile;

/// <summary>Device-local gesture preference for a stable configuration identity.</summary>
public interface IMobilePttPreferences
{
    bool ReadTogglePtt(string configurationId);
    void WriteTogglePtt(string configurationId, bool enabled);
}
