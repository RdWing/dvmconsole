// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using dvmconsole;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DvmConsole.Avalonia.Services
{
    /// <summary>
    /// Headless settings-transfer boundary for the Avalonia shell. The service
    /// owns only the flat JSON file and the portable transfer codec; category
    /// application is an allowlisted property merge, so unknown transfer fields
    /// and secrets never reach the live settings file.
    /// </summary>
    public sealed class SettingsTransferService
    {
        private static readonly IReadOnlyList<SettingsTransferCategoryDefinition> categoryDefinitions =
            new List<SettingsTransferCategoryDefinition>
            {
                new()
                {
                    Id = "layout",
                    DisplayName = "Console Layout",
                    Description = "Window, canvas, widget, and background placement.",
                    PropertyNames = new List<string>
                    {
                        nameof(UserSettingsLayoutSection.ChannelPositions),
                        nameof(UserSettingsLayoutSection.SystemStatusPositions),
                        nameof(UserSettingsLayoutSection.AlertTonePositions),
                        nameof(UserSettingsLayoutSection.WebStreamPositions),
                        nameof(UserSettingsLayoutSection.Maximized),
                        nameof(UserSettingsLayoutSection.WindowWidth),
                        nameof(UserSettingsLayoutSection.WindowHeight),
                        nameof(UserSettingsLayoutSection.CanvasWidth),
                        nameof(UserSettingsLayoutSection.CanvasHeight),
                        nameof(UserSettingsLayoutSection.UserBackgroundImage),
                    },
                },
                new()
                {
                    Id = "audio",
                    DisplayName = "Audio Routing",
                    Description = "Input, output, AGC, per-resource routing, and volumes.",
                    PropertyNames = new List<string>
                    {
                        nameof(UserSettingsAudioSection.AudioInputDeviceKey),
                        nameof(UserSettingsAudioSection.MasterOutputDeviceKey),
                        nameof(UserSettingsAudioSection.AudioInputAgcEnabled),
                        nameof(UserSettingsAudioSection.ChannelOutputDevices),
                        nameof(UserSettingsAudioSection.ChannelOutputDeviceKeys),
                        nameof(UserSettingsAudioSection.ChannelVolumes),
                        nameof(UserSettingsAudioSection.WebStreamVolumes),
                    },
                },
                new()
                {
                    Id = "tar",
                    DisplayName = "Talkgroup Audio Recorder",
                    Description = "TAR recording folder and per-channel policies.",
                    PropertyNames = new List<string>
                    {
                        nameof(UserSettingsTarSection.TarRecordingsRootPath),
                        nameof(UserSettingsTarSection.TarChannelConfigs),
                    },
                },
                new()
                {
                    Id = "groups",
                    DisplayName = "Groups and Patches",
                    Description = "Patch memberships, modes, and retained enabled state.",
                    PropertyNames = new List<string>
                    {
                        nameof(UserSettingsGroupSection.PatchGroupMemberships),
                        nameof(UserSettingsGroupSection.PatchGroupModes),
                        nameof(UserSettingsGroupSection.PatchGroupEnabledStates),
                    },
                },
                new()
                {
                    Id = "alerts",
                    DisplayName = "Alert Tones and Tone Presets",
                    Description = "Custom tones and generated tone/DTMF presets.",
                    PropertyNames = new List<string>
                    {
                        nameof(UserSettingsAlertSection.AlertToneFilePaths),
                        nameof(UserSettingsAlertSection.AlertTonePositions),
                        nameof(UserSettingsAlertSection.AlertToneTabs),
                        nameof(UserSettingsAlertSection.AlertTones),
                        nameof(UserSettingsAlertSection.TonePresets),
                        nameof(UserSettingsAlertSection.DtmfPresets),
                    },
                },
                new()
                {
                    Id = "clocks",
                    DisplayName = "Toolbar Clocks",
                    Description = "Toolbar clock configuration and formatting.",
                    PropertyNames = new List<string>
                    {
                        nameof(UserSettingsClockSection.ToolbarClockConfigs),
                        nameof(UserSettingsClockSection.ToolbarClockConfigSlots),
                        nameof(UserSettingsClockSection.ClockUse24HourTime),
                        nameof(UserSettingsClockSection.ClockShowSeconds),
                    },
                },
                new()
                {
                    Id = "startup",
                    DisplayName = "Startup and Sticky State",
                    Description = "Restored channels, web streams, and primary resource.",
                    PropertyNames = new List<string>
                    {
                        nameof(UserSettingsRestoreSection.SelectedChannels),
                        nameof(UserSettingsRestoreSection.SelectedWebStreams),
                        nameof(UserSettingsRestoreSection.PrimaryResourceKey),
                    },
                },
                new()
                {
                    Id = "preferences",
                    DisplayName = "Operator Preferences",
                    Description = "Permit tone, RX mute, startup retention, theme, and window state.",
                    PropertyNames = new List<string>
                    {
                        nameof(UserSettingsPreferencesSection.TalkPermitTone),
                        nameof(UserSettingsPreferencesSection.MuteRxAudioWhileTransmitting),
                        nameof(UserSettingsPreferencesSection.RetainPatchStateOnStartup),
                        nameof(UserSettingsPreferencesSection.RestoreSelectedChannelsOnStartup),
                        nameof(UserSettingsPreferencesSection.DarkMode),
                        nameof(UserSettingsPreferencesSection.KeepWindowOnTop),
                    },
                },
                new()
                {
                    Id = "keys-security",
                    DisplayName = "Keybinds and Selectable Encryption",
                    Description = "Non-secret PTT settings and selectable encryption state.",
                    PropertyNames = new List<string>
                    {
                        nameof(UserSettingsPttSection.TogglePTTMode),
                        nameof(UserSettingsPttSection.GlobalPTTShortcut),
                        nameof(UserSettingsPttSection.GlobalPTTKeysAllChannels),
                        nameof(UserSettingsRestoreSection.SelectableEncryptionStates),
                    },
                },
            };

        private readonly string settingsFilePath;

        /// <summary>Creates a transfer service bound to one settings file.</summary>
        public SettingsTransferService(string settingsFilePath)
        {
            if (string.IsNullOrWhiteSpace(settingsFilePath))
            {
                throw new ArgumentException("A settings file path is required.", nameof(settingsFilePath));
            }

            this.settingsFilePath = settingsFilePath;
        }

        /// <summary>Known transfer categories in dependency/application order.</summary>
        public IReadOnlyList<SettingsTransferCategoryDefinition> Categories => categoryDefinitions;

        /// <summary>
        /// Exports the selected categories using the portable Core codec. The
        /// destination is replaced only after the complete JSON has serialized.
        /// </summary>
        public bool Export(string filePath, IEnumerable<string>? categoryIds)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("Export path is required.", nameof(filePath));
            }

            List<SettingsTransferCategoryDefinition> selected = ResolveSelected(categoryIds);
            if (selected.Count == 0)
            {
                throw new InvalidOperationException("Select at least one settings category to export.");
            }

            JObject source = ReadSettingsObject(settingsFilePath);
            JObject payload = BuildPayload(source, selected.SelectMany(category => category.PropertyNames));
            var transferFile = new SettingsTransferFile
            {
                ExportedUtc = DateTime.UtcNow,
                Categories = selected.Select(category => category.Id).ToList(),
                Settings = payload,
            };

            AtomicWrite(filePath, SettingsTransferCodec.Serialize(transferFile));
            return true;
        }

        /// <summary>
        /// Imports selected categories into the bound settings file. The
        /// transfer is parsed and fully prepared before the target is replaced;
        /// a failed read, validation, or serialization leaves the target bytes
        /// untouched.
        /// </summary>
        public IReadOnlyList<string> Import(string filePath, IEnumerable<string>? categoryIds)
        {
            List<SettingsTransferCategoryDefinition> selected = ResolveSelected(categoryIds);
            if (selected.Count == 0)
            {
                throw new InvalidOperationException("Select at least one settings category to import.");
            }

            SettingsTransferFile transferFile = SettingsTransferCodec.ReadFile(filePath);
            HashSet<string> exportedIds = new(
                transferFile.Categories ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            List<SettingsTransferCategoryDefinition> applicable = selected
                .Where(category => exportedIds.Count == 0 || exportedIds.Contains(category.Id))
                .ToList();
            if (applicable.Count == 0)
            {
                throw new InvalidOperationException(SettingsTransferCodec.NO_CATEGORIES_RESOLVED_MESSAGE);
            }

            JObject target = ReadSettingsObject(settingsFilePath);
            foreach (string propertyName in applicable.SelectMany(category => category.PropertyNames)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!transferFile.Settings.TryGetValue(
                        propertyName,
                        StringComparison.OrdinalIgnoreCase,
                        out JToken? token))
                {
                    continue;
                }

                JProperty? existing = target.Properties()
                    .FirstOrDefault(property => string.Equals(
                        property.Name,
                        propertyName,
                        StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    target[propertyName] = token.DeepClone();
                }
                else
                {
                    existing.Value = token.DeepClone();
                }
            }

            AtomicWrite(settingsFilePath, target.ToString(Formatting.Indented));
            return applicable.Select(category => category.DisplayName).ToArray();
        }

        /// <summary>Deletes the settings file; missing files are already reset.</summary>
        public void Reset()
        {
            if (File.Exists(settingsFilePath))
            {
                File.Delete(settingsFilePath);
            }
        }

        private List<SettingsTransferCategoryDefinition> ResolveSelected(
            IEnumerable<string>? categoryIds)
            => SettingsTransferCodec.ResolveCategories(categoryDefinitions, categoryIds ?? Array.Empty<string>());

        private static JObject ReadSettingsObject(string path)
        {
            if (!File.Exists(path))
            {
                return new JObject();
            }

            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new JObject();
            }

            JToken token = JToken.Parse(json);
            if (token is not JObject root)
            {
                throw new JsonException("The settings file does not contain a JSON object.");
            }

            return root;
        }

        private static JObject BuildPayload(
            JObject source,
            IEnumerable<string> propertyNames)
        {
            var payload = new JObject();
            foreach (string propertyName in propertyNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (source.TryGetValue(
                        propertyName,
                        StringComparison.OrdinalIgnoreCase,
                        out JToken? value))
                {
                    payload[propertyName] = value.DeepClone();
                }
            }

            return payload;
        }

        private static void AtomicWrite(string path, string content)
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new IOException("The target path has no directory.");
            }

            Directory.CreateDirectory(directory);
            string temporaryPath = Path.Combine(
                directory,
                "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temporaryPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }
}
