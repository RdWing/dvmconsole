// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dvmconsole;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DvmConsole.Core.Tests
{
    /// <summary>
    /// RED contract for the WPF-compatible groups and patches settings section.
    /// Persistence preserves raw member values and member order; runtime identity
    /// normalization remains owned by PatchManager.
    /// </summary>
    public sealed class UserSettingsGroupSectionTests
    {
        private sealed class TempDir : IDisposable
        {
            public string Root { get; } = Path.Combine(
                Path.GetTempPath(),
                "dvmconsole-groups-section-" + Guid.NewGuid().ToString("N"));

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
        public void Type_IsPublicSealedWithExactWpfGroupProperties()
        {
            var type = typeof(UserSettingsGroupSection);
            var properties = type.GetProperties();

            Assert.Equal("dvmconsole", type.Namespace);
            Assert.True(type.IsPublic);
            Assert.True(type.IsSealed);
            Assert.Equal(
                new[]
                {
                    nameof(UserSettingsGroupSection.PatchGroupMemberships),
                    nameof(UserSettingsGroupSection.PatchGroupModes),
                    nameof(UserSettingsGroupSection.PatchGroupEnabledStates),
                },
                properties.Select(property => property.Name));
            Assert.Equal(
                typeof(Dictionary<string, Dictionary<string, List<PatchTalkgroupMember>>>),
                properties[0].PropertyType);
            Assert.Equal(
                typeof(Dictionary<string, Dictionary<string, bool>>),
                properties[1].PropertyType);
            Assert.Equal(
                typeof(Dictionary<string, Dictionary<string, bool>>),
                properties[2].PropertyType);
            Assert.All(properties, property => Assert.True(property.SetMethod!.IsPublic));
        }

        [Fact]
        public void Defaults_AreFreshEmptyMaps()
        {
            var first = new UserSettingsGroupSection();
            var second = new UserSettingsGroupSection();

            Assert.Empty(first.PatchGroupMemberships);
            Assert.Empty(first.PatchGroupModes);
            Assert.Empty(first.PatchGroupEnabledStates);
            Assert.NotSame(first.PatchGroupMemberships, second.PatchGroupMemberships);
            Assert.NotSame(first.PatchGroupModes, second.PatchGroupModes);
            Assert.NotSame(first.PatchGroupEnabledStates, second.PatchGroupEnabledStates);
        }

        [Fact]
        public void JsonShape_UsesExactWpfPropertyNamesAndNestedMemberShape()
        {
            var section = new UserSettingsGroupSection
            {
                PatchGroupMemberships = new Dictionary<string, Dictionary<string, List<PatchTalkgroupMember>>>
                {
                    ["codeplug-a"] = new Dictionary<string, List<PatchTalkgroupMember>>
                    {
                        ["Patch A"] = new List<PatchTalkgroupMember>
                        {
                            new PatchTalkgroupMember { SystemName = "System 1", Tgid = "123" }
                        }
                    }
                },
                PatchGroupModes = new Dictionary<string, Dictionary<string, bool>>
                {
                    ["codeplug-a"] = new Dictionary<string, bool> { ["Patch A"] = true }
                },
                PatchGroupEnabledStates = new Dictionary<string, Dictionary<string, bool>>
                {
                    ["codeplug-a"] = new Dictionary<string, bool> { ["Patch A"] = false }
                }
            };

            var json = JObject.FromObject(section);

            Assert.Equal(
                new[]
                {
                    nameof(UserSettingsGroupSection.PatchGroupMemberships),
                    nameof(UserSettingsGroupSection.PatchGroupModes),
                    nameof(UserSettingsGroupSection.PatchGroupEnabledStates),
                },
                json.Properties().Select(property => property.Name));
            Assert.Equal("System 1", (string)json[nameof(UserSettingsGroupSection.PatchGroupMemberships)]!["codeplug-a"]!["Patch A"]![0]![nameof(PatchTalkgroupMember.SystemName)]!);
            Assert.Equal("123", (string)json[nameof(UserSettingsGroupSection.PatchGroupMemberships)]!["codeplug-a"]!["Patch A"]![0]![nameof(PatchTalkgroupMember.Tgid)]!);
            Assert.Null(json["$type"]);
        }

        [Fact]
        public void Store_RoundTripsOrderedMembersModesAndEnabledStatesWithoutNormalization()
        {
            using var dir = new TempDir();
            var expected = new UserSettingsGroupSection
            {
                PatchGroupMemberships = new Dictionary<string, Dictionary<string, List<PatchTalkgroupMember>>>
                {
                    [" Codeplug A "] = new Dictionary<string, List<PatchTalkgroupMember>>
                    {
                        ["Patch A"] = new List<PatchTalkgroupMember>
                        {
                            new PatchTalkgroupMember { SystemName = " System One ", Tgid = " 123 " },
                            new PatchTalkgroupMember { SystemName = "System Two", Tgid = "456" },
                        }
                    }
                },
                PatchGroupModes = new Dictionary<string, Dictionary<string, bool>>
                {
                    [" Codeplug A "] = new Dictionary<string, bool>
                    {
                        ["Patch A"] = true,
                        ["Multi A"] = false,
                    }
                },
                PatchGroupEnabledStates = new Dictionary<string, Dictionary<string, bool>>
                {
                    [" Codeplug A "] = new Dictionary<string, bool>
                    {
                        ["Patch A"] = true,
                        ["Multi A"] = false,
                    }
                }
            };
            var store = new SettingsSectionStore(dir.SettingsPath);

            store.SaveSection(expected);

            Assert.True(store.TryLoadSection(out UserSettingsGroupSection actual));
            Assert.Equal(" Codeplug A ", actual.PatchGroupMemberships.Keys.Single());
            Assert.Equal(" System One ", actual.PatchGroupMemberships[" Codeplug A "]["Patch A"][0].SystemName);
            Assert.Equal(" 123 ", actual.PatchGroupMemberships[" Codeplug A "]["Patch A"][0].Tgid);
            Assert.Equal("System Two", actual.PatchGroupMemberships[" Codeplug A "]["Patch A"][1].SystemName);
            Assert.Equal(new[] { "Patch A", "Multi A" }, actual.PatchGroupModes[" Codeplug A "].Keys);
            Assert.True(actual.PatchGroupModes[" Codeplug A "]["Patch A"]);
            Assert.False(actual.PatchGroupEnabledStates[" Codeplug A "]["Multi A"]);
        }

        [Fact]
        public void Store_PreservesUnrelatedSettingsValues()
        {
            using var dir = new TempDir();
            File.WriteAllText(
                dir.SettingsPath,
                """
                {
                  "PatchGroupMemberships": {},
                  "FneSystems": [{ "Name": "system-1", "Port": 62031 }],
                  "WindowLayout": { "Width": 1200, "Height": 800 },
                  "UnknownScalar": "preserve-me"
                }
                """);
            var store = new SettingsSectionStore(dir.SettingsPath);

            store.SaveSection(new UserSettingsGroupSection
            {
                PatchGroupModes = new Dictionary<string, Dictionary<string, bool>>
                {
                    ["codeplug-a"] = new Dictionary<string, bool> { ["Patch A"] = true }
                }
            });

            var saved = JObject.Parse(File.ReadAllText(dir.SettingsPath));
            Assert.Equal("preserve-me", (string)saved["UnknownScalar"]!);
            Assert.Equal("system-1", (string)saved["FneSystems"]![0]!["Name"]!);
            Assert.Equal(62031, (int)saved["FneSystems"]![0]!["Port"]!);
            Assert.Equal(1200, (int)saved["WindowLayout"]!["Width"]!);
            Assert.Equal(800, (int)saved["WindowLayout"]!["Height"]!);
        }

        [Fact]
        public void TryLoad_MissingMalformedOrPartialFileUsesFreshDefaultsAndPresentKeys()
        {
            using var missingDir = new TempDir();
            var missing = new SettingsSectionStore(missingDir.SettingsPath);
            Assert.False(missing.TryLoadSection(out UserSettingsGroupSection missingSection));
            Assert.Empty(missingSection.PatchGroupMemberships);
            Assert.Empty(missingSection.PatchGroupModes);
            Assert.Empty(missingSection.PatchGroupEnabledStates);

            using var malformedDir = new TempDir();
            File.WriteAllText(malformedDir.SettingsPath, "{ not valid json");
            var malformed = new SettingsSectionStore(malformedDir.SettingsPath);
            Assert.False(malformed.TryLoadSection(out UserSettingsGroupSection malformedSection));
            Assert.Empty(malformedSection.PatchGroupMemberships);

            using var partialDir = new TempDir();
            File.WriteAllText(
                partialDir.SettingsPath,
                "{ \"PatchGroupModes\": { \"codeplug-a\": { \"Patch A\": true } } }");
            var partial = new SettingsSectionStore(partialDir.SettingsPath);
            Assert.True(partial.TryLoadSection(out UserSettingsGroupSection partialSection));
            Assert.True(partialSection.PatchGroupModes["codeplug-a"]["Patch A"]);
            Assert.Empty(partialSection.PatchGroupMemberships);
            Assert.Empty(partialSection.PatchGroupEnabledStates);
        }

        [Fact]
        public void Save_MalformedFilePropagatesWithoutOverwriting()
        {
            using var dir = new TempDir();
            const string malformed = "{ not valid json";
            File.WriteAllText(dir.SettingsPath, malformed);
            var store = new SettingsSectionStore(dir.SettingsPath);

            Assert.ThrowsAny<Exception>(() => store.SaveSection(new UserSettingsGroupSection()));
            Assert.Equal(malformed, File.ReadAllText(dir.SettingsPath));
        }
    }
}
