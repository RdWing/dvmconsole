using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ConfigurationStudioOperatorStateTests
{
    [Fact]
    public async Task PatchOperatorStateAppliesImmediatelyWithoutChangingTheYamlDraft()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-studio-operator-state-tests",
            Guid.NewGuid().ToString("N"));
        string settingsPath = Path.Combine(directory, "UserSettings.json");
        string codeplugPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestData",
            "multiple-systems.yml");
        var store = new UserSettingsStore(settingsPath);
        var settings = new UserSettings
        {
            RetainPatchStateOnStartup = true,
            LegacyPatchGroupStateMigrated = true
        };
        settings.CodeplugGroupStates[CodeplugGroupStateStore.NormalizePath(codeplugPath)] = new CodeplugGroupState
        {
            Memberships = new Dictionary<string, List<PatchMemberSetting>>
            {
                ["Dispatch Patch"] =
                [
                    Member("Alpha", 101, "Alpha Dispatch"),
                    Member("Beta", 201, "Beta Dispatch")
                ]
            },
            EnabledStates = new Dictionary<string, bool> { ["Dispatch Patch"] = true }
        };
        Directory.CreateDirectory(directory);
        store.Save(settings);

        string yamlBefore = File.ReadAllText(codeplugPath);
        try
        {
            await using MainWindowViewModel runtime = MainWindowViewModel.Load(codeplugPath, store);
            var studio = new ConfigurationStudioViewModel(
                ConfigurationDocument.Open(codeplugPath),
                runtime,
                store,
                ConfigurationStudioSection.Groups);
            PatchGroupEditorViewModel group = Assert.Single(
                studio.OperationalGroups,
                candidate => candidate.IsPatchGroup);
            PatchMemberEditorViewModel beta = Assert.Single(
                group.Members,
                member => member.Channel.Definition.SystemName == "Beta" && member.IsMember);

            beta.IsMember = false;
            group.IsEnabled = false;
            studio.SetOperationalGroupEnabled(group);
            Assert.Null(studio.ApplyAllOperationalGroups());
            await runtime.FlushUserSettingsAsync();

            UserSettings saved = store.Load();
            CodeplugGroupState savedState = CodeplugGroupStateStore.GetOrMigrate(saved, codeplugPath);
            PatchMemberSetting member = Assert.Single(savedState.Memberships["Dispatch Patch"]);
            Assert.Equal("Alpha", member.SystemName);
            Assert.False(savedState.EnabledStates["Dispatch Patch"]);
            Assert.False(studio.IsDirty);
            Assert.Equal(yamlBefore, File.ReadAllText(codeplugPath));
            Assert.Contains("Saved operator state", runtime.StatusText, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static PatchMemberSetting Member(string system, uint destinationId, string channel)
        => new()
        {
            SystemName = system,
            DestinationId = destinationId,
            ChannelName = channel
        };
}
