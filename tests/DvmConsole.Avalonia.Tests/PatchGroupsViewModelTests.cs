// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DvmConsole.Avalonia.Persistence;
using DvmConsole.Avalonia.ViewModels;
using dvmconsole;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for the headless Gate 4.2a Groups editor state. The
    /// owner-bound Avalonia window, PatchManager receive lifecycle, and actual
    /// transmit/runtime routing remain later seams.
    /// </summary>
    public sealed class PatchGroupsViewModelTests
    {
        private sealed class TempDir : IDisposable
        {
            public string Root { get; } = Path.Combine(
                Path.GetTempPath(),
                "dvmconsole-avalonia-patch-groups-" + Guid.NewGuid().ToString("N"));

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
        public void ApiShape_IsSealedAndExposesRequestOnlyEditorSurface()
        {
            var type = typeof(PatchGroupsViewModel);

            Assert.True(type.IsPublic);
            Assert.True(type.IsSealed);
            Assert.NotNull(type.GetConstructor(new[]
            {
                typeof(IReadOnlyList<Codeplug.Group>),
                typeof(IReadOnlyList<Codeplug.Channel>),
                typeof(GroupSettingsPersistence),
                typeof(string),
                typeof(bool)
            }));
            Assert.NotNull(type.GetProperty(nameof(PatchGroupsViewModel.Groups)));
            Assert.NotNull(type.GetProperty(nameof(PatchGroupsViewModel.IsAnyGroupEditing)));
            Assert.NotNull(type.GetEvent(nameof(PatchGroupsViewModel.SaveRequested)));
            Assert.NotNull(type.GetEvent(nameof(PatchGroupsViewModel.PttRequested)));
        }

        [Fact]
        public void Constructor_ProjectsImmutableCodeplugGroupsAndLoadsPersistedMembers()
        {
            using var dir = new TempDir();
            var persistence = CreatePersistence(dir.SettingsPath);
            persistence.Save(new UserSettingsGroupSection
            {
                PatchGroupMemberships = new Dictionary<string, Dictionary<string, List<PatchTalkgroupMember>>>
                {
                    ["codeplug-a"] = new Dictionary<string, List<PatchTalkgroupMember>>
                    {
                        ["Patch A"] = new List<PatchTalkgroupMember>
                        {
                            new PatchTalkgroupMember { SystemName = "SYS-2", Tgid = "200" },
                            new PatchTalkgroupMember { SystemName = "SYS-1", Tgid = "100" }
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
            });

            var groups = new List<Codeplug.Group>
            {
                new Codeplug.Group { Name = "Patch A", Type = "patch" },
                new Codeplug.Group { Name = "Select A", Type = "multiselect" }
            };
            var channels = Channels();
            var vm = new PatchGroupsViewModel(groups, channels, persistence, "codeplug-a", retainPatchStateOnStartup: true);

            Assert.Equal(2, vm.Groups.Count);
            Assert.Equal("Patch A", vm.Groups[0].Name);
            Assert.True(vm.Groups[0].IsPatchGroup);
            Assert.False(vm.Groups[1].IsPatchGroup);
            Assert.Equal(new[] { "Channel 2", "Channel 1" }, vm.Groups[0].Members.Select(m => m.ChannelName));
            Assert.True(vm.Groups[0].IsOneWay);
            Assert.True(vm.Groups[0].IsEnabled);
            Assert.True(vm.Groups[1].IsEnabled); // multi-select is always active

            groups[0].Name = "Mutated outside editor";
            groups[0].Type = "multiselect";
            Assert.Equal("Patch A", vm.Groups[0].Name);
            Assert.True(vm.Groups[0].IsPatchGroup);
        }

        [Fact]
        public void Editing_AddRemoveAndMovePreservesCanonicalIdentityAndOrder()
        {
            var vm = CreateViewModel();
            Assert.True(vm.EnterEdit("Patch A"));
            Assert.True(vm.AddMember("Patch A", "SYS-1", "100"));
            Assert.True(vm.AddMember("Patch A", "SYS-2", "200"));

            Assert.True(vm.AddMember("Patch A", " sys-3 ", "300"));
            Assert.False(vm.AddMember("Patch A", "SYS-3", "300"));
            Assert.True(vm.MoveMember("Patch A", 2, 0));
            Assert.True(vm.RemoveMember("Patch A", 1));

            var members = vm.Groups[0].Members;
            Assert.Equal(new[] { "Channel 3", "Channel 2" }, members.Select(m => m.ChannelName));
            Assert.Equal("SYS-3", members[0].SystemName);
            Assert.Equal("300", members[0].Tgid);
            Assert.False(vm.AddMember("Patch A", "missing", "999"));
        }

        [Fact]
        public void Editing_IsExclusiveAndCloseExitsEveryEditMode()
        {
            var vm = CreateViewModel();

            Assert.True(vm.EnterEdit("Patch A"));
            Assert.True(vm.IsAnyGroupEditing);
            Assert.True(vm.Groups[0].IsEditing);
            Assert.False(vm.Groups[1].IsEditing);

            Assert.True(vm.EnterEdit("Select A"));
            Assert.False(vm.Groups[0].IsEditing);
            Assert.True(vm.Groups[1].IsEditing);
            Assert.True(vm.IsAnyGroupEditing);

            vm.Close();
            Assert.False(vm.IsAnyGroupEditing);
            Assert.All(vm.Groups, group => Assert.False(group.IsEditing));
        }

        [Fact]
        public void Ptt_IsBlockedWhileEditingAndRaisesOnlyRequestEvents()
        {
            var vm = CreateViewModel();
            var requests = new List<(string GroupName, bool IsActive)>();
            vm.PttRequested += (groupName, isActive, _) => requests.Add((groupName, isActive));

            SeedPatchMembers(vm);
            Assert.True(vm.EnterEdit("Patch A"));
            Assert.False(vm.RequestPtt("Patch A"));
            Assert.Empty(requests);

            Assert.True(vm.ExitEdit("Patch A"));
            Assert.True(vm.SetEnabled("Patch A", true));
            Assert.True(vm.RequestPtt("Patch A"));
            Assert.True(vm.Groups[0].IsPttActive);
            Assert.Single(requests);
            Assert.Equal(("Patch A", true), requests[0]);

            Assert.True(vm.RequestPtt("Patch A"));
            Assert.False(vm.Groups[0].IsPttActive);
            Assert.Equal(("Patch A", false), requests[1]);
        }

        [Fact]
        public void DisabledPatchBlocksPttButMultiSelectRemainsRequestable()
        {
            var vm = CreateViewModel();
            var requests = new List<string>();
            vm.PttRequested += (groupName, isActive, _) =>
            {
                if (isActive)
                {
                    requests.Add(groupName);
                }
            };

            SeedPatchMembers(vm);
            Assert.True(vm.SetEnabled("Patch A", true));
            Assert.True(vm.SetEnabled("Patch A", false));
            Assert.False(vm.RequestPtt("Patch A"));
            SeedMultiSelectMembers(vm);
            Assert.True(vm.RequestPtt("Select A"));
            Assert.Equal(new[] { "Select A" }, requests);
        }

        [Fact]
        public void CommitRaisesOneMergePreservingSaveRequestWithOrderedState()
        {
            using var dir = new TempDir();
            var persistence = CreatePersistence(dir.SettingsPath);
            persistence.Save(new UserSettingsGroupSection
            {
                PatchGroupMemberships = new Dictionary<string, Dictionary<string, List<PatchTalkgroupMember>>>
                {
                    ["codeplug-a"] = new Dictionary<string, List<PatchTalkgroupMember>>
                    {
                        ["Patch A"] = new List<PatchTalkgroupMember>
                        {
                            new PatchTalkgroupMember { SystemName = "SYS-1", Tgid = "100" },
                            new PatchTalkgroupMember { SystemName = "SYS-2", Tgid = "200" }
                        }
                    },
                    ["other-context"] = new Dictionary<string, List<PatchTalkgroupMember>>
                    {
                        ["Other"] = new List<PatchTalkgroupMember>
                        {
                            new PatchTalkgroupMember { SystemName = "OTHER", Tgid = "9" }
                        }
                    }
                }
            });

            var vm = new PatchGroupsViewModel(Definitions(), Channels(), persistence, "codeplug-a", retainPatchStateOnStartup: false);
            Assert.True(vm.EnterEdit("Patch A"));
            Assert.True(vm.AddMember("Patch A", "SYS-3", "300"));
            Assert.True(vm.SetOneWay("Patch A", true));
            Assert.True(vm.SetEnabled("Patch A", true));
            Assert.True(vm.ExitEdit("Patch A"));

            UserSettingsGroupSection? saved = null;
            var requestCount = 0;
            vm.SaveRequested += section =>
            {
                requestCount++;
                saved = section;
            };

            vm.Commit();

            Assert.Equal(1, requestCount);
            Assert.NotNull(saved);
            Assert.Equal("OTHER", saved!.PatchGroupMemberships["other-context"]["Other"][0].SystemName);
            Assert.Equal(
                new[] { "100", "200", "300" },
                saved.PatchGroupMemberships["codeplug-a"]["Patch A"].Select(member => member.Tgid));
            Assert.True(saved.PatchGroupModes["codeplug-a"]["Patch A"]);
            Assert.True(saved.PatchGroupEnabledStates["codeplug-a"]["Patch A"]);
        }

        [Fact]
        public void Close_ReleasesActivePttAndEmitsRequestOnlyStop()
        {
            var vm = CreateViewModel();
            var requests = new List<(string GroupName, bool IsActive)>();
            vm.PttRequested += (groupName, isActive, _) => requests.Add((groupName, isActive));

            SeedPatchMembers(vm);
            Assert.True(vm.SetEnabled("Patch A", true));
            Assert.True(vm.RequestPtt("Patch A"));
            vm.Close();

            Assert.False(vm.Groups[0].IsPttActive);
            Assert.Equal(new[] { ("Patch A", true), ("Patch A", false) }, requests);
        }

        private static PatchGroupsViewModel CreateViewModel()
            => new(Definitions(), Channels(), persistence: null, membershipContextKey: "codeplug-a", retainPatchStateOnStartup: false);

        private static void SeedPatchMembers(PatchGroupsViewModel vm)
        {
            Assert.True(vm.EnterEdit("Patch A"));
            Assert.True(vm.AddMember("Patch A", "SYS-1", "100"));
            Assert.True(vm.AddMember("Patch A", "SYS-2", "200"));
            Assert.True(vm.ExitEdit("Patch A"));
        }

        private static void SeedMultiSelectMembers(PatchGroupsViewModel vm)
        {
            Assert.True(vm.EnterEdit("Select A"));
            Assert.True(vm.AddMember("Select A", "SYS-1", "100"));
            Assert.True(vm.ExitEdit("Select A"));
        }

        private static GroupSettingsPersistence CreatePersistence(string path)
            => new(new SettingsSectionStore(path));

        private static IReadOnlyList<Codeplug.Group> Definitions()
            => new List<Codeplug.Group>
            {
                new Codeplug.Group { Name = "Patch A", Type = "patch" },
                new Codeplug.Group { Name = "Select A", Type = "multiselect" }
            };

        private static IReadOnlyList<Codeplug.Channel> Channels()
            => new List<Codeplug.Channel>
            {
                new Codeplug.Channel { Name = "Channel 1", System = "SYS-1", Tgid = "100" },
                new Codeplug.Channel { Name = "Channel 2", System = "SYS-2", Tgid = "200" },
                new Codeplug.Channel { Name = "Channel 3", System = "SYS-3", Tgid = "300" }
            };
    }
}
