using System.Collections.Specialized;
using DvmConsole.Core.Settings;
using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class MainWindowSessionHostTests
{
    [Fact]
    public async Task PreparingReplacementFlushesLatestPatchMembershipBeforeReload()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-session-host-tests",
            Guid.NewGuid().ToString("N"));
        string settingsPath = Path.Combine(directory, "UserSettings.json");
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
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
            }
        });

        MainWindowSessionHost? host = null;
        MainWindowViewModel? replacement = null;
        try
        {
            MainWindowViewModel initial = MainWindowViewModel.Load(codeplugPath, store);
            NotifyCollectionChangedEventHandler historyChanging = (_, _) => { };
            host = new MainWindowSessionHost(
                initial,
                historyChanging,
                _ => { },
                () => { },
                () => { });

            PatchGroupEditorViewModel group = Assert.Single(
                initial.PatchGroups,
                candidate => candidate.IsPatchGroup);
            PatchMemberEditorViewModel beta = Assert.Single(
                group.Members,
                member => member.IsMember && member.Channel.Definition.SystemName == "Beta");
            beta.IsMember = false;
            initial.ApplyPatchGroup(group);

            await host.PrepareForReplacementAsync();
            replacement = MainWindowViewModel.Load(codeplugPath, store);

            PatchGroupEditorViewModel reloaded = Assert.Single(
                replacement.PatchGroups,
                candidate => candidate.IsPatchGroup);
            PatchMemberEditorViewModel selected = Assert.Single(
                reloaded.Members,
                member => member.IsMember);
            Assert.Equal("Alpha", selected.Channel.Definition.SystemName);

            await host.ReplaceAsync(replacement);
            replacement = null;
        }
        finally
        {
            if (replacement is not null)
                await replacement.DisposeAsync();
            if (host is not null)
                await host.DisposeAsync();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
