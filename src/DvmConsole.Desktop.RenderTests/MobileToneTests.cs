// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DvmConsole.Application;
using DvmConsole.Audio;
using DvmConsole.Core.Settings;
using DvmConsole.Mobile;
using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.RenderTests;

public sealed class MobileToneTests
{
    [AvaloniaFact]
    public void FocusedToneFamiliesRetainDraftControlsAndShareOneScroller()
    {
        var editor = new ToneSettingsView();
        var options = new ComboBox();
        var window = new Window { Content = editor, Width = 390, Height = 700 };
        try
        {
            editor.UseFocusedNavigation(options);
            window.Show(); window.UpdateLayout();
            var pattern = editor.FindControl<Control>("TonePatternSection")!;
            var dtmf = editor.FindControl<Control>("DtmfSection")!;
            Assert.True(pattern.IsVisible);
            Assert.False(dtmf.IsVisible);
            Assert.Single(options.GetVisualAncestors().OfType<ScrollViewer>());
            editor.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Tag, "DtmfSection"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(dtmf.IsVisible);
            Assert.False(pattern.IsVisible);
            editor.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Tag, "TonePatternSection"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Same(pattern, editor.FindControl<Control>("TonePatternSection"));
            Assert.True(pattern.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(390, 700)]
    [InlineData(852, 320)]
    [InlineData(1024, 768)]
    public async Task ToneOptionsLeaveEditorAndNavigationVisible(double width, double height)
    {
        await using var view = new MobileToneView();
        var window = new Window { Content = view, Width = width, Height = height };
        try
        {
            window.Show(); window.UpdateLayout();
            var editor = Assert.Single(view.GetVisualDescendants().OfType<ToneSettingsView>());
            Assert.True(editor.Bounds.Height >= 100);
            var back = view.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "‹ Settings"));
            Assert.DoesNotContain(back.GetVisualAncestors(), ancestor => ancestor is ScrollViewer);
            Assert.True(back.Bounds.Height >= 44);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SuccessfulSendKeepsRuntimeMonitorWarningVisible()
    {
        const string warning = "Audio sent; local monitor failed: output unavailable.";
        var commands = new ToneCommands();
        var reference = new ConfigurationReference(ConfigurationId.New(), ConfigurationRevision.New());
        await using var application = new ConsoleApplicationSession(new(reference, [], [], []),
            ConsoleRuntimeSnapshot.Empty with { StatusText = warning }, commands);
        var settings = new UserSettings();
        await using var model = new MobileToneViewModel(settings, new Store(), application);
        model.DtmfDigits = "1";
        await model.SendDtmfAsync();
        Assert.Equal(warning, model.Status);
        Assert.Equal(1, commands.Sends);
    }

    [AvaloniaFact]
    public async Task PatternImportRenamesConflictsAndSupportsUndoWithoutTransmitting()
    {
        var commands = new ToneCommands();
        await using var session = new ConsoleApplicationSession(new(null, [], [], []), ConsoleRuntimeSnapshot.Empty, commands);
        var settings = new UserSettings { TonePresets = [new() { Name = "Alert" }] };
        await using var model = new MobileToneViewModel(settings, new Store(), session);
        var document = new TonePatternDocument
        {
            TonePresets = [new() { Name = "Alert", Steps =
            [new() { FrequencyHz = 900, DurationSeconds = 1 }, new() { Kind = "hold", DurationSeconds = 0.02 }] }]
        };
        await model.EditAsync(() => model.Editor.ImportPatterns(document));
        Assert.Collection(settings.TonePresets, preset => Assert.Equal("Alert", preset.Name),
            preset => Assert.Equal("Alert (2)", preset.Name));
        Assert.Equal(0.02, settings.TonePresets[1].Steps[1].DurationSeconds);
        Assert.Equal(0, commands.Sends);
        await model.AssignMainAlertAsync("Alert (2)");
        Assert.Equal("Alert (2)", settings.MobileAlertPresetName);
        await model.UndoAsync();
        Assert.Single(settings.TonePresets);
    }

    private sealed class ToneCommands : IConsoleCommands, IConsoleToneCommands
    {
        public int Sends { get; private set; }
        public bool LocalToneMonitorEnabled { get; set; }
        public Task SendToneAsync(GeneratedToneSequence sequence, ConsoleToneTargets targets, CancellationToken cancellationToken = default)
        { Sends++; return Task.CompletedTask; }
        public Task SendAlertAudioAsync(AssetId asset, CancellationToken cancellationToken = default)
        { Sends++; return Task.CompletedTask; }
        public Task CancelTonesAsync() => Task.CompletedTask;
        public ValueTask SetReceiveEnabledAsync(ChannelId id, bool enabled, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<bool> BeginPttAsync(ChannelId id, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
        public ValueTask EndPttAsync(ChannelId id, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SetTransmitSelectedAsync(ChannelId id, bool selected, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SetPageSelectedAsync(ChannelId id, bool selected, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SetAlertSelectedAsync(ChannelId id, bool selected, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SetTransmitEncryptedAsync(ChannelId id, bool encrypted, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SetChannelGainAsync(ChannelId id, double gain, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SetChannelBalanceAsync(ChannelId id, double balance, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    [AvaloniaFact]
    public async Task LeavingSettingsCancelsSlowTonePageBeforeItCanReplaceNavigation()
    {
        await using var console = new MobileConsoleView(ConsoleHostFormFactor.Phone);
        var store = new DeferredStore();
        var settings = new MobileSettingsView(new TextBlock(), ConsoleHostFormFactor.Phone, () => console,
            () => new MobileSession(console.ApplicationSession) { ToneSettings = store });
        var window = new Window { Content = settings, Width = 390, Height = 700 };
        settings.ConsoleRequested += (_, _) => window.Content = console;
        try
        {
            window.Show();
            var originalPage = settings.Content;
            var open = settings.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, "Tones and Paging  ›"));
            var back = settings.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, "‹ Console"));
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            open.PropertyChanged += (_, change) =>
            {
                if (change.Property == Button.IsEnabledProperty && open.IsEnabled) finished.TrySetResult();
            };
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(open.IsEnabled);
            back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(store.Token.IsCancellationRequested);
            store.Result.SetResult(new UserSettings()); // Simulate storage ignoring cancellation.
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(console, window.Content);
            Assert.Same(originalPage, settings.Content);
        }
        finally { store.Result.TrySetResult(new UserSettings()); window.Close(); }
    }

    private sealed class DeferredStore : IConsoleToneSettingsStore
    {
        public TaskCompletionSource<UserSettings> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public ValueTask<UserSettings> LoadToneSettingsAsync(CancellationToken cancellationToken = default)
        { Token = cancellationToken; return new(Result.Task); }
        public ValueTask SaveToneSettingsAsync(UserSettings settings, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    [AvaloniaFact]
    public async Task SharedAudioFileSurvivesBothDeletionUndoAndFailedSave()
    {
        await using var console = new MobileConsoleView(ConsoleHostFormFactor.Phone);
        var id = AssetId.New();
        var settings = new UserSettings
        {
            AlertTones =
            [new() { Name = "One", AssetId = id.ToString() }, new() { Name = "Two", AssetId = id.ToString() }]
        };
        var store = new Store();
        await store.SaveToneSettingsAsync(settings);
        var assets = new Assets();
        await using var model = new MobileToneViewModel(settings, store, console.ApplicationSession, assets);
        var tones = model.AlertTones.Cast<AlertToneViewModel>().ToArray();
        await model.DeleteAlertAsync(tones[0]);
        await model.DeleteAlertAsync(tones[1]);
        Assert.Empty(assets.Deleted); // The second entry can still restore the same file.
        await model.UndoAsync();
        Assert.Same(tones[1], Assert.Single(model.AlertTones.Cast<AlertToneViewModel>()));
        Assert.Empty(assets.Deleted);

        store.Fail = true;
        await model.DeleteAlertAsync(tones[1]);
        await model.EditAsync(() => ((ITonePresentationSession)model).BeginUndoableAction(
            "Next edit", static () => ValueTask.CompletedTask));
        Assert.True(model.HasUnsavedChanges);
        Assert.Empty(assets.Deleted);
        store.Fail = false;
        await model.RetrySaveAsync();
        Assert.False(model.HasUnsavedChanges);
        Assert.False(model.HasPendingAssetCleanup);
        Assert.Equal([id], assets.Deleted);
    }

    [AvaloniaFact]
    public async Task FailedAssetCleanupRemainsRetryableAndDoesNotUndoSavedRemoval()
    {
        await using var console = new MobileConsoleView(ConsoleHostFormFactor.Phone);
        var id = AssetId.New();
        var settings = new UserSettings { AlertTones = [new() { Name = "One", AssetId = id.ToString() }] };
        var store = new Store();
        await store.SaveToneSettingsAsync(settings);
        var assets = new Assets { Fail = true };
        await using var model = new MobileToneViewModel(settings, store, console.ApplicationSession, assets);
        await model.DeleteAlertAsync(Assert.Single(model.AlertTones.Cast<AlertToneViewModel>()));
        await model.EditAsync(() => ((ITonePresentationSession)model).BeginUndoableAction(
            "Next edit", static () => ValueTask.CompletedTask));
        Assert.False(model.HasUnsavedChanges);
        Assert.True(model.HasPendingAssetCleanup);
        Assert.Empty(model.AlertTones.Cast<AlertToneViewModel>());
        Assert.Contains("Audio storage unavailable", model.Status);
        assets.Fail = false;
        await model.RetrySaveAsync();
        Assert.False(model.HasPendingAssetCleanup);
        Assert.Equal([id], assets.Deleted);
    }

    [AvaloniaFact]
    public async Task ReplacingUndoCommitsPreviousActionAndPageRetirementJoinsCleanup()
    {
        await using var console = new MobileConsoleView(ConsoleHostFormFactor.Phone);
        var model = new MobileToneViewModel(new UserSettings(), new Store(), console.ApplicationSession);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int committed = 0;
        var editor = (ITonePresentationSession)model;
        editor.BeginUndoableAction("First", static () => ValueTask.CompletedTask, async () =>
        {
            committed++;
            await release.Task;
        });
        editor.BeginUndoableAction("Second", static () => ValueTask.CompletedTask, () =>
        {
            committed++;
            return ValueTask.CompletedTask;
        });
        Task closing = model.DisposeAsync().AsTask();
        try
        {
            Assert.Equal(2, committed);
            Assert.False(closing.IsCompleted);
            release.TrySetResult();
            await closing;
            await model.DisposeAsync();
            Assert.Equal(2, committed);
        }
        finally { release.TrySetResult(); await model.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task FailedPresetSaveRemainsRetryableAndDeleteCanBeUndone()
    {
        await using var console = new MobileConsoleView(ConsoleHostFormFactor.Phone);
        var store = new Store { Fail = true };
        using var model = new MobileToneViewModel(new UserSettings(), store, console.ApplicationSession)
        { DtmfDigits = "12", DtmfPresetName = "Dispatch" };
        await model.EditAsync(model.Editor.SaveDtmfPreset);
        Assert.True(model.HasUnsavedChanges);
        Assert.Contains("not been saved", model.Status);
        Assert.Single(model.DtmfPresets.Cast<DtmfPresetViewModel>());
        store.Fail = false;
        await model.RetrySaveAsync();
        Assert.False(model.HasUnsavedChanges);
        var preset = Assert.Single(model.DtmfPresets.Cast<DtmfPresetViewModel>());
        await model.EditAsync(() => model.Editor.DeleteDtmfPreset(preset));
        Assert.Empty(model.DtmfPresets.Cast<DtmfPresetViewModel>());
        Assert.True(model.CanUndo);
        await model.UndoAsync();
        Assert.Same(preset, Assert.Single(model.DtmfPresets.Cast<DtmfPresetViewModel>()));
        Assert.False(model.CanUndo);
        Assert.Equal(3, store.SuccessfulWrites);
    }

    [AvaloniaFact]
    public async Task CancelJoinsPendingPreferenceWriteBeforePageRetirement()
    {
        await using var console = new MobileConsoleView(ConsoleHostFormFactor.Phone);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Store { SaveBarrier = release.Task };
        using var model = new MobileToneViewModel(new UserSettings(), store, console.ApplicationSession)
        { DtmfDigits = "1", DtmfPresetName = "One" };
        Task saving = model.EditAsync(model.Editor.SaveDtmfPreset);
        Task closing = model.CancelAsync();
        Assert.True(model.IsBusy);
        Assert.False(closing.IsCompleted);
        release.SetResult();
        await Task.WhenAll(saving, closing);
        Assert.False(model.IsBusy);
        Assert.False(model.HasUnsavedChanges);
    }

    private sealed class Store : IConsoleToneSettingsStore, IConsoleToneAssetMaintenance
    {
        private IReadOnlySet<AssetId> references = new HashSet<AssetId>();
        public bool Fail;
        public int SuccessfulWrites;
        public Task SaveBarrier = Task.CompletedTask;
        public ValueTask<UserSettings> LoadToneSettingsAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(new UserSettings());
        public async ValueTask SaveToneSettingsAsync(UserSettings settings, CancellationToken cancellationToken = default)
        {
            await SaveBarrier.WaitAsync(cancellationToken);
            if (Fail) throw new IOException("Storage unavailable.");
            references = ConsoleAssetReferences.FromSettings(settings);
            SuccessfulWrites++;
        }
        public ValueTask<bool> DeleteUnreferencedToneAssetAsync(AssetId id, IAssetStore assets, CancellationToken cancellationToken = default)
            => assets.DeleteIfUnreferencedAsync(id, references, cancellationToken);
    }

    private sealed class Assets : IAssetStore
    {
        public bool Fail;
        public List<AssetId> Deleted { get; } = [];
        public ValueTask<bool> DeleteIfUnreferencedAsync(AssetId id, IReadOnlyCollection<AssetId> referencedAssets,
            CancellationToken cancellationToken = default)
        {
            if (Fail) throw new IOException("Audio storage unavailable.");
            if (referencedAssets.Contains(id)) return ValueTask.FromResult(false);
            Deleted.Add(id);
            return ValueTask.FromResult(true);
        }
        public ValueTask<AssetDescriptor> ImportAsync(string displayName, string mediaType, Stream content, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask<Stream> OpenReadAsync(AssetId id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public IAsyncEnumerable<AssetDescriptor> ListAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
