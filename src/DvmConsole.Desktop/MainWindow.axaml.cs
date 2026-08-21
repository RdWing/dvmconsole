using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DvmConsole.Audio;
using DvmConsole.Core.Settings;
using DvmConsole.FneClient;
using System.Collections.Specialized;
using System.Reflection;

namespace DvmConsole.Desktop;

public sealed partial class MainWindow : Window
{
    private MainWindowViewModel viewModel;
    private readonly PressAndHoldPttController cardPtt;
    private OperatorToolsWindow? operatorToolsWindow;
    private DebugLogWindow? debugLogWindow;
    private DocumentationWindow? documentationWindow;
    private AboutWindow? aboutWindow;
    private readonly List<DispatcherTimer> scrollBarTimers = [];
    private readonly HashSet<ScrollViewer> configuredScrollViewers = [];
    private readonly INotifyCollectionChanged activityHistoryCollection;
    private readonly ScrollViewportAnchor<CallHistoryEntry> activityViewportAnchor;
    private Control? draggedChannelCard;
    private ChannelViewModel? draggedChannel;
    private Point dragPointerOrigin;
    private double dragWidgetXOrigin;
    private double dragWidgetYOrigin;
    private bool draggedChannelMoved;
    private bool toggleReceiveAfterChannelClick;

    public MainWindow() : this(null)
    {
    }

    public MainWindow(string? configurationPath)
    {
        InitializeComponent();
        // Avalonia can leave named controls declared inside nested MenuItems
        // unresolved when the compiled XAML is loaded from a published
        // self-contained apphost. Resolve them from the window name scope
        // before the startup menu refreshes run.
        recentCodeplugsMenu ??= this.FindControl<MenuItem>("recentCodeplugsMenu");
        namedSettingsProfileLoadMenu ??= this.FindControl<MenuItem>("namedSettingsProfileLoadMenu");
        namedSettingsProfileDeleteMenu ??= this.FindControl<MenuItem>("namedSettingsProfileDeleteMenu");
        activityCallHistoryList ??= this.FindControl<ItemsControl>("activityCallHistoryList")
            ?? throw new InvalidOperationException("The Activity history list was not initialized.");
        viewModel = MainWindowViewModel.Load(configurationPath);
        cardPtt = new PressAndHoldPttController(
            channel => viewModel.StartChannelTransmitAsync(channel),
            channel => viewModel.StopChannelTransmitAsync(channel));
        DataContext = viewModel;
        activityHistoryCollection = (INotifyCollectionChanged)viewModel.ActivityCallHistory;
        activityViewportAnchor = new ScrollViewportAnchor<CallHistoryEntry>(
            () => activityScrollViewer,
            () => activityCallHistoryList.GetVisualDescendants()
                .OfType<Border>()
                .Where(border => border.Classes.Contains("activity-call-card")),
            control => control.DataContext as CallHistoryEntry);
        activityHistoryCollection.CollectionChanged += HandleActivityHistoryCollectionChanged;
        activityCallHistoryList.LayoutUpdated += HandleActivityHistoryLayoutUpdated;
        AddHandler(InputElement.KeyDownEvent, HandleKeyDown, RoutingStrategies.Tunnel);
        AddHandler(InputElement.KeyUpEvent, HandleKeyUp, RoutingStrategies.Tunnel);
        AddHandler(InputElement.PointerPressedEvent, HandlePttPointerPressed, RoutingStrategies.Tunnel, true);
        AddHandler(InputElement.PointerReleasedEvent, HandlePttPointerReleased, RoutingStrategies.Tunnel, true);
        AddHandler(InputElement.PointerCaptureLostEvent, HandlePttPointerCaptureLost, RoutingStrategies.Bubble, true);
        RefreshRecentCodeplugMenu();
        RefreshNamedSettingsProfileMenus();
        Opened += async (_, _) =>
        {
            ConfigureTransientChannelScrollBars();
            ConfigureTransientScrollBars(activityScrollViewer);
            await viewModel.StartKeyboardPttAsync().ConfigureAwait(false);
        };
        LayoutUpdated += (_, _) => ConfigureTransientChannelScrollBars();
        Closed += async (_, _) =>
        {
            try
            {
                operatorToolsWindow?.Close();
                debugLogWindow?.Close();
                documentationWindow?.Close();
                aboutWindow?.Close();
                foreach (DispatcherTimer timer in scrollBarTimers)
                    timer.Stop();
                activityCallHistoryList.LayoutUpdated -= HandleActivityHistoryLayoutUpdated;
                activityHistoryCollection.CollectionChanged -= HandleActivityHistoryCollectionChanged;
                activityViewportAnchor.Reset();
                await viewModel.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                DesktopCrashLog.Write("Main window shutdown", exception);
            }
        };
    }

