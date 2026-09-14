// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Styling;
using Avalonia.Media;
using Avalonia.VisualTree;
using DvmConsole.Application;
using DvmConsole.Configuration.Yaml;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using DvmConsole.Mobile;
using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.RenderTests;

public sealed class MobileStudioTests
{
    [AvaloniaTheory]
    [InlineData(390, 600, false)]
    [InlineData(1024, 768, true)]
    public async Task SaveAndReloadPromptsStayCenteredAndBlockTheEditor(int width, int height, bool reloadAccepted)
    {
        var reference = new ConfigurationReference(ConfigurationId.New(), ConfigurationRevision.New());
        var vm = new ConfigurationStudioViewModel(ConfigurationDocument.CreateNew(), reference.Id,
            "draft.yml", new MobileStudioRuntimeContext(), new MaterializedConfigurationStudioCompanionSource(),
            new ConfigurationSnapshotPreviewFactory(), new(new Dictionary<string, ConfigurationStudioPosition>(),
                new Dictionary<string, string>(), []), ConfigurationStudioSection.Zones);
        vm.AddSystem(); vm.AddChannelToSelectedSystem();
        int commits = 0, reloads = 0;
        var services = new ConfigurationStudioSaveServices(() => Task.CompletedTask, () => reference,
            _ => new([new("draft.yml", "", null, "configuration", false)], []),
            _ => string.Join("\n", Enumerable.Repeat("Save the reviewed configuration changes.", width < 600 ? 30 : 1)),
            (_, _, _) => { commits++; return Task.FromResult(new ConfigurationStudioCommitResult(reference, "")); });
        await using var studio = new MobileStudioSession(vm, services,
            (_, _) => Task.FromResult<IReadOnlyList<string>>([]), () => ValueTask.CompletedTask);
        var view = new MobileConfigurationStudioView(studio, reference.Id,
            _ => { reloads++; return Task.FromResult(true); }, () => Task.CompletedTask,
            () => throw new InvalidOperationException(), formFactor: ConsoleHostFormFactor.Tablet);
        var window = new Window { Width = width, Height = height, Content = view };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var editor = view.GetVisualDescendants().OfType<ConfigurationStudioView>().Single();
            var review = editor.GetVisualDescendants().OfType<Button>().Single(b =>
                b.IsEffectivelyVisible && b.Content as string == "Review & Save");
            review.Focus();
            review.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            CheckPrompt("Review & Save");
            Assert.Equal(0, commits);
            ClickPrompt("Save");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            CheckPrompt("Reload active configuration?");
            Assert.Equal(1, commits);
            ClickPrompt(reloadAccepted ? "Disconnect and reload" : "Cancel");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            Assert.Empty(view.GetVisualDescendants().OfType<MobileConfirmationPrompt>());
            Assert.True(editor.IsEffectivelyEnabled);
            Assert.Equal(reloadAccepted ? 1 : 0, reloads);
            Assert.Same(review, window.FocusManager?.GetFocusedElement());
            review.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Empty(view.GetVisualDescendants().OfType<MobileConfirmationPrompt>());
            Assert.True(editor.IsEffectivelyEnabled);
            Assert.Equal(1, commits);

            void CheckPrompt(string title)
            {
                var prompt = view.GetVisualDescendants().OfType<MobileConfirmationPrompt>().Single();
                Assert.Contains(prompt.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == title);
                var origin = prompt.TranslatePoint(default, view)!.Value;
                // Pixel rounding can place the center half a device pixel away.
                Assert.InRange(Math.Abs(view.Bounds.Height / 2 - (origin.Y + prompt.Bounds.Height / 2)),
                    0, 0.5 / window.RenderScaling + 0.001);
                Assert.InRange(origin.X, 0, width);
                Assert.InRange(origin.Y + prompt.Bounds.Height, 0, height);
                foreach (var button in prompt.GetVisualDescendants().OfType<Button>())
                {
                    var location = button.TranslatePoint(default, view)!.Value;
                    Assert.InRange(location.Y + button.Bounds.Height, 0, height);
                }
                Assert.False(editor.IsEffectivelyEnabled);
                Assert.Same(ControlAutomationPeer.CreatePeerForElement(prompt),
                    Assert.Single(ControlAutomationPeer.CreatePeerForElement(view)!.GetChildren()));
                if (Environment.GetEnvironmentVariable("NEO_REVIEW_CAPTURE") is { } captures)
                {
                    Directory.CreateDirectory(captures);
                    App.SaveVisual(view, Path.Combine(captures, $"studio-{width}-{(commits == 0 ? "review" : "reload")}.png"));
                }
                Assert.Equal("Cancel", (window.FocusManager?.GetFocusedElement() as Button)?.Content);
            }
            void ClickPrompt(string label) => view.GetVisualDescendants().OfType<MobileConfirmationPrompt>()
                .Single().GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == label)
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(790)]
    [InlineData(1030)]
    public void TabletChannelEditingAdaptsWithoutClipping(int width)
    {
        var model = new ConfigurationStudioViewModel(ConfigurationDocument.CreateNew(), null,
            "new.yml", new MobileStudioRuntimeContext(), new MaterializedConfigurationStudioCompanionSource(),
            new ConfigurationSnapshotPreviewFactory(), new(new Dictionary<string, ConfigurationStudioPosition>(),
                new Dictionary<string, string>(), []), ConfigurationStudioSection.Zones);
        model.AddSystem();
        model.AddChannelToSelectedSystem();
        var view = new ConfigurationStudioView { UseTouchLayout = true, DataContext = model };
        var window = new Window { Width = width, Height = 900, Content = view };
        try
        {
            window.Show(); window.UpdateLayout();
            var zones = view.ZonesView;
            Assert.True(zones.FindControl<Button>("TouchZoneActions")!.IsVisible);
            Assert.False(zones.FindControl<Menu>("ZoneEditMenu")!.IsVisible);
            if (width < 860)
            {
                Assert.False(zones.FindControl<ScrollViewer>("DesktopChannelTableScroller")!.IsVisible);
                var row = zones.FindControl<ListBox>("narrowChannelList")!.GetVisualDescendants()
                    .OfType<Button>().First(b => b.DataContext is ConfigurationChannelRow && b.MinHeight == 58);
                var point = row.TranslatePoint(new Point(40, 20), window)!.Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Assert.True(zones.FindControl<Border>("ChannelInspector")!.IsVisible);
                Assert.False(zones.FindControl<Grid>("ChannelPane")!.IsVisible);
            }
            else
            {
                Assert.True(zones.FindControl<ScrollViewer>("DesktopChannelTableScroller")!.IsVisible);
                var field = zones.GetVisualDescendants().OfType<TextBox>().First(t => t.Classes.Contains("table-editor"));
                Assert.Equal(44, field.Height);
                Assert.Equal(new Thickness(1), field.BorderThickness);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(ConsoleHostFormFactor.Phone, 390, false)]
    [InlineData(ConsoleHostFormFactor.Phone, 844, false)]
    [InlineData(ConsoleHostFormFactor.Tablet, 390, true)]
    [InlineData(ConsoleHostFormFactor.Tablet, 1024, true)]
    public async Task LiveLayoutFollowsHostCapabilityRatherThanWindowWidth(ConsoleHostFormFactor form, int width, bool visible)
    {
        var reference = new ConfigurationReference(ConfigurationId.New(), ConfigurationRevision.New());
        var vm = new ConfigurationStudioViewModel(ConfigurationDocument.CreateNew(), reference.Id,
            "draft.yml", new MobileStudioRuntimeContext(), new MaterializedConfigurationStudioCompanionSource(),
            new ConfigurationSnapshotPreviewFactory(), new(new Dictionary<string, ConfigurationStudioPosition>(),
                new Dictionary<string, string>(), []), ConfigurationStudioSection.Zones);
        var services = new ConfigurationStudioSaveServices(() => Task.CompletedTask, () => reference,
            _ => throw new InvalidOperationException(), _ => "", (_, _, _) => throw new InvalidOperationException());
        await using var studio = new MobileStudioSession(vm, services,
            (_, _) => Task.FromResult<IReadOnlyList<string>>([]), () => ValueTask.CompletedTask);
        var view = new MobileConfigurationStudioView(studio, reference.Id, _ => Task.FromResult(false),
            () => Task.CompletedTask, () => throw new InvalidOperationException(), formFactor: form);
        await using var console = new MobileConsoleView(form);
        var settings = new MobileSettingsView(view, form, console);
        settings.OpenConfigurationLibrary();
        var window = new Window { Width = width, Height = 768, Content = settings };
        try
        {
            window.Show();
            window.UpdateLayout();
            var editor = view.GetVisualDescendants().OfType<ConfigurationStudioView>().Single();
            Assert.Equal(visible, editor.ZonesView.FindControl<Border>("liveZoneLayoutDrawer")!.IsVisible);
            Assert.InRange(view.Bounds.Width, width - 1, width + 1);
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                window.RequestedThemeVariant = theme;
                window.UpdateLayout();
                Assert.True(view.TryFindResource("ShellBackgroundBrush", theme, out var appBrush));
                Assert.True(editor.TryFindResource("StudioCanvasBrush", theme, out var studioBrush));
                Assert.Same(appBrush, studioBrush);
                string? capture = Environment.GetEnvironmentVariable("NEO_STUDIO_STYLE_CAPTURE");
                if (form == ConsoleHostFormFactor.Tablet && width == 1024 && capture is not null)
                {
                    Directory.CreateDirectory(capture);
                    App.SaveVisual(view, Path.Combine(capture, $"studio-{theme.Key}.png"));
                }
            }
            Assert.True(new ConfigurationStudioView().ShowZoneLayoutPreview);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void StudioPreviewFollowsHostAppearanceWithoutEditingTheDraft(bool touch)
    {
        var model = new ConfigurationStudioViewModel(ConfigurationDocument.CreateNew(), null,
            "new.yml", new MobileStudioRuntimeContext(), new MaterializedConfigurationStudioCompanionSource(),
            new ConfigurationSnapshotPreviewFactory(), new(new Dictionary<string, ConfigurationStudioPosition>(),
                new Dictionary<string, string>(), []), ConfigurationStudioSection.Zones);
        model.AddSystem();
        model.AddChannelToSelectedSystem();
        var view = new ConfigurationStudioView { UseTouchLayout = touch, DataContext = model };
        var window = new Window
        {
            Width = touch ? 390 : 1200,
            Height = 720,
            RequestedThemeVariant = ThemeVariant.Dark,
            Content = view
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            Assert.True(view.ZonesView.IsVisible);
            Assert.False(view.FindControl<ConfigurationStudioKeysView>("KeysPage")!.IsVisible);
            Assert.False(view.FindControl<ConfigurationStudioFilesView>("FilesPage")!.IsVisible);
            var inspectorNavigation = view.ZonesView.FindControl<Button>("InspectorNavigation")!;
            var openInspector = view.ZonesView.FindControl<Button>("OpenInspector")!;
            Assert.Equal(touch, openInspector.IsVisible);
            Assert.False(inspectorNavigation.IsVisible);
            if (touch)
            {
                var selectedBeforeExpansion = model.SelectedChannel;
                openInspector.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var inspectorScroll = view.ZonesView.FindControl<ScrollViewer>("InspectorScroll")!;
                Assert.True(inspectorScroll.Offset.Y > 0);
                var heading = view.ZonesView.FindControl<TextBlock>("ChannelSettingsHeading")!;
                Assert.InRange(heading.TranslatePoint(default, inspectorScroll)!.Value.Y, 0, 30);
                Assert.True(inspectorNavigation.IsVisible);
                Assert.False(view.ZonesView.FindControl<Grid>("ChannelPane")!.IsVisible);
                Assert.Same(selectedBeforeExpansion, model.SelectedChannel);
                inspectorNavigation.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                Assert.True(view.ZonesView.FindControl<Grid>("ChannelPane")!.IsVisible);
            }
            var sectionHeading = view.ZonesView.FindControl<TextBlock>("ChannelSettingsHeading")!;
            Assert.Equal(16, sectionHeading.FontSize);
            bool beforeScale = model.IsDirty;
            MobileTypography.Apply(window.Resources, 1.5);
            Assert.Equal(24, sectionHeading.FontSize);
            Assert.Equal(beforeScale, model.IsDirty);
            MobileTypography.Apply(window.Resources, 1);
            var dark = Assert.Single(model.PreviewChannels);
            var darkColor = Assert.IsAssignableFrom<ISolidColorBrush>(dark.Card.CardBackgroundBrush).Color;
            bool dirty = model.IsDirty, undo = model.CanUndo, redo = model.CanRedo, layout = model.LayoutChanged;
            var selected = model.SelectedChannel;
            window.RequestedThemeVariant = ThemeVariant.Light;
            window.UpdateLayout();
            var light = Assert.Single(model.PreviewChannels);
            Assert.NotEqual(darkColor, Assert.IsAssignableFrom<ISolidColorBrush>(light.Card.CardBackgroundBrush).Color);
            Assert.Equal(dark.X, light.X);
            Assert.Equal(dark.Y, light.Y);
            Assert.Same(selected, model.SelectedChannel);
            Assert.Equal(dark.IsSelected, light.IsSelected);
            Assert.Equal((dirty, undo, redo, layout), (model.IsDirty, model.CanUndo, model.CanRedo, model.LayoutChanged));
            model.SetPreviewAppearance(false);
            Assert.Same(light, Assert.Single(model.PreviewChannels));
            window.RequestedThemeVariant = ThemeVariant.Dark;
            window.UpdateLayout();
            Assert.Equal(darkColor, Assert.IsAssignableFrom<ISolidColorBrush>(Assert.Single(model.PreviewChannels).Card.CardBackgroundBrush).Color);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StudioUsesActiveGroupCommandsAndRejectsReplacedSessions(bool activeDraft)
    {
        var reference = new ConfigurationReference(ConfigurationId.New(), ConfigurationRevision.New());
        ConfigurationId editedId = activeDraft ? reference.Id : ConfigurationId.New();
        var commands = new GroupCommands();
        await using var application = new ConsoleApplicationSession(new(reference, [], [], []),
            ConsoleRuntimeSnapshot.Empty, commands);
        await using var replacement = new ConsoleApplicationSession(new(reference, [], [], []),
            ConsoleRuntimeSnapshot.Empty, commands);
        MobileSession current = new(application);
        var vm = new ConfigurationStudioViewModel(ConfigurationDocument.CreateNew(), editedId,
            "draft.yml", new MobileStudioRuntimeContext(), new MaterializedConfigurationStudioCompanionSource(),
            new ConfigurationSnapshotPreviewFactory(), new(new Dictionary<string, ConfigurationStudioPosition>(),
                new Dictionary<string, string>(), []), ConfigurationStudioSection.Groups);
        var services = new ConfigurationStudioSaveServices(() => Task.CompletedTask, () => reference,
            _ => throw new InvalidOperationException(), _ => "", (_, _, _) => throw new InvalidOperationException());
        await using var studio = new MobileStudioSession(vm, services,
            (_, _) => Task.FromResult<IReadOnlyList<string>>([]), () => ValueTask.CompletedTask);
        var view = new MobileConfigurationStudioView(studio, editedId, _ => Task.FromResult(false),
            () => Task.CompletedTask, () => throw new InvalidOperationException(), () => current);
        var window = new Window { Width = 390, Height = 768, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            Button? save = view.GetVisualDescendants().OfType<Button>().SingleOrDefault(b => b.Content as string == "Save and apply");
            if (!activeDraft)
            {
                Assert.Null(save);
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text =>
                    text.Text?.StartsWith("Save and open this configuration", StringComparison.Ordinal) == true);
                return;
            }
            Assert.NotNull(save);
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            Assert.Equal(1, commands.Saves);
            current = new(replacement);
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            Assert.Equal(1, commands.Saves);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text =>
                text.Text?.StartsWith("The active configuration changed", StringComparison.Ordinal) == true);
        }
        finally { window.Close(); }
    }

    private sealed class GroupCommands : IConsoleCommands, IConsoleGroupSettings
    {
        public int Saves { get; private set; }
        public IReadOnlyList<ConsoleGroupDefinitionSnapshot> SavedGroups => [new("Dispatch", false, [], false, false, 0)];
        public bool RestorePatchesOnStartup => false;
        public IReadOnlySet<string> EnabledPatchGroups { get; } = new HashSet<string>();
        public Task SaveGroupAsync(string name, IReadOnlyList<ChannelId> members, bool enabled, bool oneWay, CancellationToken cancellationToken = default)
        { Saves++; return Task.CompletedTask; }
        public Task SetRestorePatchesAsync(bool restore, CancellationToken cancellationToken = default) => Task.CompletedTask;
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

    [AvaloniaTheory]
    [InlineData(390)]
    [InlineData(600)]
    public void TouchCompanionEditorsUseReadableFieldsAndRestoreDesktopLayout(double width)
    {
        var model = new ConfigurationStudioViewModel(ConfigurationDocument.CreateNew(), null,
            "new.yml", new MobileStudioRuntimeContext(), new MaterializedConfigurationStudioCompanionSource(),
            new ConfigurationSnapshotPreviewFactory(), new(new Dictionary<string, ConfigurationStudioPosition>(),
                new Dictionary<string, string>(), []), ConfigurationStudioSection.EncryptionKeys);
        model.AddSystem();
        model.AddKey();
        model.SelectSection(ConfigurationStudioSection.EncryptionKeys);
        var view = new ConfigurationStudioView { UseTouchLayout = true, DataContext = model };
        var window = new Window { Width = width, Height = 600, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            var keys = view.FindControl<ConfigurationStudioKeysView>("KeysPage")!;
            var inspector = keys.FindControl<Border>("KeyInspector")!;
            Assert.Equal(0, Grid.GetColumn(inspector));
            Assert.Equal(1, Grid.GetRow(inspector));
            Assert.True(inspector.Bounds.Width >= width - 80);
            var material = keys.GetVisualDescendants().OfType<TextBox>().Single(box =>
                AutomationProperties.GetName(box) == "Encryption key material");
            Assert.True(material.Bounds.Height >= 44);
            Assert.True(material.Bounds.Width > width / 2);
            Assert.IsType<ScrollViewer>(keys.Content);
            var keyRow = keys.FindControl<ListBox>("KeyList")!.ContainerFromIndex(0)!;
            var key = model.KeyEntries.Single();
            Assert.Equal($"{key.Name}, {key.SystemDisplayName}, key {key.KeyIdText}", AutomationProperties.GetName(keyRow));

            model.SelectSection(ConfigurationStudioSection.Files);
            model.AddAlias();
            model.SelectedAlias!.Name = "Accessible alias";
            model.CommitAliasEdit();
            window.UpdateLayout();
            var files = view.FindControl<ConfigurationStudioFilesView>("FilesPage")!;
            var aliasList = files.GetVisualDescendants().OfType<ListBox>().Single();
            Assert.Equal($"RID {model.SelectedAlias.Rid}: Accessible alias",
                AutomationProperties.GetName(aliasList.ContainerFromIndex(0)!));
            Assert.Equal(1, Grid.GetRow(files.FindControl<ComboBox>("AliasOwner")!));
            Assert.Equal(2, Grid.GetRow(files.FindControl<WrapPanel>("AliasActions")!));
            view.UseTouchLayout = false;
            window.UpdateLayout();
            Assert.Equal(1, Grid.GetColumn(inspector));
            Assert.Equal(0, Grid.GetRow(inspector));
            Assert.IsType<Grid>(keys.Content);
            Assert.Equal(1, Grid.GetColumn(files.FindControl<ComboBox>("AliasOwner")!));
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeyMaterialFocusLossDoesNotReenterAlgorithmSelection(bool touchLayout)
    {
        var model = new ConfigurationStudioViewModel(ConfigurationDocument.CreateNew(), null,
            "new.yml", new MobileStudioRuntimeContext(), new MaterializedConfigurationStudioCompanionSource(),
            new ConfigurationSnapshotPreviewFactory(), new(new Dictionary<string, ConfigurationStudioPosition>(),
                new Dictionary<string, string>(), []), ConfigurationStudioSection.EncryptionKeys);
        model.AddSystem();
        model.AddKey();
        model.SelectSection(ConfigurationStudioSection.EncryptionKeys);
        var view = new ConfigurationStudioView { UseTouchLayout = touchLayout, DataContext = model };
        var window = new Window { Width = touchLayout ? 390 : 1000, Height = 900, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            var keys = view.FindControl<ConfigurationStudioKeysView>("KeysPage")!;
            var material = keys.GetVisualDescendants().OfType<TextBox>().Single(control =>
                AutomationProperties.GetName(control) == "Encryption key material");
            var algorithm = keys.GetVisualDescendants().OfType<ComboBox>().Single(control =>
                AutomationProperties.GetName(control) == "Encryption key algorithm");
            material.Focus();
            material.Text = new string('0', 64);
            algorithm.IsDropDownOpen = true;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            Assert.True(algorithm.IsDropDownOpen);
            Assert.Equal(new string('0', 64), model.KeyEntries.Single().Key);
            algorithm.IsDropDownOpen = false;
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task CommitRejectsARevisionNewerThanTheEditorBeforeStagingItsContents()
    {
        string root = Path.Combine(Path.GetTempPath(), $"studio-revision-{Guid.NewGuid():N}");
        try
        {
            var library = new ManagedConfigurationLibrary(Path.Combine(root, "Library"));
            var draft = await library.CreateDraftAsync("Revision");
            const string yaml = """
                systems:
                  - name: Simulator
                    identity: Console
                    address: 127.0.0.1
                    port: 62031
                    peerId: 1
                    rid: "1001"
                zones: []
                groups: []

                """;
            var original = await library.CommitAsync(draft with { Yaml = yaml });
            draft = await library.OpenDraftAsync(original.Reference.Id);
            var newer = await library.CommitAsync(draft with { Yaml = yaml + "customField: newer\n" });
            var service = new ConfigurationStudioCommitService(new UserSettingsStore(Path.Combine(root, "settings.json")), library);
            var plan = new ConfigurationSavePlan([new("codeplug.yml", yaml + "customField: stale\n", null, "Codeplug", false)], []);
            await Assert.ThrowsAsync<ConfigurationRevisionConflictException>(() => service.CommitAsync(
                plan, "codeplug.yml", false, original.Reference.Id,
                new(() => null, _ => throw new InvalidOperationException("Must not materialize."),
                    _ => throw new InvalidOperationException("Must not adopt settings."),
                    (_, _, _) => throw new InvalidOperationException("Must not accept save."),
                    _ => throw new InvalidOperationException("Must not commit."), original.Reference)));
            var retained = await library.OpenDraftAsync(original.Reference.Id);
            Assert.Equal(newer.Reference.Revision, retained.BasedOnRevision);
            Assert.Contains("newer", retained.Yaml);
            Assert.False(retained.IsDirty);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RecoveredDraftMaterializationOwnsCompanionsAndRetiresItsLease()
    {
        string root = Path.Combine(Path.GetTempPath(), $"mobile-draft-{Guid.NewGuid():N}");
        try
        {
            var library = new ManagedConfigurationLibrary(Path.Combine(root, "Library"));
            ConfigurationDraft initial = await library.CreateDraftAsync("Recovery");
            const string yaml = """
                systems:
                  - name: Simulator
                    identity: Console
                    address: 127.0.0.1
                    port: 62031
                    peerId: 1
                    rid: "1001"
                    aliasPath: ./alias.yml
                zones: []
                groups: []
                """;
            initial = await library.StageDraftAsync(initial with { Yaml = yaml, IsDirty = true },
                new Dictionary<string, ReadOnlyMemory<byte>> { ["alias.yml"] = "[]\n"u8.ToArray() });
            ConfigurationCommit saved = await library.CommitAsync(initial);
            ConfigurationDraft draft = await library.OpenDraftAsync(saved.Reference.Id);
            await library.StageDraftAsync(draft with { Yaml = yaml.Replace("Console", "Recovered"), IsDirty = true },
                new Dictionary<string, ReadOnlyMemory<byte>> { ["alias.yml"] = "- id: 1\n  name: Recovered\n"u8.ToArray() });
            var materializer = new ManagedConfigurationMaterializer(library, Path.Combine(root, "Runtime"));
            var recovery = await materializer.MaterializeDraftAsync(saved.Reference.Id);
            string path = recovery.Lease.Path;
            await using (recovery.Lease)
            {
                Assert.True(recovery.Draft.IsDirty);
                Assert.Contains("Recovered", await File.ReadAllTextAsync(path));
                Assert.Contains("Recovered", await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(path)!, "alias.yml")));
            }
            Assert.False(File.Exists(path));
            Assert.True((await library.OpenDraftAsync(saved.Reference.Id)).IsDirty);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [AvaloniaTheory]
    [InlineData(390, 844)]
    [InlineData(402, 874)]
    [InlineData(852, 390)]
    [InlineData(1024, 768)]
    public async Task TouchStudioEditsUndoRedoReviewsAndCommitsWithoutReload(double width, double height)
    {
        string root = Path.Combine(Path.GetTempPath(), $"mobile-studio-{Guid.NewGuid():N}");
        Window? window = null;
        try
        {
            var library = new ManagedConfigurationLibrary(Path.Combine(root, "Library"));
            ConfigurationDraft draft = await library.CreateDraftAsync("Touch Studio");
            ConfigurationCommit original = await library.CommitAsync(draft with { Yaml = """
                systems:
                  - name: Simulator
                    identity: Console
                    address: 127.0.0.1
                    port: 62031
                    peerId: 1
                    rid: "1001"
                zones:
                  - name: Test
                    channels:
                      - name: Test channel
                        system: Simulator
                        tgid: 100
                        mode: p25
                groups: []
                """ });
            await library.ActivateAsync(original.Reference);
            var materializer = new ManagedConfigurationMaterializer(library, Path.Combine(root, "Runtime"));
            await using var lease = await materializer.MaterializeAsync(original.Reference);
            var vm = new ConfigurationStudioViewModel(ConfigurationDocument.Open(lease.Path), original.Reference.Id,
                lease.Path, new MobileStudioRuntimeContext(), new MaterializedConfigurationStudioCompanionSource(),
                new ConfigurationSnapshotPreviewFactory(), new(new Dictionary<string, ConfigurationStudioPosition>(),
                    new Dictionary<string, string>(), []), ConfigurationStudioSection.Systems);
            var settings = new UserSettingsStore(Path.Combine(root, "settings.json"));
            var planner = new ConfigurationStudioSavePlanner(vm, settings);
            var commit = new ConfigurationStudioCommitService(settings, library);
            var saved = new TaskCompletionSource<ConfigurationReference>(TaskCreationOptions.RunContinuationsAsynchronously);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int commits = 0, reloads = 0, disposals = 0;
            bool holdDisposal = false;
            var disposalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var services = new ConfigurationStudioSaveServices(() => Task.CompletedTask, () => library.Active,
                copy => planner.CreatePlan(lease.Path, copy), planner.BuildReviewText,
                async (plan, copy, durable) =>
                {
                    commits++;
                    var result = await commit.CommitAsync(plan, lease.Path, copy, original.Reference.Id,
                        new(() => library.Active, reference => materializer.MaterializeAsync(reference),
                            _ => Task.CompletedTask,
                            (path, reference, accepted) => vm.AcceptSaved(path, reference.Id, accepted), durable));
                    saved.TrySetResult(result.Reference);
                    return result;
                });
            await using var session = new MobileStudioSession(vm, services,
                (_, _) => Task.FromResult<IReadOnlyList<string>>([]),
                async () =>
                {
                    disposals++;
                    disposalStarted.TrySetResult();
                    if (holdDisposal) await releaseDisposal.Task.ConfigureAwait(false);
                });
            var view = new MobileConfigurationStudioView(session, original.Reference.Id,
                _ => { reloads++; return Task.FromResult(true); },
                () => { closed.TrySetResult(); return Task.CompletedTask; },
                () => new ConfigurationExportArchive(Path.Combine(root, "Exports")));
            window = new Window { Width = width, Height = height, Content = view };
            window.Show(); Dispatcher.UIThread.RunJobs();
            var editor = Assert.Single(view.GetVisualDescendants().OfType<ConfigurationStudioView>());
            Assert.True(editor.Bounds.Height > 180);
            Grid pages = editor.FindControl<Grid>("PageHost")!;
            Assert.True(pages.Bounds.Height >= height - 180, $"Editor area collapsed to {pages.Bounds.Height} at {width}x{height}.");
            var navigation = editor.FindControl<ConfigurationStudioNavigationView>("Navigation")!;
            Assert.False(navigation.IsVisible);
            view.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Browse"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(navigation.IsVisible);
            navigation.FindControl<Button>("SystemsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.False(navigation.IsVisible);
            vm.SelectedChannel = vm.Configuration.Zones[0].Channels[0];
            var selectedNode = Assert.IsType<ConfigurationHierarchyNode>(vm.SelectedHierarchyNode);
            vm.SelectSection(ConfigurationStudioSection.Overview);
            Dispatcher.UIThread.RunJobs();
            navigation.FindControl<TreeView>("ConfigurationTree")!.RaiseEvent(
                new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Assert.True(vm.IsZones);
            Assert.Same(selectedNode, vm.SelectedHierarchyNode);
            Assert.False(vm.IsDirty);
            vm.SelectSection(ConfigurationStudioSection.Zones);
            Dispatcher.UIThread.RunJobs();
            if (height < 500)
            {
                Assert.False(vm.IsZonePreviewExpanded);
                ListBox channelRows = editor.ZonesView.FindControl<ListBox>("narrowChannelList")!;
                Assert.True(channelRows.IsEffectivelyVisible);
                Assert.True(channelRows.Bounds.Height >= 44, $"Channel selection collapsed to {channelRows.Bounds.Height}.");
            }
            vm.SelectSection(ConfigurationStudioSection.Systems);
            vm.Configuration.Systems[0].Identity = "Touch Console";
            vm.CommitFieldEdit();
            Assert.True(vm.IsDirty);
            Button actions = editor.FindControl<Button>("TouchActionsButton")!;
            var flyout = Assert.IsType<Flyout>(actions.Flyout);
            flyout.ShowAt(actions);
            Dispatcher.UIThread.RunJobs();
            var actionItems = Assert.IsType<StackPanel>(flyout.Content);
            Button undo = actionItems.Children.OfType<Button>().Single(button => Equals(button.Content, "Undo"));
            Assert.True(undo.IsEnabled);
            undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Console", vm.Configuration.Systems[0].Identity);
            Button redo = actionItems.Children.OfType<Button>().Single(button => Equals(button.Content, "Redo"));
            Assert.True(redo.IsEnabled);
            redo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Touch Console", vm.Configuration.Systems[0].Identity);
            flyout.Hide();
            Button review = view.GetVisualDescendants().OfType<Button>().Single(button =>
                button.IsEffectivelyVisible && AutomationProperties.GetName(button) == "Review and save configuration");
            review.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, commits);
            Button accept = view.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Save"));
            Assert.True(accept.Bounds.Height >= 44);
            Assert.True(accept.TranslatePoint(default, view)!.Value.Y + accept.Bounds.Height <= height);
            accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ConfigurationReference changed = await saved.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, commits);
            Assert.NotEqual(original.Reference.Revision, changed.Revision);
            Assert.Equal(original.Reference, library.Active);
            Assert.False(vm.IsDirty);
            Button cancel = view.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Cancel"));
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Equal(0, reloads);
            var back = view.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "‹ Library"));
            holdDisposal = true;
            try
            {
                back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await disposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
                Task repeatedClose = session.DisposeAsync().AsTask();
                Assert.False(repeatedClose.IsCompleted);
                Assert.False(closed.Task.IsCompleted);
                releaseDisposal.TrySetResult();
                await repeatedClose.WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally { releaseDisposal.TrySetResult(); }
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(1, disposals);
            ConfigurationDraft reopened = await library.OpenDraftAsync(changed.Id);
            Assert.Contains("Touch Console", reopened.Yaml);
        }
        finally
        {
            window?.Close();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
