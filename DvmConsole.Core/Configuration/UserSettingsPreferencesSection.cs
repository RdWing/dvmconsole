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
    /// Core-owned operator-preferences section DTO, persisted by
    /// <see cref="SettingsSectionStore"/>. Property names, JSON shape, and
    /// defaults mirror the WPF <c>SettingsManager</c> preference properties;
    /// values are intentionally not applied to runtime behavior by this DTO.
    /// </summary>
    /// <remarks>
    /// Runtime application is split across later preference gates: permit-tone
    /// and RX-mute behavior belong to Gate 3.3, selection restoration belongs
    /// to Gate 3.4, and theme/always-on-top shell behavior belongs to Gate 3.5.
    /// Keeping this section data-only lets the settings file migrate before
    /// those runtime consumers exist.
    /// </remarks>
    public sealed class UserSettingsPreferencesSection
    {
        /// <summary>WPF SettingsManager.TalkPermitTone.</summary>
        public bool TalkPermitTone { get; set; } = false;

        /// <summary>WPF SettingsManager.MuteRxAudioWhileTransmitting.</summary>
        public bool MuteRxAudioWhileTransmitting { get; set; } = false;

        /// <summary>WPF SettingsManager.RetainPatchStateOnStartup.</summary>
        public bool RetainPatchStateOnStartup { get; set; } = false;

        /// <summary>WPF SettingsManager.RestoreSelectedChannelsOnStartup.</summary>
        public bool RestoreSelectedChannelsOnStartup { get; set; } = false;

        /// <summary>WPF dark-mode preference.</summary>
        public bool DarkMode { get; set; } = false;

        /// <summary>WPF always-on-top preference.</summary>
        public bool KeepWindowOnTop { get; set; } = false;
    }
}
