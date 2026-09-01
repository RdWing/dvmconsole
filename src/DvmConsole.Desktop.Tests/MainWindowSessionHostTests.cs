using System.Collections.Specialized;
using DvmConsole.Application;
using DvmConsole.Core.Settings;
using DvmConsole.Desktop;
using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class MainWindowSessionHostTests
{
    [Fact]
    public async Task ListCommandsUseTheDesktopSelectionWorkflowAndPersistTxSelection()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-session-host-tests",
            Guid.NewGuid().ToString("N"));
        string settingsPath = Path.Combine(directory, "UserSettings.json");
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        var store = new UserSettingsStore(settingsPath);
        MainWindowSessionHost? host = null;

        try
        {
            MainWindowViewModel viewModel = MainWindowViewModel.Load(
                codeplugPath,
                store,
                serialPortProvider: static () => [],
                networkDisabledDemo: true);
            host = new MainWindowSessionHost(viewModel, (_, _) => { }, _ => { }, () => { }, () => { });
            ChannelViewModel channel = viewModel.Systems
                .SelectMany(system => system.Channels)
                .First(candidate => candidate.CanTransmit);
            var channelId = new ChannelId(channel.SessionId);

            await host.ApplicationSession.Commands.SetTransmitSelectedAsync(channelId, true);
            await host.ApplicationSession.Commands.SetPageSelectedAsync(channelId, true);
            await host.ApplicationSession.Commands.SetAlertSelectedAsync(channelId, true);
            await host.ApplicationSession.FlushSettingsAsync(CancellationToken.None);

            Assert.True(channel.IsTransmitSelected);
            Assert.True(channel.IsPageSelected);
            Assert.True(channel.IsAlertSelected);
            Assert.Equal($"{channel.Name} armed for DTMF and alert tones.", viewModel.TransmitStatusText);
            Assert.Contains(channel.SettingsKey, store.Load().TransmitSelectedChannelKeys);
        }
        finally
        {
            if (host is not null)
                await host.DisposeAsync();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PttUsesTheCommandResultWithoutWaitingForAnAsynchronousSnapshot()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-session-host-tests",
            Guid.NewGuid().ToString("N"));
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        var store = new UserSettingsStore(Path.Combine(directory, "UserSettings.json"));
        MainWindowSessionHost? host = null;

        try
        {
            MainWindowViewModel viewModel = MainWindowViewModel.Load(
                codeplugPath,
                store,
                serialPortProvider: static () => [],
                networkDisabledDemo: true);
            host = new MainWindowSessionHost(viewModel, (_, _) => { }, _ => { }, () => { }, () => { });
            ChannelViewModel channel = viewModel.Systems
                .SelectMany(system => system.Channels)
                .First(candidate => candidate.CanTransmit);
            var channelId = new ChannelId(channel.SessionId);
            channel.SetTransmitEnabled(true, streamId: 42);

            await host.ChannelPtt.PressAsync(channelId);
            Assert.True(channel.IsTransmitting);
            await host.ChannelPtt.ReleaseAsync(channelId);

            Assert.False(channel.IsTransmitting);
        }
        finally
        {
            if (host is not null)
                await host.DisposeAsync();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SavingWindowPlacementFlushesTheExactSizeBeforeShutdownContinues()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-session-host-tests",
            Guid.NewGuid().ToString("N"));
        var store = new UserSettingsStore(Path.Combine(directory, "UserSettings.json"));
        var viewModel = new MainWindowViewModel("Placement session", [], [], CreateOptions(store));

        try
        {
            await viewModel.SaveMainWindowPlacementAsync(new WindowPlacementSetting
            {
                Left = 141,
                Top = 82,
                Width = 1187,
                Height = 743
            });

            WindowPlacementSetting saved = store.Load().MainWindowPlacement;
            Assert.Equal(141, saved.Left);
            Assert.Equal(82, saved.Top);
            Assert.Equal(1187, saved.Width);
            Assert.Equal(743, saved.Height);
        }
        finally
        {
            await viewModel.DisposeAsync();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

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
                member => member.IsMember && member.Channel.SystemName == "Beta");
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
            Assert.Equal("Alpha", selected.Channel.SystemName);

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

    [Fact]
    public async Task DeactivationFlushWaitsForSessionReplacement()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-session-host-tests",
            Guid.NewGuid().ToString("N"));
        var store = new UserSettingsStore(Path.Combine(directory, "UserSettings.json"));
        var initial = new MainWindowViewModel("Initial session", [], [], CreateOptions(store));
        MainWindowViewModel? replacement = new(
            "Replacement session",
            [],
            [],
            CreateOptions(store));
        var quiesceEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowReplacement = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        MainWindowSessionHost? host = null;

        try
        {
            host = new MainWindowSessionHost(
                initial,
                (_, _) => { },
                _ => { },
                () => { },
                () => { },
                async (candidate, cancellationToken) =>
                {
                    if (!ReferenceEquals(candidate, initial))
                        return;
                    quiesceEntered.TrySetResult();
                    await allowReplacement.Task.WaitAsync(cancellationToken);
                });

            Task replacementTask = host.ReplaceAsync(replacement);
            await quiesceEntered.Task;
            Task flushTask = host.FlushSettingsIfActiveAsync();

            Assert.False(flushTask.IsCompleted);
            allowReplacement.TrySetResult();
            await Task.WhenAll(replacementTask, flushTask);
            replacement = null;
        }
        finally
        {
            allowReplacement.TrySetResult();
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
    public async Task DeactivationFlushAfterShutdownIsANoOp()
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
        var host = new MainWindowSessionHost(
            viewModel,
            (_, _) => { },
            _ => { },
            () => { },
            () => { });

        try
        {
            await host.DisposeAsync();

            await host.FlushSettingsIfActiveAsync();
        }
        finally
        {
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
