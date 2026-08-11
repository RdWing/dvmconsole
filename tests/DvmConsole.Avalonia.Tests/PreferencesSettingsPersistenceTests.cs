// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.IO;
using DvmConsole.Avalonia.Persistence;
using dvmconsole;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for the headless Avalonia adapter over the operator
    /// preferences section. Menu, VM wiring, and runtime application are
    /// later slices.
    /// </summary>
    public sealed class PreferencesSettingsPersistenceTests
    {
        private sealed class TempDir : IDisposable
        {
            public string Root { get; } = Path.Combine(
                Path.GetTempPath(),
                "dvmconsole-avalonia-preferences-" + Guid.NewGuid().ToString("N"));

            public TempDir() => Directory.CreateDirectory(Root);

            public string SettingsPath => Path.Combine(Root, "nested", "UserSettings.json");

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
        public void Adapter_IsPublicSealedAndBoundToCoreSectionStore()
        {
            var type = typeof(PreferencesSettingsPersistence);

            Assert.Equal("DvmConsole.Avalonia.Persistence", type.Namespace);
            Assert.True(type.IsPublic);
            Assert.True(type.IsSealed);
            Assert.NotNull(type.GetConstructor(new[] { typeof(SettingsSectionStore) }));
            Assert.NotNull(type.GetMethod(nameof(PreferencesSettingsPersistence.TryLoad)));
            Assert.NotNull(type.GetMethod(nameof(PreferencesSettingsPersistence.Save)));
        }

        [Fact]
        public void SaveThenTryLoad_RoundTripsAllPreferences()
        {
            using var dir = new TempDir();
            var persistence = CreatePersistence(dir.SettingsPath);
            var expected = new UserSettingsPreferencesSection
            {
                TalkPermitTone = true,
                MuteRxAudioWhileTransmitting = true,
                RetainPatchStateOnStartup = true,
                RestoreSelectedChannelsOnStartup = true,
                DarkMode = true,
                KeepWindowOnTop = true,
            };

            persistence.Save(expected);

            Assert.True(persistence.TryLoad(out UserSettingsPreferencesSection actual));
            Assert.True(actual.TalkPermitTone);
            Assert.True(actual.MuteRxAudioWhileTransmitting);
            Assert.True(actual.RetainPatchStateOnStartup);
            Assert.True(actual.RestoreSelectedChannelsOnStartup);
            Assert.True(actual.DarkMode);
            Assert.True(actual.KeepWindowOnTop);
        }

        [Fact]
        public void Save_PreservesUnrelatedSettingsProperties()
        {
            using var dir = new TempDir();
            Directory.CreateDirectory(Path.GetDirectoryName(dir.SettingsPath)!);
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
            var persistence = CreatePersistence(dir.SettingsPath);

            persistence.Save(new UserSettingsPreferencesSection
            {
                TalkPermitTone = true,
                DarkMode = true,
            });

            var saved = JObject.Parse(File.ReadAllText(dir.SettingsPath));
            Assert.True((bool)saved["TalkPermitTone"]!);
            Assert.True((bool)saved["DarkMode"]!);
            Assert.Equal("preserve-me", (string)saved["UnknownScalar"]!);
            Assert.Equal("system-1", (string)saved["FneSystems"]![0]!["Name"]!);
            Assert.Equal(62031, (int)saved["FneSystems"]![0]!["Port"]!);
            Assert.Equal(1200, (int)saved["WindowLayout"]!["Width"]!);
            Assert.Equal(800, (int)saved["WindowLayout"]!["Height"]!);
        }

        [Fact]
        public void TryLoad_MissingOrMalformedFile_ReturnsFalseWithWpfDefaults()
        {
            using var missingDir = new TempDir();
            var missing = CreatePersistence(missingDir.SettingsPath);
            Assert.False(missing.TryLoad(out UserSettingsPreferencesSection missingSection));
            Assert.False(missingSection.TalkPermitTone);
            Assert.False(missingSection.MuteRxAudioWhileTransmitting);
            Assert.False(missingSection.RetainPatchStateOnStartup);
            Assert.False(missingSection.RestoreSelectedChannelsOnStartup);
            Assert.False(missingSection.DarkMode);
            Assert.False(missingSection.KeepWindowOnTop);

            using var malformedDir = new TempDir();
            Directory.CreateDirectory(Path.GetDirectoryName(malformedDir.SettingsPath)!);
            File.WriteAllText(malformedDir.SettingsPath, "{ not valid json");
            var malformed = CreatePersistence(malformedDir.SettingsPath);
            Assert.False(malformed.TryLoad(out UserSettingsPreferencesSection malformedSection));
            Assert.False(malformedSection.TalkPermitTone);
            Assert.False(malformedSection.DarkMode);
        }

        [Fact]
        public void Save_MalformedFile_ThrowsAndLeavesOriginalBytesUntouched()
        {
            using var dir = new TempDir();
            Directory.CreateDirectory(Path.GetDirectoryName(dir.SettingsPath)!);
            const string malformed = "{ not valid json";
            File.WriteAllText(dir.SettingsPath, malformed);
            var persistence = CreatePersistence(dir.SettingsPath);

            Assert.ThrowsAny<Exception>(() => persistence.Save(new UserSettingsPreferencesSection
            {
                TalkPermitTone = true,
            }));
            Assert.Equal(malformed, File.ReadAllText(dir.SettingsPath));
        }

        [Fact]
        public void Save_CreatesMissingParentDirectory()
        {
            using var dir = new TempDir();
            var persistence = CreatePersistence(dir.SettingsPath);

            persistence.Save(new UserSettingsPreferencesSection
            {
                KeepWindowOnTop = true,
            });

            Assert.True(File.Exists(dir.SettingsPath));
            Assert.True(persistence.TryLoad(out UserSettingsPreferencesSection actual));
            Assert.True(actual.KeepWindowOnTop);
        }

        private static PreferencesSettingsPersistence CreatePersistence(string path)
            => new(new SettingsSectionStore(path));
    }
}
