// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using DvmConsole.Application;
using DvmConsole.Core.Runtime;
using DvmConsole.Presentation;
using DvmConsole.Operations;

namespace DvmConsole.Mobile;

/// <summary>
/// Mobile renderers observe a shared application session; an offline fixture is
/// available before a configuration is opened. The view constructs no host services.
/// </summary>
public sealed class MobileConsoleView : UserControl, IAsyncDisposable
{
    public event EventHandler? SettingsRequested;
    public event EventHandler? ConfigurationRequested;
    private readonly Control? welcome;

    private readonly ConsoleHostFormFactor formFactor;
    private readonly IConsoleApplicationSession session;
    private readonly string layoutKey;
    private readonly IConsoleConnectionStateSource? connections;
    private static readonly IBrush ConnectedBorder = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#00BE5A"));
    private static readonly IBrush FailedBorder = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#E05252"));
    private static readonly IBrush IdleBorder = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#8794A1"));
    private static readonly IBrush ConnectingBorder = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#E5A93C"));
    private readonly IConsoleConnectionCommands? connectionCommands;
    private readonly CancellationTokenSource connectionCancellation = new();
    private Task connectionOperation = Task.CompletedTask;
    private bool connectionBusy;
    private readonly StackPanel connectionPills = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly Dictionary<SystemId, Button> connectionButtons = [];

