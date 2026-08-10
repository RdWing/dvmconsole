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
    /// RED contract for pure PTT section persistence. Raw WPF key-code to
    /// platform gesture mapping remains a separate shell boundary.
    /// </summary>
    public sealed class PttSettingsPersistenceTests
    {
        private sealed class TempDir : IDisposable
        {
            public string Root { get; } = Path.Combine(
                Path.GetTempPath(),
                "dvmconsole-avalonia-ptt-persistence-" + Guid.NewGuid().ToString("N"));

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
            var type = typeof(PttSettingsPersistence);

            Assert.Equal("DvmConsole.Avalonia.Persistence", type.Namespace);
            Assert.True(type.IsPublic);
            Assert.True(type.IsSealed);
            Assert.NotNull(type.GetConstructor(new[] { typeof(SettingsSectionStore) }));
            Assert.NotNull(type.GetMethod(nameof(PttSettingsPersistence.TryLoad)));
            Assert.NotNull(type.GetMethod(nameof(PttSettingsPersistence.Save)));
        }

        [Fact]
        public void SaveThenTryLoad_RoundTripsRawWpfPttValues()
        {
            using var dir = new TempDir();
            var persistence = CreatePersistence(dir.SettingsPath);
            var expected = new UserSettingsPttSection
            {
                TogglePTTMode = true,
                GlobalPTTShortcut = 0x10041,
                GlobalPTTKeysAllChannels = true
            };

            persistence.Save(expected);

            Assert.True(persistence.TryLoad(out UserSettingsPttSection actual));
            Assert.True(actual.TogglePTTMode);
            Assert.Equal(0x10041, actual.GlobalPTTShortcut);
            Assert.True(actual.GlobalPTTKeysAllChannels);
        }

        [Fact]
        public void Save_PreservesUnrelatedSettingsProperties()
        {
            using var dir = new TempDir();
            File.WriteAllText(
                dir.SettingsPath,
                """
                {
                  "TogglePTTMode": false,
                  "GlobalPTTShortcut": 0,
                  "GlobalPTTKeysAllChannels": false,
                  "FneSystems": [{ "Name": "system-1", "Port": 62031 }],
                  "WindowLayout": { "Width": 1200, "Height": 800 }
                }
                """);
            var persistence = CreatePersistence(dir.SettingsPath);

            persistence.Save(new UserSettingsPttSection
            {
                TogglePTTMode = true,
                GlobalPTTShortcut = 123,
                GlobalPTTKeysAllChannels = true
            });

            var saved = JObject.Parse(File.ReadAllText(dir.SettingsPath));
            Assert.True((bool)saved["TogglePTTMode"]!);
            Assert.Equal(123, (int)saved["GlobalPTTShortcut"]!);
            Assert.True((bool)saved["GlobalPTTKeysAllChannels"]!);
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

            Assert.False(missing.TryLoad(out UserSettingsPttSection missingSection));
            Assert.False(missingSection.TogglePTTMode);
            Assert.Equal(0, missingSection.GlobalPTTShortcut);
            Assert.False(missingSection.GlobalPTTKeysAllChannels);

            using var malformedDir = new TempDir();
            File.WriteAllText(malformedDir.SettingsPath, "{ not valid json");
            var malformed = CreatePersistence(malformedDir.SettingsPath);

            Assert.False(malformed.TryLoad(out UserSettingsPttSection malformedSection));
            Assert.False(malformedSection.TogglePTTMode);
            Assert.Equal(0, malformedSection.GlobalPTTShortcut);
            Assert.False(malformedSection.GlobalPTTKeysAllChannels);
        }

        [Fact]
        public void Save_MalformedFile_PropagatesInsteadOfOverwriting()
        {
            using var dir = new TempDir();
            const string malformed = "{ not valid json";
            File.WriteAllText(dir.SettingsPath, malformed);
            var persistence = CreatePersistence(dir.SettingsPath);

            Assert.ThrowsAny<Exception>(() => persistence.Save(new UserSettingsPttSection
            {
                GlobalPTTShortcut = 123
            }));
            Assert.Equal(malformed, File.ReadAllText(dir.SettingsPath));
        }

        private static PttSettingsPersistence CreatePersistence(string path)
            => new(new SettingsSectionStore(path));
    }
}
