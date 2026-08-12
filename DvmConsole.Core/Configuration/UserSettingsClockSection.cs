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

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace dvmconsole
{
    /// <summary>
    /// Core-owned toolbar-clock settings section. Property names and nested
    /// values mirror the WPF SettingsManager clock settings; runtime clock
    /// manager and toolbar-strip behavior belongs to a later shell slice.
    /// </summary>
    public sealed class UserSettingsClockSection
    {
        /// <summary>Maximum number of persisted toolbar clock slots.</summary>
        public const int MAX_TOOLBAR_CLOCKS = 8;

        /// <summary>WPF default toolbar clock color.</summary>
        public const string DEFAULT_TOOLBAR_CLOCK_COLOR = "#3A3A3A";

        /// <summary>Whether toolbar clocks use 24-hour formatting.</summary>
        public bool ClockUse24HourTime { get; set; } = true;

        /// <summary>Whether toolbar clocks display seconds.</summary>
        public bool ClockShowSeconds { get; set; } = true;

        /// <summary>
        /// Persisted clock configurations keyed by one-based slot number.
        /// </summary>
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public Dictionary<string, ToolbarClockConfig> ToolbarClockConfigSlots { get; set; } =
            CreateDefaultToolbarClockConfigSlots();

        /// <summary>Persisted toolbar clock configuration rows.</summary>
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<ToolbarClockConfig> ToolbarClockConfigs { get; set; } =
            CreateDefaultToolbarClockConfigs();

        /// <summary>
        /// Returns a normalized copy using WPF load/save precedence and bounds.
        /// Slot values take precedence over list fallback values; the resulting
        /// list and slot map always contain exactly eight fresh entries.
        /// </summary>
        public static UserSettingsClockSection Normalize(UserSettingsClockSection section)
        {
            if (section == null)
                throw new ArgumentNullException(nameof(section));

            List<ToolbarClockConfig> fallback = NormalizeToolbarClockConfigs(section.ToolbarClockConfigs);
            Dictionary<string, ToolbarClockConfig> slots = NormalizeToolbarClockConfigSlots(
                section.ToolbarClockConfigSlots,
                fallback);

            return new UserSettingsClockSection
            {
                ClockUse24HourTime = section.ClockUse24HourTime,
                ClockShowSeconds = section.ClockShowSeconds,
                ToolbarClockConfigSlots = slots,
                ToolbarClockConfigs = ToolbarClockConfigSlotsToList(slots)
            };
        }

        /// <summary>
        /// Returns a normalized copy for save operations. The list is the
        /// caller-owned source of truth, matching WPF
        /// <c>SaveToolbarClockSettings</c>; slots are regenerated from it.
        /// </summary>
        public static UserSettingsClockSection NormalizeForSave(UserSettingsClockSection section)
        {
            if (section == null)
                throw new ArgumentNullException(nameof(section));

            List<ToolbarClockConfig> configs = NormalizeToolbarClockConfigs(section.ToolbarClockConfigs);
            return new UserSettingsClockSection
            {
                ClockUse24HourTime = section.ClockUse24HourTime,
                ClockShowSeconds = section.ClockShowSeconds,
                ToolbarClockConfigs = configs,
                ToolbarClockConfigSlots = ToolbarClockConfigsToSlots(configs)
            };
        }

        private static List<ToolbarClockConfig> CreateDefaultToolbarClockConfigs()
        {
            return Enumerable.Range(0, MAX_TOOLBAR_CLOCKS)
                .Select(_ => new ToolbarClockConfig())
                .ToList();
        }

        private static Dictionary<string, ToolbarClockConfig> CreateDefaultToolbarClockConfigSlots()
        {
            return ToolbarClockConfigsToSlots(CreateDefaultToolbarClockConfigs());
        }

        private static List<ToolbarClockConfig> NormalizeToolbarClockConfigs(
            IEnumerable<ToolbarClockConfig> configs)
        {
            List<ToolbarClockConfig> normalized = (configs ?? Enumerable.Empty<ToolbarClockConfig>())
                .Take(MAX_TOOLBAR_CLOCKS)
                .Select(NormalizeToolbarClockConfig)
                .ToList();

            while (normalized.Count < MAX_TOOLBAR_CLOCKS)
                normalized.Add(new ToolbarClockConfig());

            return normalized;
        }

        private static Dictionary<string, ToolbarClockConfig> NormalizeToolbarClockConfigSlots(
            IDictionary<string, ToolbarClockConfig> slots,
            IEnumerable<ToolbarClockConfig> fallbackConfigs)
        {
            Dictionary<string, ToolbarClockConfig> normalized =
                new Dictionary<string, ToolbarClockConfig>(StringComparer.OrdinalIgnoreCase);
            List<ToolbarClockConfig> fallback = NormalizeToolbarClockConfigs(fallbackConfigs);

            for (int i = 0; i < MAX_TOOLBAR_CLOCKS; i++)
            {
                string slotKey = (i + 1).ToString();
                ToolbarClockConfig source = null;

                if (slots != null && slots.TryGetValue(slotKey, out ToolbarClockConfig slotConfig))
                    source = slotConfig;
                else
                    source = fallback[i];

                normalized[slotKey] = NormalizeToolbarClockConfig(source);
            }

            return normalized;
        }

        private static Dictionary<string, ToolbarClockConfig> ToolbarClockConfigsToSlots(
            IEnumerable<ToolbarClockConfig> configs)
        {
            List<ToolbarClockConfig> normalized = NormalizeToolbarClockConfigs(configs);
            Dictionary<string, ToolbarClockConfig> slots =
                new Dictionary<string, ToolbarClockConfig>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < MAX_TOOLBAR_CLOCKS; i++)
                slots[(i + 1).ToString()] = NormalizeToolbarClockConfig(normalized[i]);

            return slots;
        }

        private static List<ToolbarClockConfig> ToolbarClockConfigSlotsToList(
            IDictionary<string, ToolbarClockConfig> slots)
        {
            return Enumerable.Range(1, MAX_TOOLBAR_CLOCKS)
                .Select(slot => slots != null && slots.TryGetValue(slot.ToString(), out ToolbarClockConfig config)
                    ? NormalizeToolbarClockConfig(config)
                    : new ToolbarClockConfig())
                .ToList();
        }

        private static ToolbarClockConfig NormalizeToolbarClockConfig(ToolbarClockConfig config)
        {
            return new ToolbarClockConfig
            {
                Enabled = config?.Enabled == true,
                UtcOffsetHours = NormalizeToolbarClockOffset(config?.UtcOffsetHours ?? 0),
                ColorHex = NormalizeToolbarClockColor(config?.ColorHex)
            };
        }

        private static int NormalizeToolbarClockOffset(int offsetHours)
        {
            return Math.Max(-12, Math.Min(14, offsetHours));
        }

        private static string NormalizeToolbarClockColor(string colorHex)
        {
            if (string.IsNullOrWhiteSpace(colorHex))
                return DEFAULT_TOOLBAR_CLOCK_COLOR;

            string trimmed = colorHex.Trim();
            if (!trimmed.StartsWith("#") || (trimmed.Length != 7 && trimmed.Length != 9))
                return DEFAULT_TOOLBAR_CLOCK_COLOR;

            return trimmed.ToUpperInvariant();
        }
    }

    /// <summary>
    /// Persisted configuration for one toolbar clock slot.
    /// </summary>
    public sealed class ToolbarClockConfig
    {
        /// <summary>Whether this clock is displayed.</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>UTC offset in hours.</summary>
        public int UtcOffsetHours { get; set; } = 0;

        /// <summary>Clock background color in WPF-compatible hex form.</summary>
        public string ColorHex { get; set; } = UserSettingsClockSection.DEFAULT_TOOLBAR_CLOCK_COLOR;
    }
}
