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
    /// RED contract for the headless Avalonia adapter over Gate 3.4 restore state.
    /// </summary>
    public sealed class RestoreSettingsPersistenceTests
    {
        private sealed class TempDir : IDisposable
        {
            public string Root { get; } = Path.Combine(
                Path.GetTempPath(),
                "dvmconsole-avalonia-restore-" + Guid.NewGuid().ToString("N"));

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
        public void Adapter_IsPublicSealedAndBoundToCoreRestoreSection()
        {
            var type = typeof(RestoreSettingsPersistence);

            Assert.Equal("DvmConsole.Avalonia.Persistence", type.Namespace);
            Assert.True(type.IsPublic);
            Assert.True(type.IsSealed);
            Assert.NotNull(type.GetConstructor(new[] { typeof(SettingsSectionStore) }));
            Assert.NotNull(type.GetMethod(nameof(RestoreSettingsPersistence.TryLoad)));
            Assert.NotNull(type.GetMethod(nameof(RestoreSettingsPersistence.Save)));
        }

        [Fact]
        public void SaveThenTryLoad_RoundTripsRestoreState()
        {
            using var dir = new TempDir();
            var persistence = CreatePersistence(dir.SettingsPath);
            var expected = new UserSettingsRestoreSection
            {
                SelectedChannels = new List<string> { "System 1|31001" },
                PrimaryResourceKey = "System 1|31001",
                SelectableEncryptionStates = new Dictionary<string, bool>
                {
                    ["System 1|31001"] = true,
                },
            };

            persistence.Save(expected);

            Assert.True(persistence.TryLoad(out UserSettingsRestoreSection actual));
            Assert.Equal(expected.SelectedChannels, actual.SelectedChannels);
            Assert.Equal(expected.PrimaryResourceKey, actual.PrimaryResourceKey);
            Assert.Equal(expected.SelectableEncryptionStates, actual.SelectableEncryptionStates);
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
                  "SelectedChannels": ["old"],
                  "PrimaryResourceKey": "old",
                  "SelectableEncryptionStates": { "old": false },
                  "FneSystems": [{ "Name": "system-1", "Port": 62031 }],
                  "WindowLayout": { "Width": 1200 },
                  "UnknownScalar": "preserve-me"
                }
                """);
            var persistence = CreatePersistence(dir.SettingsPath);

            persistence.Save(new UserSettingsRestoreSection
            {
                SelectedChannels = new List<string> { "System 1|31001" },
                PrimaryResourceKey = "System 1|31001",
                SelectableEncryptionStates = new Dictionary<string, bool>
                {
                    ["System 1|31001"] = true,
                },
            });

            var saved = JObject.Parse(File.ReadAllText(dir.SettingsPath));
            Assert.Equal("System 1|31001", (string)saved["PrimaryResourceKey"]!);
            Assert.Equal("preserve-me", (string)saved["UnknownScalar"]!);
            Assert.Equal("system-1", (string)saved["FneSystems"]![0]!["Name"]!);
            Assert.Equal(62031, (int)saved["FneSystems"]![0]!["Port"]!);
            Assert.Equal(1200, (int)saved["WindowLayout"]!["Width"]!);
        }

        [Fact]
        public void TryLoad_MissingOrMalformedFile_ReturnsDefaultsWithoutThrowing()
        {
            using var missingDir = new TempDir();
            var missing = CreatePersistence(missingDir.SettingsPath);
            Assert.False(missing.TryLoad(out UserSettingsRestoreSection missingSection));
            Assert.Null(missingSection.PrimaryResourceKey);
            Assert.Empty(missingSection.SelectedChannels);
            Assert.Empty(missingSection.SelectableEncryptionStates);

            using var malformedDir = new TempDir();
            Directory.CreateDirectory(Path.GetDirectoryName(malformedDir.SettingsPath)!);
            File.WriteAllText(malformedDir.SettingsPath, "{ not valid json");
            var malformed = CreatePersistence(malformedDir.SettingsPath);
            Assert.False(malformed.TryLoad(out UserSettingsRestoreSection malformedSection));
            Assert.Null(malformedSection.PrimaryResourceKey);
            Assert.Empty(malformedSection.SelectedChannels);
        }

        private static RestoreSettingsPersistence CreatePersistence(string path)
            => new(new SettingsSectionStore(path));
    }
}
