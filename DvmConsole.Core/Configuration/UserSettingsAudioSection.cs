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

using System.Collections.Generic;

namespace dvmconsole
{
    /// <summary>
    /// Core-owned audio-settings section DTO, persisted by
    /// <see cref="SettingsSectionStore"/>. Property names and defaults stay
    /// byte-compatible with the existing WPF SettingsManager schema; values are
    /// never normalized.
    /// </summary>
    public sealed class UserSettingsAudioSection
    {
        /// <summary>
        /// Key of the selected audio input device; "windows-default" selects the
        /// platform default input.
        /// </summary>
        public string AudioInputDeviceKey { get; set; } = "windows-default";

        /// <summary>
        /// Key of the selected master audio output device; "windows-default"
        /// selects the platform default output.
        /// </summary>
        public string MasterOutputDeviceKey { get; set; } = "windows-default";

        /// <summary>
        /// True when automatic gain control is enabled for the audio input.
        /// </summary>
        public bool AudioInputAgcEnabled { get; set; } = false;

        /// <summary>
        /// Legacy per-resource output device indices keyed by stable resource
        /// identity. Retained for WPF settings compatibility.
        /// </summary>
        public Dictionary<string, int> ChannelOutputDevices { get; set; } = new();

        /// <summary>
        /// Stable per-resource output device keys keyed by resource identity.
        /// An absent key inherits the master output device.
        /// </summary>
        public Dictionary<string, string> ChannelOutputDeviceKeys { get; set; } = new();

        /// <summary>
        /// Per-resource monitor volumes keyed by stable resource identity.
        /// Values are interpreted in the WPF-compatible 0..4 range by the
        /// audio runtime; this DTO preserves the persisted value verbatim.
        /// </summary>
        public Dictionary<string, double> ChannelVolumes { get; set; } = new();

        /// <summary>
        /// WPF SettingsManager.WebStreamVolumes keyed by stream name.
        /// </summary>
        public Dictionary<string, double> WebStreamVolumes { get; set; } = new();
    }
}
