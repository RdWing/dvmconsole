// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

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
                    {
                        Assert.True(initial.IsSessionInputSuppressed);
                        outgoingQuiesced = true;
                    }
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
    public async Task FailedReplacementRestoresOutgoingInputOwnership()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-session-host-tests",
            Guid.NewGuid().ToString("N"));
        var store = new UserSettingsStore(Path.Combine(directory, "UserSettings.json"));
        var initial = new MainWindowViewModel("Initial session", [], [], CreateOptions(store));
        var replacement = new MainWindowViewModel("Replacement session", [], [], CreateOptions(store));
        int publications = 0;
        var connectionLifecycle = new RecordingConnectionLifecycle();
        var host = new MainWindowSessionHost(
            initial,
            (_, _) => { },
            _ =>
            {
                if (Interlocked.Increment(ref publications) > 1)
                    throw new InvalidOperationException("test publication failure");
            },
            () => { },
            () => { },
            (_, _) => Task.CompletedTask,
            createConnectionLifecycle: _ => connectionLifecycle);

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => host.ReplaceAsync(replacement));

            Assert.Same(initial, host.ViewModel);
            Assert.False(initial.IsSessionInputSuppressed);
            Assert.Equal(1, connectionLifecycle.QuiesceCount);
            Assert.Equal([SystemId.FromName("Initial")], connectionLifecycle.RestoredSystemIds);
        }
        finally
        {
            await host.DisposeAsync();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FailedReplacementPreservesPrimaryCauseWhenRollbackCleanupAlsoFails()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-session-host-tests",
            Guid.NewGuid().ToString("N"));
        var store = new UserSettingsStore(Path.Combine(directory, "UserSettings.json"));
        var initial = new MainWindowViewModel("Initial session", [], [], CreateOptions(store));
        var replacementServices = new ConsoleSessionServices();
        var replacement = new MainWindowViewModel(
            "Replacement session",
            [],
            [],
            new MainWindowViewModelOptions(
                Document: new(store),
                Host: new(
                    SerialPortProvider: () => [],
                    UiDispatcher: ImmediateTestUiDispatcher.Instance,
                    SessionServices: replacementServices),
                Features: new(NetworkDisabledDemo: true)));
        replacementServices.Presentation.Register(
            "rollback-failure-one",
            () => ValueTask.FromException(new IOException("test rollback failure one")));
        replacementServices.Presentation.Register(
            "rollback-failure-two",
            () => ValueTask.FromException(new IOException("test rollback failure two")));
        int publications = 0;
        var host = new MainWindowSessionHost(
            initial,
            (_, _) => { },
            _ =>
            {
                if (Interlocked.Increment(ref publications) > 1)
                    throw new InvalidOperationException("test publication failure");
            },
            () => { },
            () => { },
            (_, _) => Task.CompletedTask,
            createConnectionLifecycle: _ => new RecordingConnectionLifecycle());

        try
        {
            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => host.ReplaceAsync(replacement));

            Assert.Contains("test publication failure", failure.Message, StringComparison.Ordinal);
            Assert.Contains("Restoring the previous session also failed", failure.Message, StringComparison.Ordinal);
            Assert.IsType<AggregateException>(failure.InnerException);
            Assert.Same(initial, host.ViewModel);
            Assert.False(initial.IsSessionInputSuppressed);
        }
        finally
        {
            await host.DisposeAsync();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PublishedReplacementContainsRetiredSessionCleanupFailure()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-session-host-tests",
            Guid.NewGuid().ToString("N"));
        var store = new UserSettingsStore(Path.Combine(directory, "UserSettings.json"));
        var initialServices = new ConsoleSessionServices();
        var initial = new MainWindowViewModel(
            "Initial session",
            [],
            [],
            new MainWindowViewModelOptions(
                Document: new(store),
                Host: new(
                    SerialPortProvider: () => [],
                    UiDispatcher: ImmediateTestUiDispatcher.Instance,
                    SessionServices: initialServices),
                Features: new(NetworkDisabledDemo: true)));
        initialServices.Presentation.Register(
            "retirement-failure-one",
            () => ValueTask.FromException(new IOException("test retirement failure one")));
        initialServices.Presentation.Register(
            "retirement-failure-two",
            () => ValueTask.FromException(new IOException("test retirement failure two")));
        MainWindowViewModel? replacement = new(
            "Replacement session",
            [],
            [],
            CreateOptions(store));
        var followUpFailures = new List<SessionReplacementFollowUpFailure>();
        var host = new MainWindowSessionHost(
            initial,
            (_, _) => { },
            _ => { },
            () => { },
            () => { },
            (_, _) => Task.CompletedTask);
        host.ReplacementFollowUpFailed += (_, failure) =>
        {
            followUpFailures.Add(failure);
            throw new InvalidOperationException("test observer failure");
        };

        try
        {
            await host.ReplaceAsync(replacement);

            Assert.Same(replacement, host.ViewModel);
            SessionReplacementFollowUpFailure failure = Assert.Single(followUpFailures);
            Assert.Same(replacement, failure.ActiveViewModel);
            Assert.Equal(SessionReplacementFollowUpPhase.RetiredSessionCleanup, failure.Phase);
            Assert.IsType<AggregateException>(failure.Exception);
            replacement = null;
        }
        finally
        {
            if (replacement is not null)
                await replacement.DisposeAsync();
            await host.DisposeAsync();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReplacementPreparationFailureDisposesCandidateAndKeepsCurrentSession()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-session-host-tests",
            Guid.NewGuid().ToString("N"));
        var store = new UserSettingsStore(Path.Combine(directory, "UserSettings.json"));
        var initial = new MainWindowViewModel("Initial session", [], [], CreateOptions(store));
        int candidateDisposalCount = 0;
        var replacement = new MainWindowViewModel(
            "Replacement session",
            [],
            [],
            new MainWindowViewModelOptions(
                Document: new(store),
                Host: new(
                    SerialPortProvider: () => [],
                    UiDispatcher: ImmediateTestUiDispatcher.Instance,
                    SessionServices: new ConsoleSessionServices(
                        _ => Interlocked.Increment(ref candidateDisposalCount))),
                Features: new(NetworkDisabledDemo: true)));
        var connectionLifecycle = new RecordingConnectionLifecycle();
        var host = new MainWindowSessionHost(
            initial,
            (_, _) => { },
            _ => { },
            () => { },
            () => { },
            createConnectionLifecycle: owner => ReferenceEquals(owner, replacement)
                ? throw new InvalidOperationException("test preparation failure")
                : connectionLifecycle);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.ReplaceAsync(replacement));

            Assert.Same(initial, host.ViewModel);
            Assert.False(initial.IsSessionInputSuppressed);
            Assert.True(candidateDisposalCount > 0);
        }
        finally
        {
            await host.DisposeAsync();
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
            },
            candidate =>
            {
                Assert.Same(viewModel, candidate);
                operations.Add("silence-live-receive");
            });

        try
        {
            await host.DisposeAsync();

            Assert.True(viewModel.IsSessionInputSuppressed);
            Assert.Equal(
                ["silence-live-receive", "quiesce-fne", "close-windows"],
                operations);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ShutdownDeadlineDoesNotSkipLaterCleanupWhenQuiesceIgnoresCancellation()
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
        var releaseQuiesce = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int closeCount = 0;
        var host = new MainWindowSessionHost(
            viewModel,
            (_, _) => { },
            _ => { },
            () => { },
            () => Interlocked.Increment(ref closeCount),
            (_, _) => releaseQuiesce.Task,
            transitionTimeout: TimeSpan.FromMilliseconds(100));

        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<Exception>(() => host.DisposeAsync().AsTask());

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
            Assert.Equal(1, Volatile.Read(ref closeCount));
            Assert.True(viewModel.IsSessionInputSuppressed);
        }
        finally
        {
            releaseQuiesce.TrySetResult();
            await host.ApplicationSession.DisposeAsync();
            await viewModel.DisposeAsync();
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
        => DesktopTestSessionBuilder.CreateOptions(store);

    private sealed class RecordingConnectionLifecycle : IConsoleSessionConnectionLifecycle
    {
        public int QuiesceCount { get; private set; }
        public IReadOnlyList<SystemId> RestoredSystemIds { get; private set; } = [];

        public IReadOnlyList<SystemId> CaptureActiveSystemIds()
            => [SystemId.FromName("Initial")];

        public ValueTask QuiesceAsync(CancellationToken cancellationToken)
        {
            QuiesceCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask RestoreAsync(
            IReadOnlyList<SystemId> systemIds,
            CancellationToken cancellationToken)
        {
            RestoredSystemIds = systemIds.ToArray();
            return ValueTask.CompletedTask;
        }
    }

}
