// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.IO;
using System.Linq;
using dvmconsole;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DvmConsole.Core.Tests
{
    /// <summary>
    /// RED contract for the WPF-compatible operator-preferences section.
    /// The section is persistence-only; runtime application belongs to later
    /// preference gates.
    /// </summary>
    public sealed class UserSettingsPreferencesSectionTests
    {
        private sealed class TempDir : IDisposable
        {
            public string Root { get; } = Path.Combine(
                Path.GetTempPath(),
                "dvmconsole-preferences-section-" + Guid.NewGuid().ToString("N"));

            public TempDir() => Directory.CreateDirectory(Root);

            public string SettingsPath => Path.Combine(Root, "UserSettings.json");

            public void Dispose()
            {
                try
                {
                    if (Directory.Exists(Root))
                    {
                        Directory.Delete(Root, recursive: true);
                    }
                }
                catch (IOException)
                {
                    // Best-effort cleanup; never mask the test result.
                }
            }
        }

        [Fact]
        public void Type_IsPublicSealedWithExactlySixWpfNamedBooleanProperties()
        {
            var type = typeof(UserSettingsPreferencesSection);
            var properties = type.GetProperties();

            Assert.Equal("dvmconsole", type.Namespace);
            Assert.True(type.IsPublic);
            Assert.True(type.IsSealed);
            Assert.Equal(6, properties.Length);
            Assert.All(properties, property => Assert.Equal(typeof(bool), property.PropertyType));
            Assert.Equal(
                new[]
                {
                    "TalkPermitTone",
                    "MuteRxAudioWhileTransmitting",
                    "RetainPatchStateOnStartup",
                    "RestoreSelectedChannelsOnStartup",
                    "DarkMode",
                    "KeepWindowOnTop",
                },
                properties.Select(property => property.Name));
        }

        [Fact]
        public void Defaults_AreWpfCompatibleFalse()
        {
            var section = new UserSettingsPreferencesSection();

            Assert.False(section.TalkPermitTone);
            Assert.False(section.MuteRxAudioWhileTransmitting);
            Assert.False(section.RetainPatchStateOnStartup);
            Assert.False(section.RestoreSelectedChannelsOnStartup);
            Assert.False(section.DarkMode);
            Assert.False(section.KeepWindowOnTop);
        }

        [Fact]
        public void JsonShape_UsesExactWpfPropertyNames()
        {
            var json = JObject.FromObject(new UserSettingsPreferencesSection());

            Assert.Equal(
                new[]
                {
                    "TalkPermitTone",
                    "MuteRxAudioWhileTransmitting",
                    "RetainPatchStateOnStartup",
                    "RestoreSelectedChannelsOnStartup",
                    "DarkMode",
                    "KeepWindowOnTop",
                },
                json.Properties().Select(property => property.Name));
        }

        [Fact]
        public void Store_RoundTripsAllPreferencesAndPreservesUnrelatedValues()
        {
            using var dir = new TempDir();
            File.WriteAllText(
                dir.SettingsPath,
                """
                {
                  "TalkPermitTone": false,
                  "MuteRxAudioWhileTransmitting": false,
                  "RetainPatchStateOnStartup": false,
                  "RestoreSelectedChannelsOnStartup": false,
                  "DarkMode": false,
                  "KeepWindowOnTop": false,
                  "FneSystems": [{ "Name": "system-1", "Port": 62031 }],
                  "WindowLayout": { "Width": 1200, "Height": 800 },
                  "UnknownScalar": "preserve-me"
                }
                """);
            var store = new SettingsSectionStore(dir.SettingsPath);
            var expected = new UserSettingsPreferencesSection
            {
                TalkPermitTone = true,
                MuteRxAudioWhileTransmitting = true,
                RetainPatchStateOnStartup = true,
                RestoreSelectedChannelsOnStartup = true,
                DarkMode = true,
                KeepWindowOnTop = true,
            };

            store.SaveSection(expected);

            Assert.True(store.TryLoadSection(out UserSettingsPreferencesSection actual));
            Assert.True(actual.TalkPermitTone);
            Assert.True(actual.MuteRxAudioWhileTransmitting);
            Assert.True(actual.RetainPatchStateOnStartup);
            Assert.True(actual.RestoreSelectedChannelsOnStartup);
            Assert.True(actual.DarkMode);
            Assert.True(actual.KeepWindowOnTop);

            var saved = JObject.Parse(File.ReadAllText(dir.SettingsPath));
            Assert.Equal("preserve-me", (string)saved["UnknownScalar"]!);
            Assert.Equal("system-1", (string)saved["FneSystems"]![0]!["Name"]!);
            Assert.Equal(62031, (int)saved["FneSystems"]![0]!["Port"]!);
            Assert.Equal(1200, (int)saved["WindowLayout"]!["Width"]!);
            Assert.Equal(800, (int)saved["WindowLayout"]!["Height"]!);
        }

        [Fact]
        public void TryLoad_MissingMalformedOrPartialFileUsesDefaultsAndPresentKeys()
        {
            using var missingDir = new TempDir();
            var missing = new SettingsSectionStore(missingDir.SettingsPath);
            Assert.False(missing.TryLoadSection(out UserSettingsPreferencesSection missingSection));
            Assert.False(missingSection.TalkPermitTone);
            Assert.False(missingSection.KeepWindowOnTop);

            using var malformedDir = new TempDir();
            File.WriteAllText(malformedDir.SettingsPath, "{ not valid json");
            var malformed = new SettingsSectionStore(malformedDir.SettingsPath);
            Assert.False(malformed.TryLoadSection(out UserSettingsPreferencesSection malformedSection));
            Assert.False(malformedSection.DarkMode);
            Assert.False(malformedSection.MuteRxAudioWhileTransmitting);

            using var partialDir = new TempDir();
            File.WriteAllText(
                partialDir.SettingsPath,
                "{ \"TalkPermitTone\": true, \"KeepWindowOnTop\": true }");
            var partial = new SettingsSectionStore(partialDir.SettingsPath);
            Assert.True(partial.TryLoadSection(out UserSettingsPreferencesSection partialSection));
            Assert.True(partialSection.TalkPermitTone);
            Assert.True(partialSection.KeepWindowOnTop);
            Assert.False(partialSection.MuteRxAudioWhileTransmitting);
            Assert.False(partialSection.DarkMode);
        }

        [Fact]
        public void WpfShapedFile_LoadsPreferencesAlongsideLegacyValues()
        {
            using var dir = new TempDir();
            File.WriteAllText(
                dir.SettingsPath,
                """
                {
                  "GlobalPTTShortcut": 65,
                  "TalkPermitTone": true,
                  "MuteRxAudioWhileTransmitting": true,
                  "RetainPatchStateOnStartup": true,
                  "RestoreSelectedChannelsOnStartup": true,
                  "DarkMode": true,
                  "KeepWindowOnTop": true,
                  "SelectedChannels": ["system-1|1001"],
                  "WindowWidth": 1440
                }
                """);
            var store = new SettingsSectionStore(dir.SettingsPath);

            Assert.True(store.TryLoadSection(out UserSettingsPreferencesSection section));
            Assert.True(section.TalkPermitTone);
            Assert.True(section.MuteRxAudioWhileTransmitting);
            Assert.True(section.RetainPatchStateOnStartup);
            Assert.True(section.RestoreSelectedChannelsOnStartup);
            Assert.True(section.DarkMode);
            Assert.True(section.KeepWindowOnTop);
        }
    }
}
