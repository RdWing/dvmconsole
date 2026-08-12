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
    /// Core-owned layout-settings section DTO, persisted by
    /// <see cref="SettingsSectionStore"/>. Property names, JSON shape, and
    /// defaults stay compatible with the WPF SettingsManager layout
    /// properties; values are never normalized.
    /// </summary>
    public sealed class UserSettingsLayoutSection
    {
        /// <summary>
        /// Saved channel widget positions keyed by "System|Channel".
        /// </summary>
        public Dictionary<string, UserSettingsLayoutPosition> ChannelPositions { get; set; } = new Dictionary<string, UserSettingsLayoutPosition>();

        /// <summary>
        /// Saved system status widget positions keyed by system name.
        /// </summary>
        public Dictionary<string, UserSettingsLayoutPosition> SystemStatusPositions { get; set; } = new Dictionary<string, UserSettingsLayoutPosition>();

        /// <summary>
        /// Saved alert tone widget positions keyed by tone file name.
        /// </summary>
        public Dictionary<string, UserSettingsLayoutPosition> AlertTonePositions { get; set; } = new Dictionary<string, UserSettingsLayoutPosition>();

        /// <summary>
        /// Saved web stream chip positions keyed by stream name.
        /// </summary>
        public Dictionary<string, UserSettingsLayoutPosition> WebStreamPositions { get; set; } = new Dictionary<string, UserSettingsLayoutPosition>();

        /// <summary>
        /// Flag indicating whether the console window stays above other windows.
        /// </summary>
        public bool KeepWindowOnTop { get; set; } = false;

        /// <summary>
        /// Flag indicating window maximized state.
        /// </summary>
        public bool Maximized { get; set; } = false;

        /// <summary>
        /// Last width of the console window.
        /// </summary>
        public double WindowWidth { get; set; } = 875;

        /// <summary>
        /// Last height of the console window.
        /// </summary>
        public double WindowHeight { get; set; } = 700;

        /// <summary>
        /// Last width of the console canvas display area.
        /// </summary>
        public double CanvasWidth { get; set; } = 875;

        /// <summary>
        /// Last height of the console canvas display area.
        /// </summary>
        public double CanvasHeight { get; set; } = 700;

        /// <summary>
        /// WPF SettingsManager.ShowSystemStatus widget-visibility preference.
        /// Runtime widget application belongs to the later shell-controls gate.
        /// </summary>
        public bool ShowSystemStatus { get; set; } = true;

        /// <summary>
        /// WPF SettingsManager.ShowChannels widget-visibility preference.
        /// Runtime widget application belongs to the later shell-controls gate.
        /// </summary>
        public bool ShowChannels { get; set; } = true;

        /// <summary>
        /// WPF SettingsManager.ShowAlertTones widget-visibility preference.
        /// Runtime widget application belongs to the later shell-controls gate.
        /// </summary>
        public bool ShowAlertTones { get; set; } = true;

        /// <summary>
        /// Full path to a user defined background image.
        /// </summary>
        public string UserBackgroundImage { get; set; } = null;
    }

    /// <summary>
    /// Data structure representing the position of a layout widget, mirroring
    /// the WPF <c>ChannelPosition</c> class.
    /// </summary>
    public sealed class UserSettingsLayoutPosition
    {
        /// <summary>
        /// Horizontal position.
        /// </summary>
        public double X { get; set; }

        /// <summary>
        /// Vertical position.
        /// </summary>
        public double Y { get; set; }
    }
}