    private void HandleActivityHistoryCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
            activityViewportAnchor.Reset();
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex == 0)
            activityViewportAnchor.Capture();
    }

    private void HandleActivityHistoryLayoutUpdated(object? sender, EventArgs e)
        => activityViewportAnchor.Restore();

    private async void HandleChannelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control ||
            ChannelCardInput.IsInteractiveSource(e.Source, control))
            return;

        if (control.DataContext is ChannelViewModel channel &&
            DataContext is MainWindowViewModel viewModel)
        {
            PointerPointProperties properties = e.GetCurrentPoint(control).Properties;
            if ((properties.IsLeftButtonPressed || properties.IsRightButtonPressed) && !viewModel.LockWidgets)
            {
                draggedChannelCard = control;
                draggedChannel = channel;
                dragPointerOrigin = e.GetPosition(this);
                dragWidgetXOrigin = channel.WidgetX;
                dragWidgetYOrigin = channel.WidgetY;
                draggedChannelMoved = false;
                toggleReceiveAfterChannelClick = properties.IsLeftButtonPressed;
                e.Pointer.Capture(control);
                e.Handled = true;
                control.Focus();
                return;
            }

            if (!properties.IsLeftButtonPressed)
                return;

            await viewModel.ToggleChannelReceiveAsync(channel);
            control.Focus();
        }
    }

    private void HandleChannelPointerMoved(object? sender, PointerEventArgs e)
    {
        if (draggedChannelCard is null || draggedChannel is null ||
            !ReferenceEquals(sender, draggedChannelCard) ||
            DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        Point current = e.GetPosition(this);
        double deltaX = current.X - dragPointerOrigin.X;
        double deltaY = current.Y - dragPointerOrigin.Y;
        if (!draggedChannelMoved && Math.Abs(deltaX) < 4 && Math.Abs(deltaY) < 4)
            return;

        draggedChannelMoved = true;
        const double gridSize = 10;
        double x = Math.Max(0, Math.Round((dragWidgetXOrigin + deltaX) / gridSize) * gridSize);
        double y = Math.Max(0, Math.Round((dragWidgetYOrigin + deltaY) / gridSize) * gridSize);
        viewModel.MoveChannelWidget(draggedChannel, x, y, persist: false);
        e.Handled = true;
    }

    private async void HandleChannelPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (draggedChannelCard is null || draggedChannel is null || !ReferenceEquals(sender, draggedChannelCard))
            return;

        ChannelViewModel channel = draggedChannel;
        bool moved = draggedChannelMoved;
        bool toggleReceive = toggleReceiveAfterChannelClick;
        if (DataContext is MainWindowViewModel viewModel)
        {
            if (moved)
                viewModel.MoveChannelWidget(channel, channel.WidgetX, channel.WidgetY, persist: true);
            else if (toggleReceive)
                await viewModel.ToggleChannelReceiveAsync(channel);
        }
        e.Pointer.Capture(null);
        ClearChannelDrag();
        e.Handled = true;
    }

    private void HandleChannelPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (draggedChannelCard is not null && ReferenceEquals(sender, draggedChannelCard))
            ClearChannelDrag();
    }

    private void ClearChannelDrag()
    {
        draggedChannelCard = null;
        draggedChannel = null;
        draggedChannelMoved = false;
        toggleReceiveAfterChannelClick = false;
    }

    private async void HandlePttPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Button? button = FindPttButton(e.Source);
        if (button?.DataContext is not ChannelViewModel channel ||
            !e.GetCurrentPoint(button).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        e.Pointer.Capture(button);
        await cardPtt.PressAsync(channel);
    }

    private async void HandlePttPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        Button? button = e.Pointer.Captured as Button ?? FindPttButton(e.Source);
        if (button?.DataContext is not ChannelViewModel channel || !button.Classes.Contains("ptt"))
            return;

        e.Handled = true;
        e.Pointer.Capture(null);
        await cardPtt.ReleaseAsync(channel);
    }

    private async void HandlePttPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        Button? button = FindPttButton(e.Source);
        if (button?.DataContext is ChannelViewModel channel)
            await cardPtt.ReleaseAsync(channel);
    }

    private static Button? FindPttButton(object? source)
    {
        for (Visual? visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Button button && button.Classes.Contains("ptt"))
                return button;
        }
        return null;
    }

    private async void HandleOpenCodeplugClick(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanOpen)
        {
            await ShowCodeplugErrorAsync("This platform did not provide an available file picker.");
            return;
        }

        IReadOnlyList<IStorageFile> files;
        try
        {
            files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open Codeplug",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Codeplug YAML")
                    {
                        Patterns = ["*.yml", "*.yaml"],
                        MimeTypes = ["application/yaml", "text/yaml", "text/x-yaml"],
                        AppleUniformTypeIdentifiers = ["public.yaml", "public.text"]
                    }
                ]
            });
        }
        catch (Exception exception)
        {
            await ShowCodeplugErrorAsync($"The codeplug picker could not be opened.\n\n{exception.Message}");
            return;
        }

        if (files.Count == 0)
            return;

        string? path = files[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            await ShowCodeplugErrorAsync("The selected codeplug does not have a local filesystem path.");
            return;
        }

        await OpenCodeplugAsync(path);
    }

    private async void HandleOpenRecentCodeplugClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string path })
            await OpenCodeplugAsync(path);
    }

    private async Task OpenCodeplugAsync(string path)
    {
        var replacement = MainWindowViewModel.Load(path);
        if (!replacement.IsCodeplugLoaded)
        {
            string error = replacement.StatusText;
            await replacement.DisposeAsync();
            await ShowCodeplugErrorAsync(error);
            return;
        }

        await ReplaceViewModelAsync(replacement);
    }

    private void RefreshRecentCodeplugMenu()
        => MainWindowMenuBuilder.ReplaceRecentCodeplugItems(
            recentCodeplugsMenu,
            viewModel.RecentCodeplugPaths,
            "No recent codeplugs",
            HandleOpenRecentCodeplugClick);

    private void RefreshNamedSettingsProfileMenus()
    {
        RefreshNamedSettingsProfileMenu(
            namedSettingsProfileLoadMenu,
            "No saved profiles",
            HandleLoadNamedSettingsProfileClick);
        RefreshNamedSettingsProfileMenu(
            namedSettingsProfileDeleteMenu,
            "No saved profiles",
            HandleDeleteNamedSettingsProfileClick);
    }

    private void RefreshNamedSettingsProfileMenu(
        MenuItem menu,
        string emptyHeader,
        EventHandler<RoutedEventArgs> clickHandler)
        => MainWindowMenuBuilder.ReplaceItems(menu, viewModel.NamedSettingsProfiles, emptyHeader, clickHandler);

    private async void HandleSelectBackgroundClick(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanOpen)
            return;

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select user background",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Images")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp"],
                    MimeTypes = ["image/png", "image/jpeg", "image/bmp", "image/webp"]
                }
            ]
        });
        if (files.Count == 0)
            return;

        string? path = files[0].TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
            viewModel.SetUserBackground(path);
    }

    private void HandleClearBackgroundClick(object? sender, RoutedEventArgs e)
        => viewModel.ClearUserBackground();

    private void HandleResetLayoutClick(object? sender, RoutedEventArgs e)
        => viewModel.ResetLayout();

    private async void HandleImportSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanOpen)
        {
            await ShowInformationAsync("Import settings", "This platform did not provide an available file picker.");
            return;
        }

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import DVM Console Settings",
            AllowMultiple = false,
            FileTypeFilter = [SettingsFileType]
        });
        if (files.Count == 0)
            return;

        string? path = files[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            await ShowInformationAsync("Import settings", "The selected settings file does not have a local filesystem path.");
            return;
        }

        try
        {
            string? activeCodeplugPath = viewModel.CurrentCodeplugPath;
            viewModel.ImportSettings(path);
            await ReplaceViewModelAsync(MainWindowViewModel.Load(activeCodeplugPath));
            await ShowInformationAsync("Settings imported", "The imported profile has been applied to the current console.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            await ShowInformationAsync("Unable to import settings", exception.Message);
        }
    }

    private async void HandleSaveSettingsProfileClick(object? sender, RoutedEventArgs e)
    {
        string? profileName = await PromptForTextAsync(
            "Save settings profile",
            "Enter a name for the current operator settings profile.",
            "Save");
        if (profileName is null)
            return;

        try
        {
            viewModel.SaveNamedSettingsProfile(profileName);
            RefreshNamedSettingsProfileMenus();
            await ShowInformationAsync("Settings profile saved", $"Saved profile '{profileName}'.");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            await ShowInformationAsync("Unable to save settings profile", exception.Message);
        }
    }

    private async void HandleLoadNamedSettingsProfileClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string profileName })
            return;

        SettingsImportPreview preview;
        try
        {
            preview = viewModel.PreviewNamedSettingsProfile(profileName);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            await ShowInformationAsync("Unable to preview settings profile", exception.Message);
            RefreshNamedSettingsProfileMenus();
            return;
        }

        if (!await ConfirmAsync(
                "Load settings profile",
                $"{preview.SummaryText}\n\nApply operator settings from '{profileName}'? The active codeplug and current channel selection will remain unchanged.",
                "Apply"))
        {
            return;
        }

        string? activeCodeplugPath = viewModel.CurrentCodeplugPath;
        try
        {
            viewModel.ImportNamedSettingsProfile(profileName, SettingsImportScope.OperatorState);
            await ReplaceViewModelAsync(MainWindowViewModel.Load(activeCodeplugPath));
            await ShowInformationAsync("Settings profile loaded", $"Applied operator settings from '{profileName}'.");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            await ShowInformationAsync("Unable to load settings profile", exception.Message);
            RefreshNamedSettingsProfileMenus();
        }
    }

    private async void HandleDeleteNamedSettingsProfileClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string profileName } ||
            !await ConfirmAsync(
                "Delete settings profile",
                $"Delete the saved operator settings profile '{profileName}'?",
                "Delete"))
        {
            return;
        }

        try
        {
            viewModel.DeleteNamedSettingsProfile(profileName);
            RefreshNamedSettingsProfileMenus();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            await ShowInformationAsync("Unable to delete settings profile", exception.Message);
        }
    }

    private async void HandleExportSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanSave)
        {
            await ShowInformationAsync("Export settings", "This platform did not provide an available save picker.");
            return;
        }

        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export DVM Console Settings",
            SuggestedFileName = "dvmconsole-settings.json",
            DefaultExtension = "json",
            FileTypeChoices = [SettingsFileType]
        });
        string? path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            viewModel.ExportSettings(path);
            await ShowInformationAsync("Settings exported", $"Settings were exported to:\n{path}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await ShowInformationAsync("Unable to export settings", exception.Message);
        }
    }

    private async void HandleResetSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync(
                "Reset settings",
                "Reset all operator settings, presets, routes, selections, and layout preferences? The active codeplug itself will not be changed."))
        {
            return;
        }

        string? activeCodeplugPath = viewModel.CurrentCodeplugPath;
        viewModel.ResetSettings();
        await ReplaceViewModelAsync(MainWindowViewModel.Load(activeCodeplugPath));
    }

    private async Task ReplaceViewModelAsync(MainWindowViewModel replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        MainWindowViewModel previous = viewModel;

        // These modeless windows hold direct references to the view model. Close
        // them before disposing the old model rather than leaving a visible
        // window bound to stale settings, history, or PTT state.
        CloseModelessViewModelWindows();
        viewModel = replacement;
        DataContext = replacement;
        RefreshRecentCodeplugMenu();
        RefreshNamedSettingsProfileMenus();
        await previous.DisposeAsync();
        await replacement.StartKeyboardPttAsync();
    }

    private void CloseModelessViewModelWindows()
    {
        OperatorToolsWindow? tools = operatorToolsWindow;
        operatorToolsWindow = null;
        tools?.Close();

        DebugLogWindow? logs = debugLogWindow;
        debugLogWindow = null;
        logs?.Close();
    }

    private async Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Reset")
    {
        bool confirmed = false;
        OperatorDialogParts parts = OperatorDialogFactory.CreateConfirmation(title, message, confirmLabel);
        parts.CancelButton!.Click += (_, _) => parts.Window.Close();
        parts.PrimaryButton.Click += (_, _) => { confirmed = true; parts.Window.Close(); };
        await parts.Window.ShowDialog(this);
        return confirmed;
    }

    private async Task<string?> PromptForTextAsync(string title, string message, string confirmLabel)
    {
        bool confirmed = false;
        OperatorDialogParts parts = OperatorDialogFactory.CreateTextPrompt(title, message, confirmLabel, "Profile name");
        TextBox input = parts.Input!;
        parts.CancelButton!.Click += (_, _) => parts.Window.Close();
        parts.PrimaryButton.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(input.Text))
            {
                confirmed = true;
                parts.Window.Close();
            }
        };
        parts.Window.Opened += (_, _) => input.Focus();
        await parts.Window.ShowDialog(this);
        return confirmed ? input.Text?.Trim() : null;
    }

    private static FilePickerFileType SettingsFileType { get; } = new("DVM Console Settings")
    {
        Patterns = ["*.json"],
        MimeTypes = ["application/json", "text/json"],
        AppleUniformTypeIdentifiers = ["public.json"]
    };

    private async Task ShowCodeplugErrorAsync(string message)
    {
        OperatorDialogParts parts = OperatorDialogFactory.CreateMessage("Unable to open codeplug", message, "OK");
        parts.PrimaryButton.Click += (_, _) => parts.Window.Close();
        await parts.Window.ShowDialog(this);
    }

    private async void HandleDisableAllReceiveClick(object? sender, RoutedEventArgs e)
        => await viewModel.DisableAllReceiveAsync();

    private async void HandleEnableAllReceiveClick(object? sender, RoutedEventArgs e)
        => await viewModel.EnableAllReceiveAsync();

    private async void HandleEnableZoneReceiveClick(object? sender, RoutedEventArgs e)
        => await viewModel.EnableSelectedZoneReceiveAsync();

    private async void HandleDisableZoneReceiveClick(object? sender, RoutedEventArgs e)
        => await viewModel.DisableSelectedZoneReceiveAsync();

    private async void HandleSubscriberCommandClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string value } ||
            !Enum.TryParse(value, ignoreCase: true, out P25SubscriberCommand command))
        {
            return;
        }

        var window = new SubscriberCommandWindow(viewModel, command);
        await window.ShowDialog(this);
    }

    private void HandleOpenDebugLogsClick(object? sender, RoutedEventArgs e)
    {
        if (debugLogWindow is null)
        {
            debugLogWindow = new DebugLogWindow(viewModel);
            debugLogWindow.Closed += (_, _) => debugLogWindow = null;
        }

        if (!debugLogWindow.IsVisible)
            debugLogWindow.Show();
        debugLogWindow.Activate();
    }

    private void HandleActivityDoubleTapped(object? sender, TappedEventArgs e)
    {
        OpenOperatorTools(OperatorToolSection.History);
        e.Handled = true;
    }

    private void HandleToggleActivitySidebarClick(object? sender, RoutedEventArgs e)
    {
        viewModel.ShowCallHistoryPane = !viewModel.ShowCallHistoryPane;
        e.Handled = true;
    }

    private void HandleToggleActivityCurrentZoneFilterClick(object? sender, RoutedEventArgs e)
    {
        viewModel.ToggleActivityCurrentZoneFilter();
        e.Handled = true;
    }

    private void HandleOpenOperatorToolsClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string value } ||
            !Enum.TryParse(value, ignoreCase: true, out OperatorToolSection section))
        {
            return;
        }

        OpenOperatorTools(section);
    }

    private void OpenOperatorTools(OperatorToolSection section)
    {
        if (operatorToolsWindow is null)
        {
            operatorToolsWindow = new OperatorToolsWindow(viewModel, section);
            operatorToolsWindow.Closed += (_, _) => operatorToolsWindow = null;
            operatorToolsWindow.Show();
            return;
        }

        operatorToolsWindow.SelectSection(section);
        operatorToolsWindow.Activate();
    }

    private void HandleDocumentationClick(object? sender, RoutedEventArgs e)
    {
        if (documentationWindow is null)
        {
            documentationWindow = new DocumentationWindow();
            documentationWindow.Closed += (_, _) => documentationWindow = null;
        }

        if (!documentationWindow.IsVisible)
            documentationWindow.Show(this);
        documentationWindow.Activate();
    }

    private void HandleAboutClick(object? sender, RoutedEventArgs e)
    {
        if (aboutWindow is null)
        {
            aboutWindow = new AboutWindow();
            aboutWindow.Closed += (_, _) => aboutWindow = null;
        }

        if (!aboutWindow.IsVisible)
            aboutWindow.Show(this);
        aboutWindow.Activate();
    }

    internal static string ApplicationVersion =>
        typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unversioned development build";

    internal static string ShortApplicationVersion => FormatShortVersion(ApplicationVersion);

    internal static string FormatShortVersion(string informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
            return "unversioned development build";

        string value = informationalVersion.Trim();
        int plusIndex = value.IndexOf('+');
        if (plusIndex < 0)
            return value;

        string version = value[..plusIndex];
        string revision = value[(plusIndex + 1)..].Split('.')[0];
        if (revision.Length == 0)
            return version;
        return $"{version} ({revision[..Math.Min(7, revision.Length)]})";
    }

    private async Task ShowInformationAsync(string title, string message)
    {
        OperatorDialogParts parts = OperatorDialogFactory.CreateMessage(title, message, "OK");
        parts.PrimaryButton.Click += (_, _) => parts.Window.Close();
        await parts.Window.ShowDialog(this);
    }

    private void HandleTransmitSelectionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ChannelViewModel channel })
            viewModel.ToggleChannelTransmitSelection(channel);
    }

    private void HandlePageSelectionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ChannelViewModel channel })
            viewModel.ToggleChannelPageSelection(channel);
    }

    private void HandleAlertSelectionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ChannelViewModel channel })
            viewModel.ToggleChannelAlertSelection(channel);
    }

    private async void HandleSystemStatusClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SystemViewModel system })
            await viewModel.ToggleSystemConnectionAsync(system);
    }

    private void HandleDismissCodeplugDiagnosticsClick(object? sender, RoutedEventArgs e)
    {
        viewModel.DismissCodeplugDiagnostics();
    }

    private void HandleToggleAllTransmitSelectionClick(object? sender, RoutedEventArgs e)
        => viewModel.ToggleAllTransmitSelection();

    private async void HandleToolbarAlertToneClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: BuiltInAlertToneViewModel tone })
            await viewModel.SendBuiltInAlertToneAsync(tone);
    }

    private void HandleToolbarToneToolsClick(object? sender, RoutedEventArgs e)
        => OpenOperatorTools(OperatorToolSection.Tones);

    private async void HandlePlayCallHistoryRecordingClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CallHistoryEntry entry })
            await viewModel.PlayCallHistoryRecordingAsync(entry);
    }

    private async void HandleGlobalPttKeyClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string value } ||
            !Enum.TryParse(value, ignoreCase: true, out KeyboardPttKey key))
            return;
        await viewModel.SetGlobalPttKeyAsync(key);
    }

    private void HandleExitClick(object? sender, RoutedEventArgs e) => Close();

    private void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel &&
            TryMapPttKey(e.Key, out KeyboardPttKey key))
        {
            bool handled = viewModel.HandleKeyboardPttDown(key);
            e.Handled = handled || viewModel.IsConfiguredPttKey(key);
        }
    }

    private void HandleKeyUp(object? sender, KeyEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel &&
            TryMapPttKey(e.Key, out KeyboardPttKey key))
        {
            bool handled = viewModel.HandleKeyboardPttUp(key);
            e.Handled = handled || viewModel.IsConfiguredPttKey(key);
        }
    }

    internal static bool TryMapPttKey(Key key, out KeyboardPttKey pttKey)
    {
        pttKey = key switch
        {
            Key.Space => KeyboardPttKey.Space,
            Key.F1 => KeyboardPttKey.F1,
            Key.F2 => KeyboardPttKey.F2,
            Key.F3 => KeyboardPttKey.F3,
            Key.F4 => KeyboardPttKey.F4,
            Key.F5 => KeyboardPttKey.F5,
            Key.F6 => KeyboardPttKey.F6,
            Key.F7 => KeyboardPttKey.F7,
            Key.F8 => KeyboardPttKey.F8,
            Key.F9 => KeyboardPttKey.F9,
            Key.F10 => KeyboardPttKey.F10,
            Key.F11 => KeyboardPttKey.F11,
            Key.F12 => KeyboardPttKey.F12,
            Key.F13 => KeyboardPttKey.F13,
            Key.F14 => KeyboardPttKey.F14,
            Key.F15 => KeyboardPttKey.F15,
            Key.F16 => KeyboardPttKey.F16,
            Key.F17 => KeyboardPttKey.F17,
            Key.F18 => KeyboardPttKey.F18,
            Key.F19 => KeyboardPttKey.F19,
            _ => default
        };
        return key is Key.Space or (>= Key.F1 and <= Key.F19);
    }

    private void ConfigureTransientScrollBars(ScrollViewer? viewer)
    {
        if (viewer is null || !configuredScrollViewers.Add(viewer))
            return;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            SetScrollBarOpacity(viewer, 0);
        };
        viewer.ScrollChanged += (_, _) =>
        {
            SetScrollBarOpacity(viewer, 1);
            timer.Stop();
            timer.Start();
        };
        scrollBarTimers.Add(timer);
        Dispatcher.UIThread.Post(
            () => SetScrollBarOpacity(viewer, 0),
            DispatcherPriority.Loaded);
    }

    private void ConfigureTransientChannelScrollBars()
    {
        foreach (ScrollViewer viewer in this.GetVisualDescendants()
                     .OfType<ScrollViewer>()
                     .Where(viewer => viewer.Name == "channelScrollViewer"))
        {
            ConfigureTransientScrollBars(viewer);
        }
    }

    private static void SetScrollBarOpacity(ScrollViewer viewer, double opacity)
    {
        foreach (ScrollBar scrollBar in viewer.GetVisualDescendants().OfType<ScrollBar>())
            scrollBar.Opacity = opacity;
    }

    private void InitializeComponent()
    {
        Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
    }

    private void HandleOpenRecordingClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CallRecordingMetadata metadata } &&
            DataContext is MainWindowViewModel viewModel)
        {
            viewModel.OpenRecording(metadata);
        }
    }

    private async void HandleDeleteRecordingClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CallRecordingMetadata metadata } &&
            DataContext is MainWindowViewModel viewModel &&
            await ConfirmAsync(
                "Delete recording",
                $"Delete '{metadata.FileName}' and its catalog metadata? This cannot be undone.",
                "Delete"))
        {
            await viewModel.DeleteRecordingAsync(metadata);
        }
    }

    private async void HandlePlayRecordingClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CallRecordingMetadata metadata } &&
            DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.PlayRecordingAsync(metadata);
        }
    }

    private async void HandleStopRecordingClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
            await viewModel.StopRecordingPlaybackAsync();
    }

    private void HandleSaveIgnoredSubscribersClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ChannelViewModel channel } &&
            DataContext is MainWindowViewModel viewModel)
        {
            viewModel.TrySaveRecordingIgnoredSubscribers(channel);
        }
    }

    private void HandleSaveOutputDeviceClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ChannelViewModel channel } &&
            DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SaveChannelOutputDevice(channel);
        }
    }

    private void HandleSaveWebStreamOutputDeviceClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: WebStreamViewModel stream } &&
            DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SaveWebStreamOutputDevice(stream);
        }
    }

    private void HandleSaveAudioInputPresetClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
            viewModel.SaveAudioInputPreset();
    }

    private void HandleUseAudioInputPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AudioInputPresetViewModel preset } &&
            DataContext is MainWindowViewModel viewModel)
        {
            viewModel.UseAudioInputPreset(preset);
        }
    }

    private void HandleDeleteAudioInputPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AudioInputPresetViewModel preset } &&
            DataContext is MainWindowViewModel viewModel)
        {
            viewModel.DeleteAudioInputPreset(preset);
        }
    }

    private void HandleApplyPatchGroupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PatchGroupEditorViewModel group } &&
            DataContext is MainWindowViewModel viewModel)
        {
            viewModel.ApplyPatchGroup(group);
        }
    }

    private void HandleUseDtmfPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DtmfPresetViewModel preset } &&
            DataContext is MainWindowViewModel viewModel)
        {
            viewModel.UseDtmfPreset(preset);
        }
    }

    private void HandleDeleteDtmfPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DtmfPresetViewModel preset } &&
            DataContext is MainWindowViewModel viewModel)
        {
            viewModel.DeleteDtmfPreset(preset);
        }
    }

    private async void HandleSendDtmfPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DtmfPresetViewModel preset } &&
            DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.SendDtmfPresetAsync(preset);
        }
    }

    private void HandleUseTonePresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TonePresetViewModel preset } &&
            DataContext is MainWindowViewModel viewModel)
        {
            viewModel.UseTonePreset(preset);
        }
    }

    private void HandleDeleteTonePresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TonePresetViewModel preset } &&
            DataContext is MainWindowViewModel viewModel)
        {
            viewModel.DeleteTonePreset(preset);
        }
    }

    private async void HandleSendTonePresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TonePresetViewModel preset } &&
            DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.SendTonePresetAsync(preset);
        }
    }
}
