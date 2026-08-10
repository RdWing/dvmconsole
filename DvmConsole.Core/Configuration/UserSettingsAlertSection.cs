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
    /// Core-owned alert-tone and tone/DTMF preset settings section DTO,
    /// persisted by <see cref="SettingsSectionStore"/>. Property names, JSON
    /// shape, and defaults stay compatible with the WPF SettingsManager alert
    /// and preset properties (SettingsManager.cs:105-130, :247-310); values
    /// are never normalized, validated, or reordered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This section is data-only. Tone generation, DTMF synthesis, and audio
    /// playback are deliberately NOT part of these DTOs — the WPF
    /// tone-generator/player behavior is deferred to a later audio slice that
    /// will interpret the persisted preset steps. Core only stores them.
    /// </para>
    /// <para>
    /// <see cref="AlertTonePositions"/> reuses the portable
    /// <see cref="UserSettingsLayoutPosition"/> (X/Y doubles) in place of the
    /// WPF <c>ChannelPosition</c> (MainWindow.xaml.cs:61-75). JSON
    /// compatibility depends on the object shape, not the CLR type name, so
    /// the shared Core position type is safe.
    /// </para>
    /// </remarks>
    public sealed class UserSettingsAlertSection
    {
        /// <summary>
        /// Saved alert tone file paths (WPF SettingsManager.AlertToneFilePaths).
        /// </summary>
        public List<string> AlertToneFilePaths { get; set; } = new List<string>();

        /// <summary>
        /// Saved alert tone widget positions keyed by tone file name (WPF
        /// SettingsManager.AlertTonePositions).
        /// </summary>
        public Dictionary<string, UserSettingsLayoutPosition> AlertTonePositions { get; set; } = new Dictionary<string, UserSettingsLayoutPosition>();

        /// <summary>
        /// Saved tab assignment for alert tone widgets (WPF
        /// SettingsManager.AlertToneTabs).
        /// </summary>
        public Dictionary<string, string> AlertToneTabs { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// Saved alert tone configurations using a stable ID (WPF
        /// SettingsManager.AlertTones).
        /// </summary>
        public List<UserSettingsAlertToneConfig> AlertTones { get; set; } = new List<UserSettingsAlertToneConfig>();

        /// <summary>
        /// Saved generated tone presets using a stable ID (WPF
        /// SettingsManager.TonePresets).
        /// </summary>
        public List<UserSettingsTonePresetConfig> TonePresets { get; set; } = new List<UserSettingsTonePresetConfig>();

        /// <summary>
        /// Saved DTMF presets using a stable ID (WPF
        /// SettingsManager.DtmfPresets).
        /// </summary>
        public List<UserSettingsDtmfPresetConfig> DtmfPresets { get; set; } = new List<UserSettingsDtmfPresetConfig>();
    }

    /// <summary>
    /// Persisted configuration for a custom alert tone widget, mirroring the
    /// WPF SettingsManager.AlertToneConfig class (SettingsManager.cs:249-256).
    /// </summary>
    public sealed class UserSettingsAlertToneConfig
    {
        /// <summary>
        /// Stable identifier (WPF default: <c>Guid.NewGuid().ToString("N")</c>).
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// Display name of the alert tone widget.
        /// </summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// Path of the alert tone audio file.
        /// </summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>
        /// Tab the alert tone widget is assigned to.
        /// </summary>
        public string TabName { get; set; } = string.Empty;

        /// <summary>
        /// Widget position (WPF default: X = 20, Y = 20).
        /// </summary>
        public UserSettingsLayoutPosition Position { get; set; } = new UserSettingsLayoutPosition { X = 20, Y = 20 };
    }

    /// <summary>
    /// Persisted generated tone preset, mirroring the WPF
    /// SettingsManager.TonePresetConfig class (SettingsManager.cs:261-267).
    /// </summary>
    public sealed class UserSettingsTonePresetConfig
    {
        /// <summary>
        /// Stable identifier (WPF default: <c>Guid.NewGuid().ToString("N")</c>).
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// Display name of the preset.
        /// </summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// Resource the preset targets, e.g. "System|Talkgroup".
        /// </summary>
        public string TargetResourceKey { get; set; } = string.Empty;

        /// <summary>
        /// Ordered tone steps of the preset stack.
        /// </summary>
        public List<UserSettingsTonePresetStep> Steps { get; set; } = new List<UserSettingsTonePresetStep>();
    }

    /// <summary>
    /// One generated tone step in a preset stack, mirroring the WPF
    /// SettingsManager.TonePresetStep class (SettingsManager.cs:272-277).
    /// </summary>
    public sealed class UserSettingsTonePresetStep
    {
        /// <summary>
        /// Step kind; "tone" (WPF
        /// SettingsManager.TONE_PRESET_STEP_KIND_TONE).
        /// </summary>
        public string Kind { get; set; } = "tone";

        /// <summary>
        /// Tone frequency in hertz (WPF default: 1000).
        /// </summary>
        public double FrequencyHz { get; set; } = 1000;

        /// <summary>
        /// Step duration in seconds (WPF default: 1).
        /// </summary>
        public double DurationSeconds { get; set; } = 1;
    }

    /// <summary>
    /// Persisted DTMF preset, mirroring the WPF
    /// SettingsManager.DtmfPresetConfig class (SettingsManager.cs:282-288).
    /// </summary>
    public sealed class UserSettingsDtmfPresetConfig
    {
        /// <summary>
        /// Stable identifier (WPF default: <c>Guid.NewGuid().ToString("N")</c>).
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// Display name of the preset.
        /// </summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// Resource the preset targets, e.g. "System|Talkgroup".
        /// </summary>
        public string TargetResourceKey { get; set; } = string.Empty;

        /// <summary>
        /// Ordered DTMF steps of the preset stack.
        /// </summary>
        public List<UserSettingsDtmfPresetStep> Steps { get; set; } = new List<UserSettingsDtmfPresetStep>();
    }

    /// <summary>
    /// One DTMF digit or hold step in a preset stack, mirroring the WPF
    /// SettingsManager.DtmfPresetStep class (SettingsManager.cs:305-310).
    /// </summary>
    public sealed class UserSettingsDtmfPresetStep
    {
        /// <summary>
        /// Step kind; "digit" (WPF
        /// SettingsManager.DTMF_PRESET_STEP_KIND_DIGIT).
        /// </summary>
        public string Kind { get; set; } = "digit";

        /// <summary>
        /// DTMF digit of the step (WPF default: "1").
        /// </summary>
        public string Digit { get; set; } = "1";

        /// <summary>
        /// Step duration in seconds (WPF default:
        /// SettingsManager.TONE_PRESET_MIN_DURATION_SECONDS, 0.25).
        /// </summary>
        public double DurationSeconds { get; set; } = 0.25;
    }
}