    private readonly Avalonia.Controls.Shapes.Ellipse statusDot = new() { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
    private string connectionSummary = string.Empty;
    private bool connectionHealthy;
    private System.Collections.Immutable.ImmutableArray<RadioConnectionSnapshot> displayedConnectionStates;
    private readonly TextBlock sessionStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly bool ownsSession;
    private Task? disposal;
    private int headerRefreshPending;
    private int accepting = 1;
    private readonly ChannelPttController ptt;
    private readonly ConsoleExecutionPolicy execution;
    private readonly ContentControl renderer = new();
    private readonly ChannelListView list = new() { AdaptToNarrowWidth = true };
    private readonly Dictionary<ChannelId, Control> cardControls = [];
    private readonly ScrollViewer cards;
    private readonly MobileCardNavigation? cardNavigation;
    private ConsoleListViewModel? cardPresentation;
    private readonly Action ensureCards;
    private static readonly StyledProperty<double> PreferredTextScaleProperty =
        AvaloniaProperty.Register<MobileConsoleView, double>("PreferredTextScale", 1);
    private readonly List<ChannelSnapshotCardViewModel> cardModels = [];
    private readonly List<MobilePttButtonInput> pttInputs = [];
    private readonly Button cardsButton = new() { Content = "Cards", MinHeight = 44 };
    private readonly Button listButton = new() { Content = "List", MinHeight = 44 };
    private readonly Control speakerIcon = MobileConsoleIcons.Speaker(false);
    private readonly Control mutedSpeakerIcon = MobileConsoleIcons.Speaker(true);
    private readonly Button alertButton = new() { Content = MobileConsoleIcons.Alert(), Width = 44, Height = 44, Padding = new Thickness(0) };
    private readonly IConsoleToneSettingsStore? toneSettings;
    private readonly CancellationTokenSource alertCancellation = new();
    private bool sendingAlert;
    private Task alertOperation = Task.CompletedTask;
    private readonly Button muteButton = new() { Width = 44, Height = 44, Padding = new Thickness(0) };
    private readonly Button selectedPttButton = new() { MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock layoutHint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly IMobileLayoutPreferences? layoutPreferences;
    private ConsoleRendererPreference preference;
    private readonly IMobilePttPreferences? pttPreferences;
    private bool changingPttMode;
    private long manualInputGeneration;
    private bool selectedGestureRequested;
    private bool selectedStartPending;
    public bool TogglePttMode { get; private set; }

    public MobileConsoleView(ConsoleHostFormFactor formFactor, IMobileLayoutPreferences? layoutPreferences = null, ConsoleExecutionPolicy? execution = null,
        IConsoleApplicationSession? applicationSession = null, bool ownsSession = true, IConsoleToneSettingsStore? toneSettings = null, IConsoleConnectionStateSource? connectionStates = null, IConsoleConnectionCommands? connectionCommands = null, Func<CancellationToken, Task>? resumeListening = null)
    {
        Resources.MergedDictionaries.Add(new MobileConsoleTheme());
        this.Bind(FontFamilyProperty, this.GetResourceObservable("MobileSystemFontFamily",
            value => value as FontFamily ?? FontFamily.Default));
        list.Classes.Add("touch");
        this.toneSettings = toneSettings;
        this.execution = execution ?? new ConsoleExecutionPolicy();
        this.formFactor = formFactor;
        this.layoutPreferences = layoutPreferences;
        this.ownsSession = ownsSession;
        session = applicationSession ?? CreatePreviewSession();
        layoutKey = session.Topology.Configuration?.Id.Value.ToString("N") ?? "preview";
        pttPreferences = layoutPreferences as IMobilePttPreferences;
        TogglePttMode = pttPreferences?.ReadTogglePtt(layoutKey) ?? false;
        preference = formFactor == ConsoleHostFormFactor.Phone
            ? ConsoleRendererPreference.List
            : layoutPreferences?.Read(layoutKey) ?? ConsoleRendererPreference.Cards;
        IConsoleCommands commands = session.Commands;
        connections = connectionStates ?? commands as IConsoleConnectionStateSource;
        this.connectionCommands = connectionCommands ?? connections as IConsoleConnectionCommands;
        list.ConnectionToggleRequested += RequestConnectionToggle;
        if (connections is IConsoleConnectionStateNotifications notifications)
            notifications.ConnectionStatesChanged += OnConnectionStatesChanged;
        ptt = new ChannelPttController(async (channel, cancellationToken) =>
        {
            if (Volatile.Read(ref accepting) == 0 || changingPttMode) return false;
            long generation = manualInputGeneration;
            ConsoleExecutionLease? lease = this.execution.TryAcquire(ConsoleTransmitIntent.Manual);
            if (lease is null) return false;
            bool started = await commands.BeginPttAsync(channel, cancellationToken);
            if (started && (Volatile.Read(ref accepting) == 0 || generation != manualInputGeneration || !this.execution.IsCurrent(lease.Value)))
            {
                await commands.EndPttAsync(channel, CancellationToken.None);
                return false;
            }
            return started;
        }, commands.EndPttAsync);
        list.AllowMultipleColumns = formFactor == ConsoleHostFormFactor.Tablet;
        list.Attach(session, ptt, () => TogglePttMode);
        foreach (var item in ((ConsoleListViewModel)list.DataContext!).Items) item.UseTouchText = true;
        var placementPreferences = layoutPreferences as IMobileCardLayoutPreferences;
        list.EnableRowReordering = true;
        ((ConsoleListViewModel)list.DataContext!).RestoreOrder(placementPreferences?.ReadListOrder(layoutKey) ?? []);
        list.RowOrderChanged += order => placementPreferences?.WriteListOrder(layoutKey, order);
        var cardGrid = new MobileCardGrid(session.Topology, cardControls);
        var reorder = new MobileCardReordering(cardGrid, cardControls,
            positions => placementPreferences?.WriteCardPositions(layoutKey, positions));
        // A tablet saved in List mode needs no second model or hidden card tree.
        ensureCards = () =>
        {
            if (cardPresentation is not null || formFactor == ConsoleHostFormFactor.Phone) return;
            cardPresentation = new ConsoleListViewModel(session, ptt);
            foreach (ChannelListItemViewModel item in cardPresentation?.Items.AsEnumerable() ?? Enumerable.Empty<ChannelListItemViewModel>())
            {
                var model = new ChannelSnapshotCardViewModel(item, session.Commands as IConsoleRecordingCommands, useTouchPalette: true);
                model.SetDarkMode(ActualThemeVariant == ThemeVariant.Dark);
                cardModels.Add(model);
                var content = new ChannelCardContent { DataContext = model, UseTouchLayout = true };
                pttInputs.Add(new MobilePttButtonInput(content.FindControl<Button>("PttButton")!,
                    () => ptt.PressAsync(item.Id), () => ptt.ReleaseAsync(item.Id),
                    () => ptt.ToggleAsync(item.Id), () => ptt.UnkeyAsync(item.Id),
                    exception => sessionStatus.Text = exception.Message,
                    () => TogglePttMode, () => item.IsTransmitting));
                content.TransmitSelectionClick += async (_, _) => await model.ToggleTransmitSelectionAsync();
                content.PageSelectionClick += async (_, _) => await model.TogglePageSelectionAsync();
                content.AlertSelectionClick += async (_, _) => await model.ToggleAlertSelectionAsync();
                content.Bind(ChannelCardContent.UiFontSizeProperty, content.GetResourceObservable("MobileChannelNameFontSize", value => value is double size ? size : 16d));
                content.Bind(ChannelCardContent.UiSmallFontSizeProperty, content.GetResourceObservable("ChannelListDetailFontSize", value => value is double size ? size : 13d));
                var card = new Grid();
                card.Children.Add(content);
                if (applicationSession is not null)
                {
                    // Only the title/caller area toggles RX; volume and transmit controls
                    // retain their own gestures and established card geometry.
                    var listen = new Button
                    {
                        Height = 44,
                        VerticalAlignment = VerticalAlignment.Top,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        Margin = new Thickness(0, 0, 80, 0),
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        Padding = new Thickness(0)
                    };
                    listen.Classes.Add("card-listen");
                    AutomationProperties.SetName(listen, $"{item.Name} toggle listening");
                    listen.Click += async (_, _) =>
                    {
                        if (Volatile.Read(ref accepting) == 0) return;
                        try { await item.ToggleReceiveAsync(); }
                        catch (Exception exception) { sessionStatus.Text = exception.Message; }
                    };
                    reorder.Attach(listen, item.Id);
                    card.Children.Add(listen);
                }
                var border = new Border
                {
                    Width = Math.Max(300, model.CardWidth),
                    Padding = new Thickness(12),
                    Margin = new Thickness(0, 0, 0, 10),
                    CornerRadius = new CornerRadius(10),
                    DataContext = model,
                    BorderThickness = new Thickness(1),
                    Child = card
                };
                void RefreshBorder()
                {
                    border.Background = model.CardBackgroundBrush;
                    border.BorderBrush = model.CardBorderBrush;
                }
                model.PropertyChanged += (_, change) =>
                {
                    if (string.IsNullOrEmpty(change.PropertyName) || change.PropertyName is nameof(ChannelSnapshotCardViewModel.CardBackgroundBrush) or nameof(ChannelSnapshotCardViewModel.CardBorderBrush))
                        RefreshBorder();
                };
                RefreshBorder();
                cardControls.Add(item.Id, border);
                cardGrid.Children.Add(border);
            }
            cardGrid.RestoreInitialOrder((layoutPreferences as IMobileCardOrderPreferences)?.ReadCardOrder(layoutKey) ?? []);
            cardGrid.Restore(placementPreferences?.ReadCardPositions(layoutKey) ?? new Dictionary<string, MobileCardPosition>());
            cardNavigation?.RefreshSelection();
            if (applicationSession is null) DisablePreviewCommands(cardGrid);
        };
        if (formFactor == ConsoleHostFormFactor.Tablet)
        {
            cardNavigation = new MobileCardNavigation(session.Topology, cardControls);
            cardNavigation.RefreshActivity(session.Snapshot);
        }
        cards = new ScrollViewer
        {
            Content = cardGrid,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        cards.PropertyChanged += (_, args) =>
        {
            if (args.Property == ScrollViewer.ViewportProperty) cardGrid.SetViewport(cards.Viewport);
        };
        if (applicationSession is null)
        {
            DisablePreviewCommands(list);
            DisablePreviewCommands(cards);
        }
        if (session.Topology.Configuration is null && session.Topology.Channels.Count == 0)
        {
            var configure = new Button
            {
                Content = "Create or import configuration",
                MinHeight = 48,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            AutomationProperties.SetName(configure, "Create or import configuration");
            configure.Click += (_, _) => ConfigurationRequested?.Invoke(this, EventArgs.Empty);
            var introduction = new StackPanel { Spacing = 16, Margin = new Thickness(12) };
            introduction.Children.Add(new TextBlock
            {
                Text = "Welcome to Console NEO",
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap
            }.WithScaledFontSize(22));
            introduction.Children.Add(new TextBlock
            {
                Text = "Start a new configuration in Studio, or import an existing configuration from Files.",
                TextWrapping = TextWrapping.Wrap
            });
            introduction.Children.Add(configure);
            welcome = new ScrollViewer
            {
                Content = introduction,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            };
        }
        cardsButton.Click += (_, _) => SelectRenderer(ConsoleRendererPreference.Cards);
        listButton.Click += (_, _) => SelectRenderer(ConsoleRendererPreference.List);
        AutomationProperties.SetName(cardsButton, "Show channel cards");
        AutomationProperties.SetName(listButton, "Show channel list");
        var header = new StackPanel();
        var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto,Auto"), MinHeight = 56, Margin = new Thickness(16, 0) };
        var title = new TextBlock
        {
            Name = "ConsoleTitle",
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Inlines = new Avalonia.Controls.Documents.InlineCollection { new Avalonia.Controls.Documents.Run("DVM Console "),
                new Avalonia.Controls.Documents.Run("NEO") { Foreground = new SolidColorBrush(Color.Parse("#22D3EE")) } }
        }.WithScaledFontSize(19);
        if (formFactor == ConsoleHostFormFactor.Phone)
        {
            titleRow.ColumnDefinitions[0].Width = GridLength.Star;
            titleRow.ColumnDefinitions[1].Width = new GridLength(0);
        }
        titleRow.Children.Add(title);
        var pillScroll = new ScrollViewer
        {
            Content = connectionPills,
            Margin = new Thickness(12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        Grid.SetColumn(pillScroll, 1);
        titleRow.Children.Add(pillScroll);
        var settings = new Button
        {
            Content = MobileConsoleIcons.Settings(),
            Width = 44,
            Height = 44,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(settings, "Open Settings");
        ToolTip.SetTip(settings, "Settings");
        settings.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        Grid.SetColumn(settings, 5);
        titleRow.Children.Add(settings);
        foreach (var button in new[] { settings, alertButton, muteButton })
        {
            button.Background = Brushes.Transparent;
            button.BorderThickness = new Thickness(0);
            button.CornerRadius = new CornerRadius(8);
        }
        muteButton.IsVisible = commands is IConsoleListeningSettings;
        muteButton.Click += async (_, _) =>
        {
            if (Volatile.Read(ref accepting) == 0 || commands is not IConsoleListeningSettings listening) return;
            muteButton.IsEnabled = false;
            try
            {
                if (this.execution.Snapshot.State == ConsoleExecutionState.RequiresResume && resumeListening is not null)
                    await resumeListening(CancellationToken.None);
                else
                    await listening.SetOutputMutedAsync(!listening.OutputMuted);
            }
            catch (Exception exception) { sessionStatus.Text = exception.Message; }
            finally { UpdateMuteButton(); }
        };
        alertButton.IsVisible = toneSettings is not null;
        AutomationProperties.SetName(alertButton, "Send assigned ALERT tone pattern");
        ToolTip.SetTip(alertButton, "Assign a pattern in Settings → Tones");
        alertButton.Click += async (_, _) => await (alertOperation = SendAssignedAlertAsync());
        Grid.SetColumn(alertButton, 3);
        titleRow.Children.Add(alertButton);
        Grid.SetColumn(muteButton, 4);
        titleRow.Children.Add(muteButton);
        UpdateMuteButton();
        var titleBorder = new Border { Child = titleRow, BorderThickness = new Thickness(0, 0, 0, 1) };
        titleBorder.Bind(Border.BorderBrushProperty, this.GetResourceObservable("MobileRibbonBorderBrush"));
        header.Children.Add(titleBorder);
        sessionStatus.Text = applicationSession is null
            ? "Offline preview · Example channels. Radio/audio disconnected."
            : welcome is not null ? "No configuration is open." : session.Snapshot.StatusText;
        session.SnapshotChanged += OnSnapshotChanged;
        // Keep the selected-transmit action beside status in a stable header row.
        // Selection changes must not move channel controls under a held finger.
        var statusRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,88"), ColumnSpacing = 8, MinHeight = 48, Margin = new Thickness(16, 0) };
        sessionStatus.WithScaledFontSize(13);
        sessionStatus.MaxLines = 1;
        sessionStatus.TextTrimming = TextTrimming.CharacterEllipsis;
        sessionStatus.VerticalAlignment = VerticalAlignment.Center;
        var statusContent = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 6 };
        statusContent.Children.Add(statusDot);
        Grid.SetColumn(sessionStatus, 1);
        statusContent.Children.Add(sessionStatus);
        var receiveNavigation = new Button
        {
            Content = statusContent,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(receiveNavigation, "Go to first receiving channel");
        receiveNavigation.Click += (_, _) => RevealFirstReceivingChannel();
        statusRow.Children.Add(receiveNavigation);
        sessionStatus.PropertyChanged += (_, args) =>
        {
            if (args.Property == TextBlock.TextProperty) UpdateStatusColor();
        };
        Grid.SetColumn(selectedPttButton, 1);
        statusRow.Children.Add(selectedPttButton);
        selectedPttButton.Width = 64;
        selectedPttButton.HorizontalAlignment = HorizontalAlignment.Right;
        selectedPttButton.CornerRadius = new CornerRadius(6);
        selectedPttButton.Padding = new Thickness(4, 0);
        var statusBorder = new Border { Child = statusRow, BorderThickness = new Thickness(0, 0, 0, 1) };
        statusBorder.Bind(Border.BorderBrushProperty, this.GetResourceObservable("MobileRibbonBorderBrush"));
        header.Children.Add(statusBorder);
        pttInputs.Add(new MobilePttButtonInput(selectedPttButton,
            BeginSelectedPttAsync, EndSelectedPttAsync, ToggleSelectedPttAsync, EndSelectedPttAsync,
            exception => sessionStatus.Text = exception.Message, () => TogglePttMode,
            () => session.Commands is IConsoleSelectedTransmitCommands { IsSelectedPttRequested: true }));
        UpdateSelectedPttButton();

        var selector = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(8, 0),
            IsVisible = formFactor == ConsoleHostFormFactor.Tablet
        };
        selector.Children.Add(cardsButton);
        selector.Children.Add(listButton);
        Grid.SetColumn(selector, 2);
        titleRow.Children.Add(selector);
        header.Children.Add(layoutHint);
        if (formFactor == ConsoleHostFormFactor.Tablet)
        {
            foreach (var system in session.Topology.Systems)
            {
                var pill = new Button { MinHeight = 44, Padding = new Thickness(10, 4), CornerRadius = new CornerRadius(7), HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(1) };
                pill.Click += (_, _) => RequestConnectionToggle(system.Id);
                connectionButtons.Add(system.Id, pill);
                connectionPills.Children.Add(pill);
            }
        }

        if (cardNavigation is not null) header.Children.Add(cardNavigation);
        var grid = new Grid { RowDefinitions = RowDefinitions.Parse("Auto,*") };
        grid.Children.Add(header);
        Grid.SetRow(renderer, 1);
        renderer.Margin = new Thickness(16, 12, 16, 8);
        grid.Children.Add(renderer);
        Content = grid;
        this.Bind(BackgroundProperty, this.GetResourceObservable("ShellBackgroundBrush"));
        ActualThemeVariantChanged += (_, _) => { UpdateCardAppearance(); UpdateStatusColor(); };
        RefreshConnectionSummary();
        UpdateCardAppearance();
        this.Bind(PreferredTextScaleProperty, this.GetResourceObservable("MobileTextScale",
            value => value is double scale ? scale : 1d));
        PropertyChanged += (_, change) =>
        {
            if (change.Property == PreferredTextScaleProperty) UpdateRenderer();
        };
        SizeChanged += (_, _) => UpdateRenderer();
        DetachedFromVisualTree += async (_, _) => await ObserveManualReleaseAsync();
        UpdateRenderer();
    }

    private void UpdateCardAppearance()
    {
        foreach (var model in cardModels) model.SetDarkMode(ActualThemeVariant == ThemeVariant.Dark);
    }

    public ConsoleRendererPreference SavedPreference => preference;
    public ConsoleRendererPreference EffectiveRenderer { get; private set; }

    public async ValueTask SetTogglePttModeAsync(bool enabled)
    {
        Avalonia.Threading.Dispatcher.UIThread.VerifyAccess();
        if (TogglePttMode == enabled || Volatile.Read(ref accepting) == 0) return;
        if (changingPttMode) throw new InvalidOperationException("PTT mode is already changing.");
        changingPttMode = true;
        try
        {
            await ReleaseManualTransmitAsync();
            if (Volatile.Read(ref accepting) == 0) return;
            pttPreferences?.WriteTogglePtt(layoutKey, enabled);
            TogglePttMode = enabled;
            UpdateSelectedPttButton();
        }
        finally { changingPttMode = false; }
    }

    private async Task ObserveManualReleaseAsync()
    {
        if (disposal is not null) return;
        try { await ReleaseManualTransmitAsync(); }
        catch (Exception exception) { sessionStatus.Text = exception.Message; }
    }

    internal void SetAdmission(bool enabled)
    {
        Volatile.Write(ref accepting, enabled ? 1 : 0);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => IsEnabled = enabled);
    }

    public ValueTask ReleaseManualTransmitAsync()
        => new(Avalonia.Threading.Dispatcher.UIThread.CheckAccess()
            ? ReleaseManualCoreAsync()
            : Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(ReleaseManualCoreAsync));

    private async Task ReleaseManualCoreAsync()
    {
        manualInputGeneration++;
        selectedGestureRequested = false;
        ValueTask channelRelease = ptt.ReleaseAllAsync();
        try
        {
            if (session.Commands is IConsoleSelectedTransmitCommands selected)
                await selected.EndSelectedPttAsync(CancellationToken.None);
        }
        finally { await channelRelease; }
    }

    public async ValueTask DisposeAsync()
        => await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => disposal ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        Volatile.Write(ref accepting, 0);
        connectionCancellation.Cancel();
        await connectionOperation;
        connectionCancellation.Dispose();
        list.ConnectionToggleRequested -= RequestConnectionToggle;
        alertCancellation.Cancel();
        await alertOperation;
        alertCancellation.Dispose();
        session.SnapshotChanged -= OnSnapshotChanged;
        if (connections is IConsoleConnectionStateNotifications notifications)
            notifications.ConnectionStatesChanged -= OnConnectionStatesChanged;
        var cleanup = new AsyncCleanup();
        await cleanup.RunTaskAsync(() => ReleaseManualTransmitAsync().AsTask());
        await cleanup.RunTaskAsync(() => list.DetachAsync().AsTask());
        foreach (var input in pttInputs) cleanup.Run(input.Dispose);
        foreach (var model in cardModels) cleanup.Run(model.Dispose);
        if (cardPresentation is not null)
            await cleanup.RunTaskAsync(() => cardPresentation.DisposeAsync().AsTask());
        await cleanup.RunTaskAsync(() => ptt.DisposeAsync().AsTask());
        if (ownsSession) await cleanup.RunTaskAsync(() => session.DisposeAsync().AsTask());
        cleanup.ThrowIfFailed();
    }

    public void SelectRenderer(ConsoleRendererPreference selected)
    {
        if (formFactor == ConsoleHostFormFactor.Phone)
            return;
        if (!Enum.IsDefined(selected))
            throw new ArgumentOutOfRangeException(nameof(selected));
        if (preference == selected)
            return;
        layoutPreferences?.Write(layoutKey, selected);
        preference = selected;
        UpdateRenderer();
    }

    private void UpdateRenderer()
    {
        ResponsivePresentation presentation = ResponsivePresentationPolicy.Resolve(Bounds.Width, preference, formFactor);
        EffectiveRenderer = presentation.EffectiveRenderer;
        if (welcome is null && EffectiveRenderer == ConsoleRendererPreference.Cards) ensureCards();
        double textScale = GetValue(PreferredTextScaleProperty);
        foreach (var card in cardControls.Values)
        {
            double baseline = card.DataContext is ChannelSnapshotCardViewModel model ? Math.Max(300, model.CardWidth) : 300;
            card.Width = Math.Min(baseline * textScale, Math.Max(300, Bounds.Width - 32));
        }
        connectionPills.IsVisible = welcome is null && EffectiveRenderer == ConsoleRendererPreference.Cards && connectionButtons.Count > 0;
        if (cardNavigation is not null) cardNavigation.IsVisible = welcome is null && EffectiveRenderer == ConsoleRendererPreference.Cards;
        cardPresentation?.SetPresentationActive(EffectiveRenderer == ConsoleRendererPreference.Cards);
        (list.DataContext as ConsoleListViewModel)?.SetPresentationActive(EffectiveRenderer == ConsoleRendererPreference.List);
        Control next = welcome ?? (EffectiveRenderer == ConsoleRendererPreference.Cards ? cards : list);
        if (!ReferenceEquals(renderer.Content, next))
        {
            if (renderer.Content is not null) _ = ObserveManualReleaseAsync();
            renderer.Content = next;
        }
        cardsButton.IsVisible = welcome is null && formFactor == ConsoleHostFormFactor.Tablet;
        listButton.IsVisible = welcome is null && formFactor == ConsoleHostFormFactor.Tablet;
        cardsButton.IsEnabled = Bounds.Width >= ResponsivePresentationPolicy.NarrowMinimum &&
            EffectiveRenderer != ConsoleRendererPreference.Cards;
        listButton.IsEnabled = EffectiveRenderer != ConsoleRendererPreference.List;
        layoutHint.IsVisible = welcome is null && formFactor == ConsoleHostFormFactor.Tablet &&
            Bounds.Width < ResponsivePresentationPolicy.NarrowMinimum;
        layoutHint.Text = formFactor == ConsoleHostFormFactor.Tablet &&
            Bounds.Width < ResponsivePresentationPolicy.NarrowMinimum
            ? "List fits this window. Your layout choice returns when there is more room."
            : EffectiveRenderer == ConsoleRendererPreference.List ? "Channel list" : "Channel cards";
    }

    private static void DisablePreviewCommands(Control view)
    {
        view.Styles.Add(new Style(selector => selector.OfType<Button>())
        {
            Setters = { new Setter(IsEnabledProperty, false) }
        });
        view.Styles.Add(new Style(selector => selector.Is<Slider>())
        {
            Setters = { new Setter(IsEnabledProperty, false) }
        });
    }

    public IConsoleApplicationSession ApplicationSession => session;
    internal ConsoleExecutionPolicy Execution => execution;

    private void OnSnapshotChanged(object? sender, ConsoleSnapshotChangedEventArgs args) => QueueHeaderRefresh();
    private void OnConnectionStatesChanged(object? sender, EventArgs args) => QueueHeaderRefresh();
    private void QueueHeaderRefresh()
    {
        if (Interlocked.Exchange(ref headerRefreshPending, 1) == 0)
            Avalonia.Threading.Dispatcher.UIThread.Post(RefreshHeader);
    }

    private async Task SendAssignedAlertAsync()
    {
        if (sendingAlert || toneSettings is null || Volatile.Read(ref accepting) == 0 || !execution.Snapshot.CanSendTones) return;
        sendingAlert = true;
        alertButton.IsEnabled = false;
        long generation = execution.Snapshot.ManualGeneration;
        try
        {
            var settings = await toneSettings.LoadToneSettingsAsync(alertCancellation.Token);
            if (Volatile.Read(ref accepting) == 0 || !execution.Snapshot.CanSendTones || execution.Snapshot.ManualGeneration != generation) return;
            if (session.Commands is not IConsoleToneCommands tones) return;
            if (settings.MobileAlertBuiltIn is { } builtIn && Enum.IsDefined(typeof(DvmConsole.Audio.LegacyAlertTone), builtIn))
                await tones.SendToneAsync(DvmConsole.Audio.LegacyAlertToneGenerator.CreateSequence((DvmConsole.Audio.LegacyAlertTone)builtIn), ConsoleToneTargets.Alert, alertCancellation.Token);
            else if (Guid.TryParse(settings.MobileAlertAssetId, out var asset))
                await tones.SendAlertAudioAsync(new AssetId(asset), alertCancellation.Token);
            else if (settings.TonePresets.FirstOrDefault(item => item.Name == settings.MobileAlertPresetName) is { } preset)
                await tones.SendToneAsync(ToneEditorSequences.TonePreset(new TonePresetViewModel(preset)), ConsoleToneTargets.Alert, alertCancellation.Token);
            else sessionStatus.Text = "Assign the bell button in Settings → Tones and Paging → Main ALERT pattern.";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { sessionStatus.Text = exception.Message; }
        finally { sendingAlert = false; alertButton.IsEnabled = Volatile.Read(ref accepting) != 0; }
    }

    private void RefreshHeader()
    {
        Interlocked.Exchange(ref headerRefreshPending, 0);
        if (disposal is not null) return;
        RefreshConnectionSummary();
        cardNavigation?.RefreshActivity(session.Snapshot);
        UpdateMuteButton();
        UpdateSelectedPttButton();
    }

    private void RequestConnectionToggle(SystemId id)
    {
        if (connectionCommands is null || connectionBusy || Volatile.Read(ref accepting) == 0) return;
        connectionBusy = true;
        connectionOperation = ToggleConnectionAsync(id);
    }

    private async Task ToggleConnectionAsync(SystemId id)
    {
        RefreshConnectionSummary(force: true);
        string? error = null;
        try { await connectionCommands!.ToggleAsync(id, connectionCancellation.Token); }
        catch (OperationCanceledException) when (connectionCancellation.IsCancellationRequested) { }
        catch (Exception exception) { error = exception.Message; }
        finally
        {
            connectionBusy = false;
            if (Volatile.Read(ref accepting) != 0)
            {
                RefreshConnectionSummary(force: true);
                if (error is not null) sessionStatus.Text = error;
            }
        }
    }

    private void RevealFirstReceivingChannel()
    {
        if (!execution.Snapshot.CanReceive) return;
        foreach (var channel in session.Topology.Channels)
        {
            if (!session.Snapshot.Channels.TryGetValue(channel.Id, out var state) ||
                !state.ReceiveEnabled || !state.ReceiveActive) continue;
            if (EffectiveRenderer == ConsoleRendererPreference.Cards)
            {
                cardNavigation?.Reveal(channel.Id);
                if (cardControls.TryGetValue(channel.Id, out var card))
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => card.BringIntoView(), Avalonia.Threading.DispatcherPriority.Loaded);
            }
            else list.RevealChannel(channel.Id);
            return;
        }
    }

    private string ReceiveSummary()
    {
        // Topology order stays stable as calls overlap. Bound the ribbon, not the channel names.
        var names = new List<string>(2);
        int active = 0;
        foreach (var channel in session.Topology.Channels)
        {
            if (!session.Snapshot.Channels.TryGetValue(channel.Id, out var state) ||
                !state.ReceiveEnabled || !state.ReceiveActive) continue;
            active++;
            if (names.Count < 2) names.Add(channel.Name);
        }
        return active == 0 ? "Listening" : "RX " + string.Join(", ", names) +
            (active > names.Count ? $" +{active - names.Count}" : string.Empty);
    }

    private void RefreshConnectionSummary(bool force = false)
    {
        var states = connections?.ConnectionStates ?? [];
        if (states.IsDefault) states = [];
        int connected = states.Count(state => state.State == RadioConnectionState.Connected);
        int receiveEnabledCount = session.Topology.Channels.Count(channel =>
            session.Snapshot.Channels.TryGetValue(channel.Id, out var state) && state.ReceiveEnabled);
        bool listening = receiveEnabledCount > 0;
        connectionHealthy = connected > 0 && execution.Snapshot.CanReceive;
        string activity = execution.Snapshot.CanReceive ? listening ? ReceiveSummary() : "Standby" : "Paused";
        connectionSummary = connected == 0 ? "Disconnected" : $"{receiveEnabledCount} RX enabled · {activity}";
        if (!activity.StartsWith("RX ", StringComparison.Ordinal) && states.Any(state => state.State is RadioConnectionState.Starting or RadioConnectionState.WaitingForLogin or RadioConnectionState.Authenticating or RadioConnectionState.Configuring))
            connectionSummary = connected == 0 ? "Connecting…" : $"{receiveEnabledCount} RX enabled · Connecting…";
        if (connections is null) connectionSummary = "Offline preview";
        sessionStatus.Text = connectionSummary;
        ToolTip.SetTip(sessionStatus, session.Snapshot.StatusText);
        UpdateStatusColor();
        if (!force && displayedConnectionStates == states) return;
        displayedConnectionStates = states;
        var byId = states.ToDictionary(state => state.SystemId);
        foreach (var group in ((ConsoleListViewModel)list.DataContext!).VisibleRows.OfType<ConsoleListGroupViewModel>())
        {
            if (group.SystemId is not { } systemId) continue;
            var state = byId.GetValueOrDefault(systemId);
            string text = state?.State switch
            {
                RadioConnectionState.Connected => "Connected",
                RadioConnectionState.Faulted => "Failed",
                RadioConnectionState.Stopping => "Disconnecting…",
                RadioConnectionState.Disconnected => "Disconnected",
                null => "Offline",
                _ => "Connecting…"
            };
            group.SetConnectionText(text);
            bool enabled = connectionCommands is not null && !connectionBusy && state?.State != RadioConnectionState.Stopping;
            group.SetConnectionEnabled(enabled);
            if (connectionButtons.TryGetValue(systemId, out var pill))
            {
                pill.Content = $"{text.ToUpperInvariant()}\n{group.Name}";
                pill.IsEnabled = enabled;
                pill.BorderBrush = state?.State switch
                {
                    RadioConnectionState.Connected => ConnectedBorder,
                    RadioConnectionState.Faulted => FailedBorder,
                    RadioConnectionState.Disconnected or null => IdleBorder,
                    _ => ConnectingBorder
                };
                AutomationProperties.SetName(pill, group.ConnectionActionText);
            }
        }
    }

    private void UpdateStatusColor()
    {
        string resource = connectionHealthy && sessionStatus.Text == connectionSummary
            ? "MobileConnectedBrush" : "MobileDisconnectedBrush";
        if (this.TryFindResource(resource, ActualThemeVariant, out var value) && value is IBrush brush)
        {
            statusDot.Fill = brush;
            sessionStatus.Foreground = brush;
        }
    }

    private ValueTask ToggleSelectedPttAsync()
        => selectedGestureRequested || session.Commands is IConsoleSelectedTransmitCommands { IsSelectedPttRequested: true }
            ? EndSelectedPttAsync() : BeginSelectedPttAsync();

    private async ValueTask EndSelectedPttAsync()
    {
        selectedGestureRequested = false;
        try
        {
            if (session.Commands is IConsoleSelectedTransmitCommands selected)
                await selected.EndSelectedPttAsync(CancellationToken.None);
        }
        finally { UpdateSelectedPttButton(); }
    }

    private async ValueTask BeginSelectedPttAsync()
    {
        if (selectedGestureRequested || selectedStartPending || changingPttMode || Volatile.Read(ref accepting) == 0 ||
            session.Commands is not IConsoleSelectedTransmitCommands selected) return;
        var lease = execution.TryAcquire(ConsoleTransmitIntent.Manual);
        if (lease is null) return;
        long generation = manualInputGeneration;
        selectedGestureRequested = true;
        selectedStartPending = true;
        try
        {
            bool started = await selected.BeginSelectedPttAsync();
            if (!started) selectedGestureRequested = false;
            else if (!selectedGestureRequested || generation != manualInputGeneration ||
                Volatile.Read(ref accepting) == 0 || !execution.IsCurrent(lease.Value))
            {
                selectedGestureRequested = false;
                await selected.EndSelectedPttAsync(CancellationToken.None);
            }
        }
        catch { selectedGestureRequested = false; throw; }
        finally { selectedStartPending = false; UpdateSelectedPttButton(); }
    }

    private void UpdateSelectedPttButton()
    {
        int count = session.Snapshot.Channels.Values.Count(channel => channel.TransmitSelected);
        bool active = session.Commands is IConsoleSelectedTransmitCommands { IsSelectedPttRequested: true } &&
            session.Snapshot.Channels.Values.Any(channel => channel.Transmitting || channel.TransmitStarting || channel.TransmitStopping);
        selectedPttButton.IsVisible = session.Commands is IConsoleSelectedTransmitCommands && (count > 0 || active);
        selectedPttButton.Width = active ? 88 : 64;
        selectedPttButton.Content = active ? "Release TX" : $"TX ({count})";
        AutomationProperties.SetName(selectedPttButton, active ? "Release selected channel transmission" :
            $"{(TogglePttMode ? "Toggle transmit" : "Hold to transmit")} on {count} selected channels");
    }

    private void UpdateMuteButton()
    {
        if (session.Commands is not IConsoleListeningSettings listening) return;
        muteButton.Content = listening.OutputMuted ? mutedSpeakerIcon : speakerIcon;
        string label = execution.Snapshot.State == ConsoleExecutionState.RequiresResume
            ? "Resume listening"
            : listening.OutputMuted ? "Restore live RX output" : "Mute live RX output";
        AutomationProperties.SetName(muteButton, label);
        ToolTip.SetTip(muteButton, label + "; TAR recording continues");
        muteButton.IsEnabled = Volatile.Read(ref accepting) != 0;
    }

    private static ConsoleApplicationSession CreatePreviewSession()
    {
        ChannelDescriptor[] channels = CreateChannels();
        var topology = new ConsoleTopologySnapshot(null,
            [new SystemDescriptor(channels[0].SystemId, "Mobile preview", "Mixed")],
            [new ZoneDescriptor(channels[0].ZoneId, "Example channels", channels.Select(c => c.Id).ToArray())], channels);
        return new(topology, ConsoleRuntimeSnapshot.Empty, new NoOpConsoleCommands());
    }

    private static ChannelDescriptor[] CreateChannels()
    {
        var system = SystemId.FromName("Mobile preview");
        var zone = ZoneId.FromName("Example channels");
        return Enumerable.Range(1, 12).Select(index =>
        {
            ChannelProtocol protocol = index % 2 == 0 ? ChannelProtocol.Dmr : ChannelProtocol.P25;
            uint destination = (uint)(1000 + index);
            var id = new ChannelId(new ChannelSessionId("Mobile preview", protocol, destination, 0, $"preview-{index}"));
            return new ChannelDescriptor(id, system, zone, $"Example {index}", destination,
                protocol.ToString(), 0, ReceiveOnly: true);
        }).ToArray();
    }
}
