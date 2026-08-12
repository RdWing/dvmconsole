// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using DvmConsole.Avalonia.Persistence;
using DvmConsole.Avalonia.ViewModels;
using dvmconsole;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for Gate 4.5 group-indicator projection and card binding.
    /// Persistence is loaded by the test shell and passed to the VM as a DTO.
    /// </summary>
    public sealed class MainWindowGroupIndicatorProjectionTests
    {
        [Fact]
        public void ApplyGroupsSection_ProjectsPatchAndMultiSelectMembershipWithWpfPriority()
        {
            using var settings = new TemporarySettings();
            var persistence = new GroupSettingsPersistence(new SettingsSectionStore(settings.Path));
            persistence.Save(new UserSettingsGroupSection
            {
                PatchGroupMemberships = new Dictionary<string, Dictionary<string, List<PatchTalkgroupMember>>>
                {
                    [settings.Context] = new Dictionary<string, List<PatchTalkgroupMember>>
                    {
                        ["Patch A"] = new List<PatchTalkgroupMember>
                        {
                            new PatchTalkgroupMember { SystemName = "SYS", Tgid = "100" },
                        },
                        ["Select A"] = new List<PatchTalkgroupMember>
                        {
                            new PatchTalkgroupMember { SystemName = "SYS", Tgid = "100" },
                        },
                    },
                },
                PatchGroupEnabledStates = new Dictionary<string, Dictionary<string, bool>>
                {
                    [settings.Context] = new Dictionary<string, bool> { ["Patch A"] = true },
                },
            });

            var viewModel = CreateViewModel(MakeCodeplug());
            viewModel.ApplyGroupsSection(
                LoadSection(persistence),
                settings.Context,
                retainPatchStateOnStartup: true);

            ChannelSlotViewModel slot = Assert.Single(viewModel.Channels);
            Assert.True(slot.IsPatchGroupMember);
            Assert.True(slot.IsPatchGroupActive);
            Assert.True(slot.IsMultiSelectMember);
            Assert.Equal("MSEL", slot.GroupIndicatorText);
            Assert.Equal("Member of the current multi-select group", slot.GroupIndicatorToolTip);
        }

        [Fact]
        public void ApplyGroupsSection_DoesNotRestoreActivePatchWhenRetentionIsDisabled()
        {
            using var settings = new TemporarySettings();
            var persistence = new GroupSettingsPersistence(new SettingsSectionStore(settings.Path));
            persistence.Save(new UserSettingsGroupSection
            {
                PatchGroupMemberships = new Dictionary<string, Dictionary<string, List<PatchTalkgroupMember>>>
                {
                    [settings.Context] = new Dictionary<string, List<PatchTalkgroupMember>>
                    {
                        ["Patch A"] = new List<PatchTalkgroupMember>
                        {
                            new PatchTalkgroupMember { SystemName = "SYS", Tgid = "100" },
                        },
                    },
                },
                PatchGroupEnabledStates = new Dictionary<string, Dictionary<string, bool>>
                {
                    [settings.Context] = new Dictionary<string, bool> { ["Patch A"] = true },
                },
            });

            var viewModel = CreateViewModel(MakeCodeplug());
            viewModel.ApplyGroupsSection(
                LoadSection(persistence),
                settings.Context,
                retainPatchStateOnStartup: false);

            ChannelSlotViewModel slot = Assert.Single(viewModel.Channels);
            Assert.True(slot.IsPatchGroupMember);
            Assert.False(slot.IsPatchGroupActive);
            Assert.Equal("PATCH", slot.GroupIndicatorText);
            Assert.Equal("Member of one or more patch groups", slot.GroupIndicatorToolTip);
        }

        [Fact]
        public void SelectedZoneChange_ReprojectsGroupIndicatorsForNewZone()
        {
            using var settings = new TemporarySettings();
            var persistence = new GroupSettingsPersistence(new SettingsSectionStore(settings.Path));
            persistence.Save(new UserSettingsGroupSection
            {
                PatchGroupMemberships = new Dictionary<string, Dictionary<string, List<PatchTalkgroupMember>>>
                {
                    [settings.Context] = new Dictionary<string, List<PatchTalkgroupMember>>
                    {
                        ["Patch A"] = new List<PatchTalkgroupMember>
                        {
                            new PatchTalkgroupMember { SystemName = "SECOND", Tgid = "200" },
                        },
                    },
                },
                PatchGroupEnabledStates = new Dictionary<string, Dictionary<string, bool>>
                {
                    [settings.Context] = new Dictionary<string, bool> { ["Patch A"] = true },
                },
            });

            var viewModel = CreateViewModel(MakeTwoZoneCodeplug());
            viewModel.ApplyGroupsSection(
                LoadSection(persistence),
                settings.Context,
                retainPatchStateOnStartup: true);
            Assert.False(viewModel.Channels[0].IsPatchGroupMember);

            viewModel.SelectedZone = viewModel.Zones[1];

            Assert.True(viewModel.Channels[0].IsPatchGroupMember);
            Assert.True(viewModel.Channels[0].IsPatchGroupActive);
        }

        [Fact]
        public void EnabledPatchStateChange_ReprojectsActivePatchBadgeWithoutReload()
        {
            using var settings = new TemporarySettings();
            var viewModel = CreateViewModel(MakeCodeplug());
            var member = new PatchTalkgroupMember { SystemName = "SYS", Tgid = "100" };
            var memberships = new Dictionary<string, Dictionary<string, List<PatchTalkgroupMember>>>
            {
                [settings.Context] = new Dictionary<string, List<PatchTalkgroupMember>>
                {
                    ["Patch A"] = new List<PatchTalkgroupMember> { member },
                },
            };

            viewModel.ApplyGroupsSection(
                new UserSettingsGroupSection
                {
                    PatchGroupMemberships = memberships,
                    PatchGroupEnabledStates = new Dictionary<string, Dictionary<string, bool>>
                    {
                        [settings.Context] = new Dictionary<string, bool> { ["Patch A"] = false },
                    },
                },
                settings.Context,
                retainPatchStateOnStartup: true);
            ChannelSlotViewModel slot = Assert.Single(viewModel.Channels);
            Assert.False(slot.IsPatchGroupActive);

            viewModel.ApplyGroupsSection(
                new UserSettingsGroupSection
                {
                    PatchGroupMemberships = memberships,
                    PatchGroupEnabledStates = new Dictionary<string, Dictionary<string, bool>>
                    {
                        [settings.Context] = new Dictionary<string, bool> { ["Patch A"] = true },
                    },
                },
                settings.Context,
                retainPatchStateOnStartup: true);
            Assert.True(slot.IsPatchGroupActive);
            Assert.Equal("PATCH ON", slot.GroupIndicatorText);

            viewModel.ApplyGroupsSection(
                new UserSettingsGroupSection
                {
                    PatchGroupMemberships = memberships,
                    PatchGroupEnabledStates = new Dictionary<string, Dictionary<string, bool>>
                    {
                        [settings.Context] = new Dictionary<string, bool> { ["Patch A"] = false },
                    },
                },
                settings.Context,
                retainPatchStateOnStartup: true);
            Assert.False(slot.IsPatchGroupActive);
            Assert.Equal("PATCH", slot.GroupIndicatorText);
        }

        [Fact]
        public void EnabledPatchMembershipUnion_KeepsBadgeActiveUntilBothGroupsDisable()
        {
            using var settings = new TemporarySettings();
            var viewModel = CreateViewModel(new Codeplug
            {
                Systems = new List<Codeplug.System>(),
                Groups = new List<Codeplug.Group>
                {
                    new() { Name = "Patch A", Type = "patch" },
                    new() { Name = "Patch B", Type = "patch" },
                },
                Zones = new List<Codeplug.Zone>
                {
                    new()
                    {
                        Name = "Zone",
                        Channels = new List<Codeplug.Channel>
                        {
                            new() { Name = "Channel", System = "SYS", Tgid = "100", Mode = "dmr" },
                        },
                    },
                },
            });
            var member = new PatchTalkgroupMember { SystemName = "SYS", Tgid = "100" };
            var memberships = new Dictionary<string, Dictionary<string, List<PatchTalkgroupMember>>>
            {
                [settings.Context] = new Dictionary<string, List<PatchTalkgroupMember>>
                {
                    ["Patch A"] = new List<PatchTalkgroupMember> { member },
                    ["Patch B"] = new List<PatchTalkgroupMember> { member },
                },
            };

            viewModel.ApplyGroupsSection(
                new UserSettingsGroupSection
                {
                    PatchGroupMemberships = memberships,
                    PatchGroupEnabledStates = new Dictionary<string, Dictionary<string, bool>>
                    {
                        [settings.Context] = new Dictionary<string, bool>
                        {
                            ["Patch A"] = true,
                            ["Patch B"] = true,
                        },
                    },
                },
                settings.Context,
                retainPatchStateOnStartup: true);

            ChannelSlotViewModel slot = Assert.Single(viewModel.Channels);
            Assert.True(slot.IsPatchGroupActive);

            viewModel.ApplyGroupsSection(
                new UserSettingsGroupSection
                {
                    PatchGroupMemberships = memberships,
                    PatchGroupEnabledStates = new Dictionary<string, Dictionary<string, bool>>
                    {
                        [settings.Context] = new Dictionary<string, bool> { ["Patch B"] = true },
                    },
                },
                settings.Context,
                retainPatchStateOnStartup: true);

            Assert.True(slot.IsPatchGroupActive);
        }

        [Fact]
        public void ChannelCardTemplate_BindsWpfPriorityGroupIndicatorSurface()
        {
            string path = Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "DvmConsole.Avalonia", "MainWindow.axaml");
            string xaml = File.ReadAllText(Path.GetFullPath(path));

            Assert.Contains("GroupIndicatorText", xaml, StringComparison.Ordinal);
            Assert.Contains("GroupIndicatorToolTip", xaml, StringComparison.Ordinal);
            Assert.Contains("StringConverters.IsNotNullOrEmpty", xaml, StringComparison.Ordinal);
        }

        private static UserSettingsGroupSection LoadSection(GroupSettingsPersistence persistence)
        {
            Assert.True(persistence.TryLoad(out UserSettingsGroupSection section));
            return section;
        }

        private static MainWindowViewModel CreateViewModel(Codeplug codeplug)
            => new(
                systems: null,
                catalog: null,
                hotkeys: null,
                persistence: null,
                vocoderStatus: null,
                codeplug: codeplug,
                callHistory: null,
                tarPersistence: null,
                pttPersistence: null,
                preferencesPersistence: null);

        private static Codeplug MakeCodeplug()
            => new()
            {
                Systems = new List<Codeplug.System>(),
                Groups = new List<Codeplug.Group>
                {
                    new() { Name = "Patch A", Type = "patch" },
                    new() { Name = "Select A", Type = "multiselect" },
                },
                Zones = new List<Codeplug.Zone>
                {
                    new()
                    {
                        Name = "Zone",
                        Channels = new List<Codeplug.Channel>
                        {
                            new() { Name = "Channel", System = "SYS", Tgid = "100", Mode = "dmr" },
                        },
                    },
                },
            };

        private static Codeplug MakeTwoZoneCodeplug()
            => new()
            {
                Systems = new List<Codeplug.System>(),
                Groups = new List<Codeplug.Group>
                {
                    new() { Name = "Patch A", Type = "patch" },
                },
                Zones = new List<Codeplug.Zone>
                {
                    new()
                    {
                        Name = "First",
                        Channels = new List<Codeplug.Channel>
                        {
                            new() { Name = "First", System = "FIRST", Tgid = "100", Mode = "dmr" },
                        },
                    },
                    new()
                    {
                        Name = "Second",
                        Channels = new List<Codeplug.Channel>
                        {
                            new() { Name = "Second", System = "SECOND", Tgid = "200", Mode = "dmr" },
                        },
                    },
                },
            };

        private sealed class TemporarySettings : IDisposable
        {
            private readonly string root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "dvmconsole-group-indicators-" + Guid.NewGuid().ToString("N"));

            public TemporarySettings()
            {
                Directory.CreateDirectory(root);
                Path = System.IO.Path.Combine(root, "UserSettings.json");
                Context = System.IO.Path.Combine(root, "codeplug.yml");
            }

            public string Path { get; }

            public string Context { get; }

            public void Dispose()
            {
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, recursive: true);
                    }
                }
                catch (IOException)
                {
                    // Best-effort cleanup; never mask the test result.
                }
            }
        }
    }
}
