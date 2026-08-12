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
    /// RED contract for the thin groups-settings persistence adapter. Group
    /// editing, PatchManager composition, and patch/multi-select runtime remain
    /// later shell and runtime seams.
    /// </summary>
    public sealed class GroupSettingsPersistenceTests
    {
        private sealed class TempDir : IDisposable
        {
            public string Root { get; } = Path.Combine(
                Path.GetTempPath(),
                "dvmconsole-avalonia-groups-persistence-" + Guid.NewGuid().ToString("N"));

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
            var type = typeof(GroupSettingsPersistence);

            Assert.Equal("DvmConsole.Avalonia.Persistence", type.Namespace);
            Assert.True(type.IsPublic);
            Assert.True(type.IsSealed);
            Assert.NotNull(type.GetConstructor(new[] { typeof(SettingsSectionStore) }));
            Assert.NotNull(type.GetMethod(nameof(GroupSettingsPersistence.TryLoad)));
            Assert.NotNull(type.GetMethod(nameof(GroupSettingsPersistence.Save)));
        }

        [Fact]
        public void SaveThenTryLoad_RoundTripsOrderedMembersModesAndEnabledStates()
        {
            using var dir = new TempDir();
            var persistence = CreatePersistence(dir.SettingsPath);
            var expected = new UserSettingsGroupSection
            {
                PatchGroupMemberships = new Dictionary<string, Dictionary<string, List<PatchTalkgroupMember>>>
                {
                    ["codeplug-a"] = new Dictionary<string, List<PatchTalkgroupMember>>
                    {
                        ["Patch A"] = new List<PatchTalkgroupMember>
                        {
                            new PatchTalkgroupMember { SystemName = "System One", Tgid = "123" },
                            new PatchTalkgroupMember { SystemName = "System Two", Tgid = "456" },
                        }
                    }
                },
                PatchGroupModes = new Dictionary<string, Dictionary<string, bool>>
                {
                    ["codeplug-a"] = new Dictionary<string, bool> { ["Patch A"] = true }
                },
                PatchGroupEnabledStates = new Dictionary<string, Dictionary<string, bool>>
                {
                    ["codeplug-a"] = new Dictionary<string, bool> { ["Patch A"] = true }
                }
            };

            persistence.Save(expected);

            Assert.True(persistence.TryLoad(out UserSettingsGroupSection actual));
            Assert.Equal("System One", actual.PatchGroupMemberships["codeplug-a"]["Patch A"][0].SystemName);
            Assert.Equal("456", actual.PatchGroupMemberships["codeplug-a"]["Patch A"][1].Tgid);
            Assert.True(actual.PatchGroupModes["codeplug-a"]["Patch A"]);
            Assert.True(actual.PatchGroupEnabledStates["codeplug-a"]["Patch A"]);
        }

        [Fact]
        public void Save_PreservesUnrelatedSettingsProperties()
        {
            using var dir = new TempDir();
            File.WriteAllText(
                dir.SettingsPath,
                """
                {
                  "PatchGroupMemberships": {},
                  "FneSystems": [{ "Name": "system-1", "Port": 62031 }],
                  "WindowLayout": { "Width": 1200, "Height": 800 }
                }
                """);
            var persistence = CreatePersistence(dir.SettingsPath);

            persistence.Save(new UserSettingsGroupSection
            {
                PatchGroupModes = new Dictionary<string, Dictionary<string, bool>>
                {
                    ["codeplug-a"] = new Dictionary<string, bool> { ["Patch A"] = true }
                }
            });

            var saved = JObject.Parse(File.ReadAllText(dir.SettingsPath));
            Assert.Equal("system-1", (string)saved["FneSystems"]![0]!["Name"]!);
            Assert.Equal(62031, (int)saved["FneSystems"]![0]!["Port"]!);
            Assert.Equal(1200, (int)saved["WindowLayout"]!["Width"]!);
            Assert.Equal(800, (int)saved["WindowLayout"]!["Height"]!);
        }

        [Fact]
        public void TryLoad_MissingOrMalformedFileReturnsFalseWithFreshDefaults()
        {
            using var missingDir = new TempDir();
            var missing = CreatePersistence(missingDir.SettingsPath);
            Assert.False(missing.TryLoad(out UserSettingsGroupSection missingSection));
            Assert.Empty(missingSection.PatchGroupMemberships);
            Assert.Empty(missingSection.PatchGroupModes);
            Assert.Empty(missingSection.PatchGroupEnabledStates);

            using var malformedDir = new TempDir();
            File.WriteAllText(malformedDir.SettingsPath, "{ not valid json");
            var malformed = CreatePersistence(malformedDir.SettingsPath);
            Assert.False(malformed.TryLoad(out UserSettingsGroupSection malformedSection));
            Assert.Empty(malformedSection.PatchGroupMemberships);
            Assert.Empty(malformedSection.PatchGroupModes);
        }

        [Fact]
        public void Save_MalformedFilePropagatesWithoutOverwriting()
        {
            using var dir = new TempDir();
            const string malformed = "{ not valid json";
            File.WriteAllText(dir.SettingsPath, malformed);
            var persistence = CreatePersistence(dir.SettingsPath);

            Assert.ThrowsAny<Exception>(() => persistence.Save(new UserSettingsGroupSection()));
            Assert.Equal(malformed, File.ReadAllText(dir.SettingsPath));
        }

        private static GroupSettingsPersistence CreatePersistence(string path)
            => new(new SettingsSectionStore(path));
    }
}
