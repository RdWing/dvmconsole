using DvmConsole.Core.Settings;
using DvmConsole.Desktop;
using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed partial class SystemViewModelTests
{
    [Fact]
    public async Task RestoresOnlyEnabledPatchStateForConfiguredPatchGroups()
    {
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();
        var store = new UserSettingsStore(settingsPath);
        store.Save(new UserSettings
        {
            RetainPatchStateOnStartup = true,
            PatchGroupMemberships = new Dictionary<string, List<PatchMemberSetting>>
            {
                ["Dispatch Patch"] =
                [
                    new PatchMemberSetting { SystemName = "Alpha", DestinationId = 101 },
                    new PatchMemberSetting { SystemName = "Beta", DestinationId = 201 }
                ],
                ["Operations Select"] =
                [
                    new PatchMemberSetting { SystemName = "Alpha", DestinationId = 103 },
                    new PatchMemberSetting { SystemName = "Beta", DestinationId = 202 }
                ]
            },
            PatchGroupEnabledStates = new Dictionary<string, bool>
            {
                ["Dispatch Patch"] = true,
                ["Operations Select"] = true
            }
        });

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(codeplugPath, store);
            Assert.Equal(["Dispatch Patch"], viewModel.PatchGroupNames);
            PatchGroupEditorViewModel group = Assert.Single(viewModel.PatchGroups, candidate => candidate.IsPatchGroup);
            Assert.True(group.IsEnabled);
            Assert.Equal(
                ["Alpha Dispatch", "Beta Dispatch"],
                group.Members.Where(member => member.IsMember).Select(member => member.Channel.Name));
            PatchGroupEditorViewModel multiSelect = Assert.Single(viewModel.PatchGroups, candidate => candidate.IsMultiSelect);
            Assert.True(multiSelect.IsEnabled);
            Assert.Equal(
                ["Alpha Emergency", "Beta Operations"],
                multiSelect.Members.Where(member => member.IsMember).Select(member => member.Channel.Name));
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task OneWayPatchSourceCanBeSelectedAndPersistsFirst()
    {
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();
        var store = new UserSettingsStore(settingsPath);
        store.Save(new UserSettings
        {
            PatchGroupMemberships = new Dictionary<string, List<PatchMemberSetting>>
            {
                ["Dispatch Patch"] =
                [
                    new PatchMemberSetting { SystemName = "Alpha", DestinationId = 101 },
                    new PatchMemberSetting { SystemName = "Beta", DestinationId = 201 }
                ]
            },
            PatchGroupModes = new Dictionary<string, bool> { ["Dispatch Patch"] = true }
        });

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(codeplugPath, store);
            PatchGroupEditorViewModel group = Assert.Single(viewModel.PatchGroups, candidate => candidate.IsPatchGroup);
            PatchMemberEditorViewModel beta = Assert.Single(
                group.SourceOptions,
                member => member.Channel.SystemName == "Beta");
            Assert.Equal("Alpha", group.SelectedSource?.Channel.SystemName);
            Assert.Equal("Edit members (2 selected)", group.MemberEditorHeader);

            group.SelectedSource = beta;
            viewModel.ApplyPatchGroup(group);
            await viewModel.FlushUserSettingsAsync();

            UserSettings saved = store.Load();
            CodeplugGroupState scoped = CodeplugGroupStateStore.GetOrMigrate(saved, codeplugPath);
            Assert.Equal("Beta", scoped.Memberships["Dispatch Patch"][0].SystemName);
            Assert.Contains("1 destination: Alpha Dispatch", group.OneWayDestinationSummary);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task PatchMembershipPersistsAndRestoresExactChannelIdentity()
    {
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "duplicate-patch-identities.yml");
        string settingsPath = CreateSettingsPath();
        var store = new UserSettingsStore(settingsPath);

        try
        {
            await using (MainWindowViewModel viewModel = MainWindowViewModel.Load(codeplugPath, store))
            {
                PatchGroupEditorViewModel group = Assert.Single(viewModel.PatchGroups);
                PatchMemberEditorViewModel source = Assert.Single(group.Members, member => member.Channel.Name == "P25 Source");
                PatchMemberEditorViewModel dmrTarget = Assert.Single(group.Members, member => member.Channel.Name == "DMR 99");
                source.IsMember = true;
                dmrTarget.IsMember = true;
                group.IsEnabled = true;
                viewModel.ApplyPatchGroup(group);
                await viewModel.FlushUserSettingsAsync();
            }

            UserSettings saved = store.Load();
            CodeplugGroupState scoped = CodeplugGroupStateStore.GetOrMigrate(saved, codeplugPath);
            Assert.Equal(
                ["P25 Source", "DMR 99"],
                scoped.Memberships["Cross Mode"].Select(member => member.ChannelName));
            await using MainWindowViewModel restored = MainWindowViewModel.Load(codeplugPath, store);
            PatchGroupEditorViewModel restoredGroup = Assert.Single(restored.PatchGroups);
            Assert.Equal(
                ["P25 Source", "DMR 99"],
                restoredGroup.Members.Where(member => member.IsMember).Select(member => member.Channel.Name));
            Assert.DoesNotContain(
                restoredGroup.Members,
                member => member.IsMember && member.Channel.Name == "P25 99");
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task SurfacesOverlappingPatchAndMultiSelectMemberships()
    {
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();
        var store = new UserSettingsStore(settingsPath);
        store.Save(new UserSettings
        {
            PatchGroupMemberships = new Dictionary<string, List<PatchMemberSetting>>
            {
                ["Dispatch Patch"] =
                [
                    new PatchMemberSetting { SystemName = "Alpha", DestinationId = 101 },
                    new PatchMemberSetting { SystemName = "Beta", DestinationId = 201 }
                ],
                ["Operations Select"] =
                [
                    new PatchMemberSetting { SystemName = "Alpha", DestinationId = 101 },
                    new PatchMemberSetting { SystemName = "Beta", DestinationId = 202 }
                ]
            }
        });

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(codeplugPath, store);
            Assert.All(viewModel.PatchGroups, group => Assert.True(group.HasConflicts));
            PatchGroupEditorViewModel patch = Assert.Single(viewModel.PatchGroups, group => group.IsPatchGroup);
            PatchMemberEditorViewModel overlappingMember = Assert.Single(
                patch.Members,
                member => member.IsMember && member.HasConflict);
            Assert.Contains("Operations Select", overlappingMember.ConflictText);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }
}
