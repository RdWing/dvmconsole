// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using DvmConsole.Avalonia.Persistence;
using DvmConsole.Avalonia.ViewModels;
using dvmconsole;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for Gate 3.4a restore selection, primary identity and
    /// selectable-encryption persistence. Hydration must be collection-aware,
    /// valid-identity-only, and write-free.
    /// </summary>
    public sealed class MainWindowRestoreSelectionGateTests
    {
        private sealed class TempDir : IDisposable
        {
            public string Root { get; } = Path.Combine(
                Path.GetTempPath(),
                "dvmconsole-main-window-restore-" + Guid.NewGuid().ToString("N"));

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
        public void AttachRestorePersistence_RestoresValidSelectionAndPrimaryWithoutWriting()
        {
            using var dir = new TempDir();
            var preferences = CreatePreferencesPersistence(dir.SettingsPath);
            preferences.Save(new UserSettingsPreferencesSection
            {
                RestoreSelectedChannelsOnStartup = true,
            });
            var restore = CreateRestorePersistence(dir.SettingsPath);
            restore.Save(new UserSettingsRestoreSection
            {
                SelectedChannels = new List<string>
                {
                    ResourceKey("31001") + "|slot:1",
                    "stale-system|99999",
                },
                PrimaryResourceKey = ResourceKey("31001"),
                SelectableEncryptionStates = new Dictionary<string, bool>
                {
                    [ResourceKey("31001")] = true,
                },
            });
            var before = File.ReadAllBytes(dir.SettingsPath);
            var vm = CreateViewModel();

            vm.AttachPreferencesPersistence(preferences);
            vm.AttachRestorePersistence(restore);

            var first = Assert.Single(vm.SelectedChannels);
            Assert.Equal(ResourceKey("31001"), first.ResourceKey);
            Assert.Same(first, vm.PrimaryChannel);
            Assert.True(first.IsEncryptionSelectable);
            Assert.True(first.IsTxEncrypted);
            Assert.Equal(before, File.ReadAllBytes(dir.SettingsPath));
        }

        [Fact]
        public void AttachRestorePersistence_IgnoresUnknownKeysAndRequiresPrimaryToBeSelected()
        {
            using var dir = new TempDir();
            var preferences = CreatePreferencesPersistence(dir.SettingsPath);
            preferences.Save(new UserSettingsPreferencesSection
            {
                RestoreSelectedChannelsOnStartup = true,
            });
            var restore = CreateRestorePersistence(dir.SettingsPath);
            restore.Save(new UserSettingsRestoreSection
            {
                SelectedChannels = new List<string> { ResourceKey("31001") },
                PrimaryResourceKey = "stale-system|99999",
            });
            var vm = CreateViewModel();

            vm.AttachPreferencesPersistence(preferences);
            vm.AttachRestorePersistence(restore);

            Assert.Single(vm.SelectedChannels);
            Assert.Null(vm.PrimaryChannel);
            Assert.False(vm.Channels[1].IsSelected);
        }

        [Fact]
        public void AttachRestorePersistence_AppliesWpfEncryptionDefaultsPerChannel()
        {
            using var dir = new TempDir();
            var preferences = CreatePreferencesPersistence(dir.SettingsPath);
            preferences.Save(new UserSettingsPreferencesSection
            {
                RestoreSelectedChannelsOnStartup = true,
            });
            var restore = CreateRestorePersistence(dir.SettingsPath);
            restore.Save(new UserSettingsRestoreSection
            {
                SelectedChannels = new List<string>
                {
                    ResourceKey("31001"),
                    ResourceKey("31002"),
                    ResourceKey("31003"),
                },
                SelectableEncryptionStates = new Dictionary<string, bool>
                {
                    [ResourceKey("31001")] = false,
                },
            });
            var vm = CreateViewModel();

            vm.AttachPreferencesPersistence(preferences);
            vm.AttachRestorePersistence(restore);

            Assert.False(vm.Channels[0].IsTxEncrypted);
            Assert.True(vm.Channels[1].IsTxEncrypted);
            Assert.False(vm.Channels[2].IsTxEncrypted);
        }

        [Fact]
        public void PreferenceOff_DoesNotRestoreAndSelectionChangesPersistEmptySelection()
        {
            using var dir = new TempDir();
            var preferences = CreatePreferencesPersistence(dir.SettingsPath);
            preferences.Save(new UserSettingsPreferencesSection
            {
                RestoreSelectedChannelsOnStartup = false,
            });
            var restore = CreateRestorePersistence(dir.SettingsPath);
            restore.Save(new UserSettingsRestoreSection
            {
                SelectedChannels = new List<string> { ResourceKey("31001") },
                PrimaryResourceKey = ResourceKey("31001"),
            });
            var vm = CreateViewModel();

            vm.AttachPreferencesPersistence(preferences);
            vm.AttachRestorePersistence(restore);
            Assert.Empty(vm.SelectedChannels);
            Assert.Null(vm.PrimaryChannel);

            vm.ProcessChannelClick(1, setPrimary: false);

            Assert.True(restore.TryLoad(out UserSettingsRestoreSection saved));
            Assert.Empty(saved.SelectedChannels);
            Assert.Null(saved.PrimaryResourceKey);
        }

        [Fact]
        public void SelectableEncryptionRequest_PersistsStateAndPreservesUnrelatedSections()
        {
            using var dir = new TempDir();
            File.WriteAllText(
                dir.SettingsPath,
                """
                {
                  "FneSystems": [{ "Name": "system-1", "Port": 62031 }],
                  "UnknownScalar": "preserve-me"
                }
                """);
            var preferences = CreatePreferencesPersistence(dir.SettingsPath);
            preferences.Save(new UserSettingsPreferencesSection());
            var restore = CreateRestorePersistence(dir.SettingsPath);
            var vm = CreateViewModel();

            vm.AttachPreferencesPersistence(preferences);
            vm.AttachRestorePersistence(restore);
            var selectable = vm.Channels[0];
            selectable.RequestSelectableEncryption();

            Assert.True(restore.TryLoad(out UserSettingsRestoreSection saved));
            Assert.False(saved.SelectableEncryptionStates[ResourceKey("31001")]);
            var json = JObject.Parse(File.ReadAllText(dir.SettingsPath));
            Assert.Equal("preserve-me", (string)json["UnknownScalar"]!);
            Assert.Equal("system-1", (string)json["FneSystems"]![0]!["Name"]!);
        }

        private MainWindowViewModel CreateViewModel()
            => new(
                null,
                null,
                null,
                null,
                null,
                MakeCodeplug());

        private static PreferencesSettingsPersistence CreatePreferencesPersistence(string path)
            => new(new SettingsSectionStore(path));

        private static RestoreSettingsPersistence CreateRestorePersistence(string path)
            => new(new SettingsSectionStore(path));

        private static string ResourceKey(string talkgroup)
            => ResourceIdentity.Build("Repeater 1", talkgroup);

        private static Codeplug MakeCodeplug()
            => new()
            {
                Systems = new List<Codeplug.System>
                {
                    new Codeplug.System { Name = "Repeater 1", Rid = "1000001" },
                },
                Zones = new List<Codeplug.Zone>
                {
                    new Codeplug.Zone
                    {
                        Name = "Zone A",
                        Channels = new List<Codeplug.Channel>
                        {
                            new Codeplug.Channel
                            {
                                Name = "Selectable",
                                System = "Repeater 1",
                                Tgid = "31001",
                                Slot = 1,
                                Mode = "P25",
                                Algo = "aes",
                                KeyId = "A1",
                                SelectableEncryption = true,
                            },
                            new Codeplug.Channel
                            {
                                Name = "Fixed encrypted",
                                System = "Repeater 1",
                                Tgid = "31002",
                                Slot = 1,
                                Mode = "P25",
                                Algo = "aes",
                                KeyId = "A2",
                            },
                            new Codeplug.Channel
                            {
                                Name = "Clear",
                                System = "Repeater 1",
                                Tgid = "31003",
                                Slot = 1,
                                Mode = "P25",
                                Algo = "none",
                            },
                        },
                    },
                },
            };
    }
}
