// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using DvmConsole.Avalonia.Persistence;
using dvmconsole;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for the thin AlertSettingsPersistence adapter. Preset
    /// editing, tone generation, playback, and shell wiring remain later seams.
    /// </summary>
    public sealed class AlertSettingsPersistenceTests
    {
        private sealed class TempDir : IDisposable
        {
            public string Root { get; } = Path.Combine(
                Path.GetTempPath(),
                "dvmconsole-avalonia-alert-persistence-" + Guid.NewGuid().ToString("N"));

            public TempDir() => Directory.CreateDirectory(Root);

            public string SettingsPath => Path.Combine(Root, "UserSettings.json");

            public void Dispose()
            {
                try
                {
                    if (Directory.Exists(Root))
                        Directory.Delete(Root, recursive: true);
                }
                catch (IOException)
                {
                    // Best-effort cleanup; never mask the test result.
                }
            }
        }

        [Fact]
        public void Adapter_IsPublicSealedAndBoundToCoreSectionStore()
        {
            var type = typeof(AlertSettingsPersistence);

            Assert.Equal("DvmConsole.Avalonia.Persistence", type.Namespace);
            Assert.True(type.IsPublic);
            Assert.True(type.IsSealed);
            Assert.NotNull(type.GetConstructor(new[] { typeof(SettingsSectionStore) }));
            Assert.NotNull(type.GetMethod(nameof(AlertSettingsPersistence.TryLoad)));
            Assert.NotNull(type.GetMethod(nameof(AlertSettingsPersistence.Save)));
        }

        [Fact]
        public void SaveThenTryLoad_RoundTripsNestedAlertAndPresetData()
        {
            using var dir = new TempDir();
            var persistence = CreatePersistence(dir.SettingsPath);
            var expected = new UserSettingsAlertSection
            {
                AlertToneFilePaths = new List<string> { "tone.wav" },
                AlertTonePositions = new Dictionary<string, UserSettingsLayoutPosition>
                {
                    ["tone.wav"] = new UserSettingsLayoutPosition { X = 12.5, Y = 34.5 }
                },
                AlertToneTabs = new Dictionary<string, string> { ["tone.wav"] = "Alerts" },
                AlertTones = new List<UserSettingsAlertToneConfig>
                {
                    new UserSettingsAlertToneConfig
                    {
                        Id = "alert-1",
                        DisplayName = "Alert",
                        FilePath = "tone.wav",
                        TabName = "Alerts",
                        Position = new UserSettingsLayoutPosition { X = 20, Y = 20 }
                    }
                },
                TonePresets = new List<UserSettingsTonePresetConfig>
                {
                    new UserSettingsTonePresetConfig
                    {
                        Id = "tone-preset-1",
                        DisplayName = "Dispatch",
                        TargetResourceKey = "System|123",
                        Steps = new List<UserSettingsTonePresetStep>
                        {
                            new UserSettingsTonePresetStep { Kind = "tone", FrequencyHz = 880, DurationSeconds = 0.5 }
                        }
                    }
                },
                DtmfPresets = new List<UserSettingsDtmfPresetConfig>
                {
                    new UserSettingsDtmfPresetConfig
                    {
                        Id = "dtmf-preset-1",
                        DisplayName = "Page",
                        TargetResourceKey = "System|123",
                        Steps = new List<UserSettingsDtmfPresetStep>
                        {
                            new UserSettingsDtmfPresetStep { Kind = "digit", Digit = "5", DurationSeconds = 0.75 }
                        }
                    }
                }
            };

            persistence.Save(expected);

            Assert.True(persistence.TryLoad(out UserSettingsAlertSection actual));
            Assert.Equal(expected.AlertToneFilePaths, actual.AlertToneFilePaths);
            Assert.Equal(expected.AlertTonePositions["tone.wav"].X, actual.AlertTonePositions["tone.wav"].X);
            Assert.Equal(expected.AlertTonePositions["tone.wav"].Y, actual.AlertTonePositions["tone.wav"].Y);
            Assert.Equal("Alerts", actual.AlertToneTabs["tone.wav"]);
            Assert.Equal("alert-1", actual.AlertTones[0].Id);
            Assert.Equal("tone-preset-1", actual.TonePresets[0].Id);
            Assert.Equal(880, actual.TonePresets[0].Steps[0].FrequencyHz);
            Assert.Equal("dtmf-preset-1", actual.DtmfPresets[0].Id);
            Assert.Equal("5", actual.DtmfPresets[0].Steps[0].Digit);
        }

        [Fact]
        public void Save_PreservesUnrelatedSettingsProperties()
        {
            using var dir = new TempDir();
            File.WriteAllText(
                dir.SettingsPath,
                """
                {
                  "AlertToneFilePaths": ["old.wav"],
                  "AlertTones": [],
                  "FneSystems": [{ "Name": "system-1", "Port": 62031 }],
                  "WindowLayout": { "Width": 1200, "Height": 800 }
                }
                """);
            var persistence = CreatePersistence(dir.SettingsPath);

            persistence.Save(new UserSettingsAlertSection
            {
                AlertToneFilePaths = new List<string> { "new.wav" }
            });

            var saved = JObject.Parse(File.ReadAllText(dir.SettingsPath));
            Assert.Equal("new.wav", (string)saved["AlertToneFilePaths"]![0]!);
            Assert.Equal("system-1", (string)saved["FneSystems"]![0]!["Name"]!);
            Assert.Equal(62031, (int)saved["FneSystems"]![0]!["Port"]!);
            Assert.Equal(1200, (int)saved["WindowLayout"]!["Width"]!);
            Assert.Equal(800, (int)saved["WindowLayout"]!["Height"]!);
        }

        [Fact]
        public void TryLoad_MissingOrMalformedFile_ReturnsFalseWithFreshDefaults()
        {
            using var missingDir = new TempDir();
            var missing = CreatePersistence(missingDir.SettingsPath);

            Assert.False(missing.TryLoad(out UserSettingsAlertSection missingSection));
            Assert.Empty(missingSection.AlertToneFilePaths);
            Assert.Empty(missingSection.AlertTones);
            Assert.Empty(missingSection.TonePresets);
            Assert.Empty(missingSection.DtmfPresets);

            using var malformedDir = new TempDir();
            File.WriteAllText(malformedDir.SettingsPath, "{ not valid json");
            var malformed = CreatePersistence(malformedDir.SettingsPath);

            Assert.False(malformed.TryLoad(out UserSettingsAlertSection malformedSection));
            Assert.Empty(malformedSection.AlertTonePositions);
            Assert.Empty(malformedSection.AlertToneTabs);
        }

        [Fact]
        public void Save_MalformedFile_PropagatesInsteadOfOverwriting()
        {
            using var dir = new TempDir();
            const string malformed = "{ not valid json";
            File.WriteAllText(dir.SettingsPath, malformed);
            var persistence = CreatePersistence(dir.SettingsPath);

            Assert.ThrowsAny<Exception>(() => persistence.Save(new UserSettingsAlertSection
            {
                AlertToneFilePaths = new List<string> { "must-not-write.wav" }
            }));
            Assert.Equal(malformed, File.ReadAllText(dir.SettingsPath));
        }

        private static AlertSettingsPersistence CreatePersistence(string path)
            => new(new SettingsSectionStore(path));
    }
}
