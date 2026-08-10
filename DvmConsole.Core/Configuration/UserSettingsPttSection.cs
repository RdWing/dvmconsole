// SPDX-License-Identifier: AGPL-3.0-only
/**
* Digital Voice Modem - Desktop Dispatch Console
* AGPLv3 Open Source. Use is subject to license terms.
* DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
*
* @package DVM / Desktop Dispatch Console
* @license AGPLv3 License (https://opensource.org/licenses/AGPL-3.0)
*
*   Copyright (C) 2026 C. Lovell, Dev_Ranger
*
*/

namespace dvmconsole
{
    /// <summary>
    /// Core-owned PTT-settings section DTO, persisted by
    /// <see cref="SettingsSectionStore"/>. Property names, JSON shape, and
    /// defaults stay compatible with the WPF SettingsManager PTT properties;
    /// values are never validated or normalized.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="GlobalPTTShortcut"/> is persisted as the raw WPF
    /// <c>System.Windows.Forms.Keys</c> virtual-key code (<c>Keys.None</c> is
    /// zero). Core intentionally has no dependency on DvmConsole.Platform
    /// hotkey types: the later Avalonia adapter maps this stable integer to
    /// the platform hotkey enums. Preserve the value verbatim; zero means
    /// "no shortcut".
    /// </para>
    /// </remarks>
    public sealed class UserSettingsPttSection
    {
        /// <summary>
        /// Flag indicating the PTT mode: Toggle PTT or Regular PTT (WPF
        /// SettingsManager.TogglePTTMode).
        /// </summary>
        public bool TogglePTTMode { get; set; } = false;

        /// <summary>
        /// Global PTT shortcut key code (WPF SettingsManager.GlobalPTTShortcut,
        /// a <c>System.Windows.Forms.Keys</c> value). Persisted as its integer
        /// virtual-key code; zero represents <c>Keys.None</c> (no shortcut).
        /// Core does not reference DvmConsole.Platform.Hotkeys or
        /// HotkeyGesture — the Avalonia adapter maps this integer to the
        /// platform hotkey enums.
        /// </summary>
        public int GlobalPTTShortcut { get; set; } = 0;

        /// <summary>
        /// Flag indicating the global PTT shortcut applies to all channels
        /// (WPF SettingsManager.GlobalPTTKeysAllChannels).
        /// </summary>
        public bool GlobalPTTKeysAllChannels { get; set; } = false;
    }
}
