// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Settings;
using DvmConsole.Application;
using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class SettingsTransferCoordinatorTests
{
    [Theory]
    [InlineData(null, SettingsImportScope.All)]
    [InlineData("Desk", SettingsImportScope.OperatorState)]
    public async Task ImportFlushesBeforeCommitAndReloadsTheReviewedStage(string? profile, SettingsImportScope scope)
    {
        var session = new Session();
        var coordinator = new SettingsTransferCoordinator(() => session);
        SettingsImportStage stage = Stage();
        await coordinator.ImportAsync(stage, scope, acceptRecordingPolicy: true, profileName: profile);
        Assert.Equal(new[] { "flush", "import", "reload" }, session.Events);
        Assert.Same(stage, session.Imported);
        Assert.Equal(scope, session.Scope);
        Assert.Equal(profile, session.Profile);
        Assert.True(session.AcceptPolicy);
    }

    [Fact]
    public async Task ResetUsesTheSameFlushAndReloadBoundary()
    {
        var session = new Session();
        await new SettingsTransferCoordinator(() => session).ResetAsync();
        Assert.Equal(new[] { "flush", "reset", "reload" }, session.Events);
    }

    [Theory]
    [InlineData("flush")]
    [InlineData("import")]
    public async Task PersistenceFailurePreventsReloadAndDoesNotPoisonNextTransfer(string phase)
    {
        var session = new Session { FailurePhase = phase };
        var coordinator = new SettingsTransferCoordinator(() => session);
        await Assert.ThrowsAsync<IOException>(() => coordinator.ImportAsync(Stage()));
        Assert.DoesNotContain("reload", session.Events);
        if (phase == "flush")
            Assert.DoesNotContain("import", session.Events);
        session.FailurePhase = null;
        await coordinator.ImportAsync(Stage());
        Assert.Equal("reload", session.Events.Last());
    }

    [Fact]
    public async Task FailedReloadReportsThatSettingsWereAlreadyCommitted()
    {
        var session = new Session { FailurePhase = "reload" };
        IOException failure = await Assert.ThrowsAsync<IOException>(() =>
            new SettingsTransferCoordinator(() => session).ImportAsync(Stage()));
        Assert.Contains("Settings were saved", failure.Message);
        Assert.Equal(new[] { "flush", "import", "reload" }, session.Events);
        Assert.IsType<IOException>(failure.InnerException);
    }

    [Fact]
    public async Task ChangedSessionDuringFlushRejectsImport()
    {
        var session = new Session { FlushBarrier = Barrier() };
        var coordinator = new SettingsTransferCoordinator(() => session);
        Task transfer = coordinator.ImportAsync(Stage());
        await session.FlushEntered.Task;
        session.IsCurrent = false;
        session.FlushBarrier.SetResult();
        await Assert.ThrowsAsync<IOException>(() => transfer);
        Assert.Equal(new[] { "flush" }, session.Events);
    }

    [Fact]
    public async Task CancelingBeforeCommitLeavesSettingsUntouched()
    {
        var session = new Session { FlushBarrier = Barrier() };
        using var cancellation = new CancellationTokenSource();
        Task transfer = new SettingsTransferCoordinator(() => session).ImportAsync(Stage(), cancellationToken: cancellation.Token);
        await session.FlushEntered.Task;
        cancellation.Cancel();
        session.FlushBarrier.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer);
        Assert.Equal(new[] { "flush" }, session.Events);
    }

    [Fact]
    public async Task QueuedTransferCannotApplyToAReplacedSession()
    {
        var session = new Session { ReloadBarrier = Barrier() };
        var coordinator = new SettingsTransferCoordinator(() => session);
        Task first = coordinator.ImportAsync(Stage());
        await session.ReloadEntered.Task;
        Task second = coordinator.ImportAsync(Stage());
        Assert.Equal(1, session.Events.Count(entry => entry == "import"));
        session.IsCurrent = false;
        session.ReloadBarrier.SetResult();
        await first;
        await Assert.ThrowsAsync<IOException>(() => second);
        Assert.Equal(1, session.Events.Count(entry => entry == "import"));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ProductionTransferRestoresMatchingCardsAcrossInstallationIdsAndRestart(
        bool matchingNames, bool restoreReceive)
    {
        string root = Path.Combine(Path.GetTempPath(), $"settings-transfer-{Guid.NewGuid():N}");
        string codeplug = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        var store = new UserSettingsStore(Path.Combine(root, "destination.json"));
        var reference = new ConfigurationReference(new ConfigurationId(Guid.NewGuid()), new ConfigurationRevision(Guid.NewGuid()));
        MainWindowSessionHost? host = null;
        try
        {
            store.Save(new UserSettings { LockWidgets = false, RecordingRootPath = Path.Combine(root, "recordings") });
            MainWindowViewModel Load() => MainWindowViewModel.Load(
                codeplug, store, serialPortProvider: static () => [], networkDisabledDemo: true,
                configurationReference: reference, useLegacyPathFallback: false);
            MainWindowViewModel original = Load();
            host = new MainWindowSessionHost(original, (_, _) => { }, _ => { }, () => { }, () => { });
            ChannelViewModel card = original.Systems[0].Channels[0];
            original.MoveChannelWidget(card, 10, 20, persist: true);
            original.SetReceiveSelectionPreference(card, enabled: true);
            string key = card.SettingsKey;
            string importedKey = matchingNames ? key : "Missing FNE\u001FRenamed channel";
            var source = new UserSettings
            {
                LockWidgets = false,
                RestoreSelectedChannelsOnStartup = restoreReceive,
                RecordingRootPath = Path.Combine(root, "recordings"),
                ReceiveEnabledChannelKeys = [importedKey],
                ChannelWidgetPositions = new() { [importedKey] = new WidgetPositionSetting { X = 347, Y = 186 } }
            };
            string sourceId = Guid.NewGuid().ToString("N");
            ConfigurationOperatorStateStore.CaptureActive(source, sourceId, codeplug);
            using var export = new MemoryStream();
            new UserSettingsStore(Path.Combine(root, "source.json")).Export(source, export);
            export.Position = 0;
            SettingsImportStage stage = original.StageSettingsImport(export, "operator-settings.json");
            var transfer = new SettingsTransferCoordinator(() =>
                new DesktopSettingsTransferSession(host.ViewModel, host, Load, replacement => host.ReplaceAsync(replacement)));
            await transfer.ImportAsync(stage);
            Assert.NotSame(original, host.ViewModel);
            ChannelViewModel imported = host.ViewModel.Systems[0].Channels[0];
            Assert.Equal(matchingNames && restoreReceive, imported.IsAudioEnabled);
            if (matchingNames)
            {
                Assert.Equal(347, imported.WidgetX);
                Assert.Equal(186, imported.WidgetY);
            }
            else
                Assert.DoesNotContain(host.ViewModel.Systems.SelectMany(system => system.Channels), candidate => candidate.SettingsKey == importedKey);
            await host.DisposeAsync();
            host = null;
            await using MainWindowViewModel restarted = Load();
            ChannelViewModel restored = restarted.Systems[0].Channels[0];
            Assert.Equal(matchingNames && restoreReceive, restored.IsAudioEnabled);
            if (matchingNames)
                Assert.Equal(347, restored.WidgetX);
        }
        finally
        {
            if (host is not null) await host.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static SettingsImportStage Stage()
    {
        using var source = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{}"));
        return new UserSettingsStore(Path.Combine(Path.GetTempPath(), $"stage-{Guid.NewGuid():N}.json"))
            .StageImport(source, "operator-settings.json");
    }
    private static TaskCompletionSource Barrier() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Session : ISettingsTransferSession
    {
        public bool IsCurrent { get; set; } = true;
        public List<string> Events { get; } = [];
        public string? FailurePhase { get; set; }
        public SettingsImportStage? Imported { get; private set; }
        public SettingsImportScope Scope { get; private set; }
        public string? Profile { get; private set; }
        public bool AcceptPolicy { get; private set; }
        public TaskCompletionSource? FlushBarrier { get; init; }
        public TaskCompletionSource FlushEntered { get; } = Barrier();
        public TaskCompletionSource? ReloadBarrier { get; init; }
        public TaskCompletionSource ReloadEntered { get; } = Barrier();
        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            Visit("flush");
            FlushEntered.TrySetResult();
            if (FlushBarrier is not null)
                await FlushBarrier.Task.WaitAsync(cancellationToken);
        }
        public void Import(SettingsImportStage stage, SettingsImportScope scope, bool acceptRecordingPolicy, string? profileName)
        {
            Visit("import");
            Imported = stage;
            Scope = scope;
            Profile = profileName;
            AcceptPolicy = acceptRecordingPolicy;
        }
        public void Reset() => Visit("reset");
        public async Task ReloadAsync()
        {
            Visit("reload");
            ReloadEntered.TrySetResult();
            if (ReloadBarrier is not null)
                await ReloadBarrier.Task;
        }
        private void Visit(string phase)
        {
            Events.Add(phase);
            if (FailurePhase == phase) throw new IOException(phase);
        }
    }
}
