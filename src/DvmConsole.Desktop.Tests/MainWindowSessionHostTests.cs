using System.Collections.Specialized;
using DvmConsole.Core.Settings;
using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class MainWindowSessionHostTests
{
    [Fact]
    public async Task ReplacementQuiescesOutgoingFneBeforePublishingNewSession()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-session-host-tests",
            Guid.NewGuid().ToString("N"));
        var store = new UserSettingsStore(Path.Combine(directory, "UserSettings.json"));
        var initial = new MainWindowViewModel(
            "Initial session",
            [],
            [],
            CreateOptions(store));
        MainWindowViewModel? replacement = new(
            "Replacement session",
            [],
            [],
            CreateOptions(store));
        bool outgoingQuiesced = false;
        bool? outgoingQuiescedAtPublication = null;
        MainWindowSessionHost? host = null;

        try
        {
            host = new MainWindowSessionHost(
                initial,
                (_, _) => { },
                candidate =>
                {
                    if (ReferenceEquals(candidate, replacement))
                        outgoingQuiescedAtPublication = outgoingQuiesced;
                },
                () => { },
                () => { },
                (candidate, _) =>
                {
                    if (ReferenceEquals(initial, candidate))
                        outgoingQuiesced = true;
                    return Task.CompletedTask;
                });

            await host.ReplaceAsync(replacement);
            replacement = null;

            Assert.True(outgoingQuiescedAtPublication);
        }
        finally
        {
            if (replacement is not null)
                await replacement.DisposeAsync();
            if (host is not null)
                await host.DisposeAsync();
            else
                await initial.DisposeAsync();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ShutdownQuiescesFneBeforeClosingSessionWindows()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-session-host-tests",
            Guid.NewGuid().ToString("N"));
        var store = new UserSettingsStore(Path.Combine(directory, "UserSettings.json"));
        var viewModel = new MainWindowViewModel(
            "Initial session",
            [],
            [],
            CreateOptions(store));
        var operations = new List<string>();
        var host = new MainWindowSessionHost(
            viewModel,
            (_, _) => { },
            _ => { },
            () => { },
            () => operations.Add("close-windows"),
            (candidate, _) =>
            {
                Assert.Same(viewModel, candidate);
                operations.Add("quiesce-fne");
                return Task.CompletedTask;
            });

        try
        {
            await host.DisposeAsync();

            Assert.Equal(["quiesce-fne", "close-windows"], operations);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

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

    private static MainWindowViewModelOptions CreateOptions(UserSettingsStore store)
        => new(
            UserSettingsStore: store,
            SerialPortProvider: () => [],
            UiDispatcher: ImmediateUiDispatcher.Instance,
            NetworkDisabledDemo: true);

    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public static ImmediateUiDispatcher Instance { get; } = new();

        public bool CheckAccess() => true;

        public void Post(Action action, bool background = false)
            => action();

        public ValueTask InvokeAsync(Action action)
        {
            action();
            return ValueTask.CompletedTask;
        }
    }
}
