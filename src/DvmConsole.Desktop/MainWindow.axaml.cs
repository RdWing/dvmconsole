using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DvmConsole.Audio;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Diagnostics;
using DvmConsole.Core.Runtime;
using DvmConsole.Core.Settings;
using DvmConsole.FneClient;
using DvmConsole.Media;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using fnecore.P25;

namespace DvmConsole.Desktop;

public sealed partial class MainWindow : Window
{
    private MainWindowViewModel viewModel;
    private readonly PressAndHoldPttController cardPtt;
    private CallHistoryWindow? callHistoryWindow;
    private OperatorToolsWindow? operatorToolsWindow;
    private DocumentationWindow? documentationWindow;
    private AboutWindow? aboutWindow;
    private readonly List<DispatcherTimer> scrollBarTimers = [];
    private readonly HashSet<ScrollViewer> configuredScrollViewers = [];
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
        viewModel = MainWindowViewModel.Load(configurationPath);
        cardPtt = new PressAndHoldPttController(
            channel => viewModel.StartChannelTransmitAsync(channel),
            channel => viewModel.StopChannelTransmitAsync(channel));
        DataContext = viewModel;
        viewModel.PropertyChanged += HandleViewModelPropertyChanged;
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
        PositionChanged += (_, _) => SnapCallHistoryWindowIfNeeded();
        SizeChanged += (_, _) => SnapCallHistoryWindowIfNeeded();
        Closed += async (_, _) =>
        {
            try
            {
                viewModel.PropertyChanged -= HandleViewModelPropertyChanged;
                callHistoryWindow?.Close();
                operatorToolsWindow?.Close();
                documentationWindow?.Close();
                aboutWindow?.Close();
                foreach (DispatcherTimer timer in scrollBarTimers)
                    timer.Stop();
                await viewModel.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                DesktopCrashLog.Write("Main window shutdown", exception);
            }
        };
    }

    private async void HandleChannelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button or Slider)
            return;

        if (sender is Control { DataContext: ChannelViewModel channel } control &&
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
    {
        recentCodeplugsMenu.Items.Clear();
        if (viewModel.RecentCodeplugPaths.Count == 0)
        {
            recentCodeplugsMenu.Items.Add(new MenuItem
            {
                Header = "No recent codeplugs",
                IsEnabled = false
            });
            recentCodeplugsMenu.IsEnabled = false;
            return;
        }

        foreach (string path in viewModel.RecentCodeplugPaths)
        {
            var item = new MenuItem
            {
                Header = path,
                Tag = path
            };
            item.Click += HandleOpenRecentCodeplugClick;
            recentCodeplugsMenu.Items.Add(item);
        }

        recentCodeplugsMenu.IsEnabled = true;
    }

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
    {
        menu.Items.Clear();
        if (viewModel.NamedSettingsProfiles.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = emptyHeader, IsEnabled = false });
            menu.IsEnabled = false;
            return;
        }

        foreach (string profileName in viewModel.NamedSettingsProfiles)
        {
            var item = new MenuItem
            {
                Header = profileName,
                Tag = profileName
            };
            item.Click += clickHandler;
            menu.Items.Add(item);
        }

        menu.IsEnabled = true;
    }

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
        MainWindowViewModel previous = viewModel;
        viewModel = replacement;
        DataContext = replacement;
        RefreshRecentCodeplugMenu();
        RefreshNamedSettingsProfileMenus();
        await previous.DisposeAsync();
        await replacement.StartKeyboardPttAsync();
    }

    private async Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Reset")
    {
        bool confirmed = false;
        var cancelButton = new Button { Content = "Cancel", MinWidth = 88 };
        var confirmButton = new Button { Content = confirmLabel, MinWidth = 88 };
        var dialog = new Window
        {
            Title = title,
            Width = 500,
            MinHeight = 220,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 18,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancelButton, confirmButton }
                    }
                }
            }
        };
        cancelButton.Click += (_, _) => dialog.Close();
        confirmButton.Click += (_, _) => { confirmed = true; dialog.Close(); };
        await dialog.ShowDialog(this);
        return confirmed;
    }

    private async Task<string?> PromptForTextAsync(string title, string message, string confirmLabel)
    {
        bool confirmed = false;
        var input = new TextBox { Watermark = "Profile name", MinWidth = 320 };
        var cancelButton = new Button { Content = "Cancel", MinWidth = 88 };
        var confirmButton = new Button { Content = confirmLabel, MinWidth = 88 };
        var dialog = new Window
        {
            Title = title,
            Width = 500,
            MinHeight = 240,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    input,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancelButton, confirmButton }
                    }
                }
            }
        };
        cancelButton.Click += (_, _) => dialog.Close();
        confirmButton.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(input.Text))
            {
                confirmed = true;
                dialog.Close();
            }
        };
        dialog.Opened += (_, _) => input.Focus();
        await dialog.ShowDialog(this);
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
        var closeButton = new Button
        {
            Content = "OK",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            MinWidth = 80
        };
        var dialog = new Window
        {
            Title = "Unable to open codeplug",
            Width = 520,
            MinHeight = 220,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 18,
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap
                    },
                    closeButton
                }
            }
        };
        closeButton.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private async void HandleDisableAllReceiveClick(object? sender, RoutedEventArgs e)
        => await viewModel.DisableAllReceiveAsync();

    private async void HandleTestTalkPermitToneClick(object? sender, RoutedEventArgs e)
        => await viewModel.TestTalkPermitToneAsync();

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

    private async void HandleOpenDebugLogsClick(object? sender, RoutedEventArgs e)
    {
        var window = new DebugLogWindow(viewModel);
        await window.ShowDialog(this);
    }

    private void HandleOpenCallHistoryClick(object? sender, RoutedEventArgs e)
    {
        if (callHistoryWindow is null)
        {
            callHistoryWindow = new CallHistoryWindow(viewModel);
            callHistoryWindow.Closed += (_, _) => callHistoryWindow = null;
        }

        if (!callHistoryWindow.IsVisible)
            callHistoryWindow.Show(this);
        SnapCallHistoryWindowIfNeeded();
        callHistoryWindow.Activate();
    }

    private void HandleActivityDoubleTapped(object? sender, TappedEventArgs e)
    {
        HandleOpenCallHistoryClick(sender, e);
        e.Handled = true;
    }

    private void HandleToggleActivitySidebarClick(object? sender, RoutedEventArgs e)
    {
        viewModel.ShowCallHistoryPane = !viewModel.ShowCallHistoryPane;
        e.Handled = true;
    }

    private void HandleViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SnapCallHistoryToWindow))
            SnapCallHistoryWindowIfNeeded();
    }

    private void SnapCallHistoryWindowIfNeeded()
    {
        if (callHistoryWindow is null || !callHistoryWindow.IsVisible)
            return;
        callHistoryWindow.SetSnapToWindow(viewModel.SnapCallHistoryToWindow, this);
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
        var closeButton = new Button
        {
            Content = "OK",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            MinWidth = 80
        };
        var dialog = new Window
        {
            Title = title,
            Width = 500,
            MinHeight = 210,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 18,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    closeButton
                }
            }
        };
        closeButton.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private void HandleTransmitSelectionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ChannelViewModel channel } button)
        {
            viewModel.ToggleChannelTransmitSelection(channel);
            ApplyTransmitSelectionButtonBrush(button, channel);
        }
    }

    private static void HandleTransmitSelectionPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Button { DataContext: ChannelViewModel channel } button)
            ApplyTransmitSelectionButtonBrush(button, channel);
    }

    private static void ApplyTransmitSelectionButtonBrush(Button button, ChannelViewModel channel)
    {
        button.Background = channel.TransmitSelectionBrush;
        button.BorderBrush = channel.TransmitSelectionBorderBrush;
    }

    private void HandlePageSelectionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ChannelViewModel channel } button)
        {
            viewModel.ToggleChannelPageSelection(channel);
            ApplyPageSelectionButtonBrush(button, channel);
        }
    }

    private static void HandlePageSelectionPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Button { DataContext: ChannelViewModel channel } button)
            ApplyPageSelectionButtonBrush(button, channel);
    }

    private static void ApplyPageSelectionButtonBrush(Button button, ChannelViewModel channel)
    {
        button.Background = channel.PageSelectionBrush;
        button.BorderBrush = channel.PageSelectionBorderBrush;
    }

    private void HandleAlertSelectionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ChannelViewModel channel } button)
        {
            viewModel.ToggleChannelAlertSelection(channel);
            ApplyAlertSelectionButtonBrush(button, channel);
        }
    }

    private async void HandleSystemStatusClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SystemViewModel system })
            await viewModel.ToggleSystemConnectionAsync(system);
    }

    private static void HandleAlertSelectionPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Button { DataContext: ChannelViewModel channel } button)
            ApplyAlertSelectionButtonBrush(button, channel);
    }

    private static void ApplyAlertSelectionButtonBrush(Button button, ChannelViewModel channel)
    {
        button.Background = channel.AlertSelectionBrush;
        button.BorderBrush = channel.AlertSelectionBorderBrush;
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
            _ => default
        };
        return key is Key.Space or (>= Key.F1 and <= Key.F12);
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

    private void HandleDeleteRecordingClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CallRecordingMetadata metadata } &&
            DataContext is MainWindowViewModel viewModel)
        {
            _ = viewModel.DeleteRecordingAsync(metadata);
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

public sealed class MainWindowViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    internal const double ChannelWidgetSpacing = 8;
    internal const double DefaultWidgetCanvasWidth = 900;
    private const int MaximumSubscriberCommandAuditEntries = 50;
    private static readonly int[] SerialPttBaudRateOptions = [1_200, 2_400, 4_800, 9_600, 19_200, 38_400, 57_600, 115_200];
    private readonly ChannelReceiveAudioCoordinator audioCoordinator;
    private readonly UserSettingsStore userSettingsStore;
    private readonly UserSettings userSettings;
    private readonly string codeplugDiagnosticsText;
    private readonly ChannelTransmitCoordinator transmitCoordinator;
    private readonly ToneTransmitCoordinator toneTransmitCoordinator;
    private readonly TalkPermitTonePlayer talkPermitTonePlayer;
    private readonly PatchForwardingCoordinator patchForwarding;
    private readonly PatchSourceDecodeCoordinator patchSourceDecode;
    private readonly P25KeyRing? p25KeyRing;
    private KeyboardPttSource keyboardPtt;
    private GlobalKeyboardPttSource? globalKeyboardPtt;
    private IPttSource? serialPtt;
    private readonly Func<string, int, IPttSource> serialPttFactory;
    private readonly Func<IReadOnlyList<string>> serialPortProvider;
    private readonly SemaphoreSlim serialPttChangeLock = new(1, 1);
    private readonly ObservableCollection<string> serialPttPortOptions = [];
    private readonly CallHistoryStore callHistory = new();
    private readonly ObservableCollection<CallRecordingMetadata> recordingEntries = [];
    private readonly ObservableCollection<DtmfPresetViewModel> dtmfPresets = [];
    private readonly ObservableCollection<TonePresetViewModel> tonePresets = [];
    private readonly ObservableCollection<AlertToneViewModel> alertTones = [];
    private readonly ObservableCollection<BuiltInAlertToneViewModel> builtInAlertTones = [];
    private readonly ObservableCollection<ToolbarClockViewModel> toolbarClocks = [];
    private readonly ObservableCollection<AudioInputPresetViewModel> audioInputPresets = [];
    private readonly ObservableCollection<AudioDeviceOptionViewModel> audioInputDevices = [];
    private readonly ObservableCollection<AudioDeviceOptionViewModel> audioOutputDevices = [];
    private readonly ObservableCollection<SubscriberCommandAuditEntry> subscriberCommandAudit = [];
    private readonly ObservableCollection<DebugLogEntry> debugLogEntries = [];
    private readonly ObservableCollection<string> recentCodeplugPaths = [];
    private readonly ObservableCollection<WebStreamViewModel> webStreams = [];
    private readonly WebStreamPlaybackCoordinator webStreamPlayback;
    private readonly object patchSourceWorkSync = new();
    private readonly Dictionary<ChannelViewModel, Task> patchSourceWork = [];
    private readonly object receiveAudioWorkSync = new();
    private readonly Dictionary<ChannelViewModel, Task> receiveAudioWork = [];
    private readonly Dictionary<string, FneConnectionState> lastConnectionStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConnectionChimeTracker connectionChimeTracker = new();
    private ChannelViewModel[] suspendedAudioChannels = [];
    private PatchGroupEditorViewModel? activeMultiSelectGroup;
    private readonly CallRecordingManager callRecordings;
    private readonly RecordingPlaybackCoordinator recordingPlayback;
    private readonly DispatcherTimer clockTimer;
    private Bitmap? userBackgroundBitmap;
    private int disposeStarted;
    private IBrush mainBackgroundBrush = new SolidColorBrush(Color.Parse("#0D1116"));
    private string statusText;
    private string audioStatusText = "RX audio disabled.";
    private string transmitStatusText = "PTT idle.";
    private string dtmfDigits = "123";
    private string toneFrequencyText = "1000";
    private string toneDurationText = "1.0";
    private string quickCallToneAText = "600";
    private string quickCallToneBText = "1200";
    private string audioInputDeviceIdText = "default";
    private string audioOutputDeviceIdText = "default";
    private string audioInputGainText = "1.0";
    private string audioInputLowGainText = "0";
    private string audioInputMidGainText = "0";
    private string audioInputHighGainText = "0";
    private bool audioInputAgcEnabled;
    private string audioInputPresetNameText = string.Empty;
    private string dtmfPresetName = string.Empty;
    private string tonePresetName = string.Empty;
    private string alertToneNameText = string.Empty;
    private string recordingRetentionDaysText = string.Empty;
    private string recordingRootPathText = string.Empty;
    private string recordingDirectionFilter = "All";
    private string recordingProtocolFilter = "All";
    private string recordingEncryptionFilter = "All";
    private string recordingSystemFilterText = string.Empty;
    private string recordingChannelFilterText = string.Empty;
    private string recordingTalkgroupFilterText = string.Empty;
    private string recordingSubscriberFilterText = string.Empty;
    private string recordingAliasFilterText = string.Empty;
    private DateTimeOffset? recordingStartDateFilter;
    private DateTimeOffset? recordingEndDateFilter;
    private bool recordingTimeColumnVisible = true;
    private bool recordingDurationColumnVisible = true;
    private bool recordingChannelColumnVisible = true;
    private bool recordingTalkgroupColumnVisible = true;
    private bool recordingSourceIdColumnVisible = true;
    private bool recordingAliasColumnVisible = true;
    private bool recordingDirectionColumnVisible;
    private bool recordingProtocolColumnVisible;
    private bool recordingSystemColumnVisible;
    private bool recordingEncryptionColumnVisible;
    private bool recordingDiagnosticsColumnVisible = true;
    private string clockText = string.Empty;
    private string debugLogFilterText = string.Empty;
    private string debugLogSeverityFilter = "All";
    private string callHistoryFilterText = string.Empty;
    private string recordingFilterText = string.Empty;
    private bool busy;
    private bool pttStarted;
    private bool serialPttEnabled;
    private string serialPttPortName = string.Empty;
    private int serialPttBaudRate = 9_600;
    private string serialPttStatusText = "Serial PTT is disabled.";
    private ChannelViewModel? selectedChannel;
    private SystemViewModel? selectedSystem;
    private AudioDeviceOptionViewModel? selectedAudioInputDevice;
    private AudioDeviceOptionViewModel? selectedAudioOutputDevice;
    private readonly ScaleTransform uiScaleTransform;

    private MainWindowViewModel(
        string statusText,
        IEnumerable<SystemViewModel> systems,
        IEnumerable<ZoneViewModel> zones,
        IP25KeyResolver? p25KeyResolver = null,
        UserSettingsStore? userSettingsStore = null,
        IEnumerable<GroupConfiguration>? groupDefinitions = null,
        bool patchSourceIdPassthrough = false,
        Func<IReadOnlyList<string>>? serialPortProvider = null,
        Func<string, int, IPttSource>? serialPttFactory = null)
    {
        this.statusText = statusText;
        codeplugDiagnosticsText = statusText;
        this.userSettingsStore = userSettingsStore ?? new UserSettingsStore(UserSettingsStore.DefaultPath);
        userSettings = this.userSettingsStore.Load();
        this.serialPortProvider = serialPortProvider ?? SerialPttSource.GetAvailablePortNames;
        this.serialPttFactory = serialPttFactory ?? ((portName, baudRate) => new SerialPttSource(portName, baudRate));
        uiScaleTransform = new ScaleTransform
        {
            ScaleX = userSettings.UiScale,
            ScaleY = userSettings.UiScale
        };
        foreach (string path in userSettings.RecentCodeplugPaths.Take(UserSettings.MaximumRecentCodeplugs))
            recentCodeplugPaths.Add(path);
        LoadUserBackground(userSettings.UserBackgroundImage);
        ApplyTheme(userSettings.DarkMode);
        keyboardPtt = new KeyboardPttSource(ParseGlobalPttKey(userSettings.GlobalPttKey))
        {
            ToggleMode = userSettings.TogglePttMode
        };
        serialPttEnabled = userSettings.SerialPttEnabled;
        serialPttPortName = userSettings.SerialPttPortName;
        serialPttBaudRate = userSettings.SerialPttBaudRate;
        string? environmentSerialPort = Environment.GetEnvironmentVariable("DVM_PTT_SERIAL_PORT");
        if (serialPttPortName.Length == 0 && !string.IsNullOrWhiteSpace(environmentSerialPort))
        {
            serialPttEnabled = true;
            serialPttPortName = environmentSerialPort.Trim();
            serialPttBaudRate = ReadSerialPttBaudRate();
        }
        RefreshSerialPttDevices();
        if (serialPttEnabled && serialPttPortName.Length > 0)
        {
            serialPtt = this.serialPttFactory(serialPttPortName, serialPttBaudRate);
            serialPttStatusText = $"Configured for {serialPttPortName} at {serialPttBaudRate:N0} baud.";
        }
        clockText = FormatClock(DateTime.Now, userSettings.ClockUse24HourTime, userSettings.ClockShowSeconds);
        clockTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        clockTimer.Tick += HandleClockTick;
        clockTimer.Start();
        dtmfDigits = userSettings.LastDtmfDigits;
        toneFrequencyText = userSettings.ToneFrequencyHz.ToString("0.###", CultureInfo.InvariantCulture);
        toneDurationText = userSettings.ToneDurationSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        quickCallToneAText = userSettings.QuickCallToneAFrequencyHz.ToString("0.###", CultureInfo.InvariantCulture);
        quickCallToneBText = userSettings.QuickCallToneBFrequencyHz.ToString("0.###", CultureInfo.InvariantCulture);
        audioInputDeviceIdText = userSettings.AudioInputDeviceId;
        audioOutputDeviceIdText = userSettings.AudioOutputDeviceId;
        audioInputGainText = userSettings.AudioInputGain.ToString("0.###", CultureInfo.InvariantCulture);
        audioInputLowGainText = userSettings.AudioInputEqLowGainDb.ToString("0.###", CultureInfo.InvariantCulture);
        audioInputMidGainText = userSettings.AudioInputEqMidGainDb.ToString("0.###", CultureInfo.InvariantCulture);
        audioInputHighGainText = userSettings.AudioInputEqHighGainDb.ToString("0.###", CultureInfo.InvariantCulture);
        audioInputAgcEnabled = userSettings.AudioInputAgcEnabled;
        audioInputPresetNameText = userSettings.AudioInputPresetName;
        recordingRetentionDaysText = userSettings.RecordingRetentionDays.ToString(CultureInfo.InvariantCulture);
        recordingRootPathText = GetDefaultRecordingRoot(userSettings.RecordingRootPath);
        webStreamPlayback = new WebStreamPlaybackCoordinator(
            () => AudioBackendFactory.CreateDefault(Environment.GetEnvironmentVariable("DVM_AUDIO_LIBRARY")),
            () => userSettings.AudioOutputDeviceId,
            getStreamOutputDeviceId: GetWebStreamOutputDeviceId);
        foreach (DtmfPresetSetting preset in userSettings.DtmfPresets)
            dtmfPresets.Add(new DtmfPresetViewModel(preset));
        foreach (TonePresetSetting preset in userSettings.TonePresets)
            tonePresets.Add(new TonePresetViewModel(preset));
        foreach (AlertToneSetting tone in userSettings.AlertTones)
            alertTones.Add(new AlertToneViewModel(tone));
        builtInAlertTones.Add(new BuiltInAlertToneViewModel(LegacyAlertTone.Alert1));
        builtInAlertTones.Add(new BuiltInAlertToneViewModel(LegacyAlertTone.Alert2));
        builtInAlertTones.Add(new BuiltInAlertToneViewModel(LegacyAlertTone.Alert3));
        List<ToolbarClockSetting> configuredClocks = (userSettings.ToolbarClocks ?? [])
            .Take(UserSettings.MaximumToolbarClocks)
            .ToList();
        while (configuredClocks.Count < UserSettings.MaximumToolbarClocks)
            configuredClocks.Add(new ToolbarClockSetting());
        for (int index = 0; index < configuredClocks.Count; index++)
            toolbarClocks.Add(new ToolbarClockViewModel(index + 1, configuredClocks[index]));
        RefreshClock();
        foreach (AudioInputPresetSetting preset in userSettings.AudioInputPresets)
            audioInputPresets.Add(new AudioInputPresetViewModel(preset));
        p25KeyRing = p25KeyResolver as P25KeyRing;
        callRecordings = new CallRecordingManager(
            recordingRootPathText,
            HandleRecordingFaulted,
            userSettings.RecordingRetentionDays,
            ShouldRecordSource);
        recordingPlayback = new RecordingPlaybackCoordinator(
            () => AudioBackendFactory.CreateDefault(Environment.GetEnvironmentVariable("DVM_AUDIO_LIBRARY")),
            () => userSettings.AudioOutputDeviceId,
            HandleRecordingPlaybackFaulted);
        audioCoordinator = new ChannelReceiveAudioCoordinator(
            p25KeyResolver,
            HandleDecodedSamples,
            GetChannelVolume,
            GetChannelOutputDeviceId);
        transmitCoordinator = new ChannelTransmitCoordinator(
            p25KeyResolver,
            new AudioInputProcessingOptions
            {
                DeviceId = userSettings.AudioInputDeviceId,
                AgcEnabled = userSettings.AudioInputAgcEnabled,
                Gain = userSettings.AudioInputGain,
                LowGainDb = userSettings.AudioInputEqLowGainDb,
                MidGainDb = userSettings.AudioInputEqMidGainDb,
                HighGainDb = userSettings.AudioInputEqHighGainDb
            },
            HandleTransmitSamples);
        toneTransmitCoordinator = new ToneTransmitCoordinator(p25KeyResolver);
        talkPermitTonePlayer = new TalkPermitTonePlayer(
            () => AudioBackendFactory.CreateDefault(Environment.GetEnvironmentVariable("DVM_AUDIO_LIBRARY")),
            () => userSettings.AudioOutputDeviceId);
        Systems = systems.ToArray();
        Zones = zones.ToArray();
        RestoreChannelWidgetLayout();
        foreach (ZoneViewModel zone in Zones)
            zone.SetWidgetCardHeight(ChannelCardHeight);
        foreach (ChannelViewModel channel in Systems.SelectMany(system => system.Channels).Distinct())
            channel.SetDarkMode(userSettings.DarkMode);
        foreach (ZoneViewModel zone in Zones)
            zone.SetDarkMode(userSettings.DarkMode);
        GroupConfiguration[] configuredGroups = (groupDefinitions ?? []).ToArray();
        patchForwarding = new PatchForwardingCoordinator(Systems, p25KeyResolver)
        {
            SourceIdPassthrough = patchSourceIdPassthrough
        };
        patchSourceDecode = new PatchSourceDecodeCoordinator(p25KeyResolver, ObservePatchDecodedSamples);
        RestorePatchState(configuredGroups);
        PatchGroups = BuildPatchGroups(configuredGroups);
        RefreshPatchMembershipConflicts();
        CallHistory = new System.Collections.ObjectModel.ReadOnlyObservableCollection<CallHistoryEntry>(callHistory.Entries);
        Recordings = new ReadOnlyObservableCollection<CallRecordingMetadata>(recordingEntries);
        DtmfPresets = new ReadOnlyObservableCollection<DtmfPresetViewModel>(dtmfPresets);
        TonePresets = new ReadOnlyObservableCollection<TonePresetViewModel>(tonePresets);
        AlertTones = new ReadOnlyObservableCollection<AlertToneViewModel>(alertTones);
        BuiltInAlertTones = new ReadOnlyObservableCollection<BuiltInAlertToneViewModel>(builtInAlertTones);
        ToolbarClocks = new ReadOnlyObservableCollection<ToolbarClockViewModel>(toolbarClocks);
        AudioInputPresets = new ReadOnlyObservableCollection<AudioInputPresetViewModel>(audioInputPresets);
        AudioInputDevices = new ReadOnlyObservableCollection<AudioDeviceOptionViewModel>(audioInputDevices);
        AudioOutputDevices = new ReadOnlyObservableCollection<AudioDeviceOptionViewModel>(audioOutputDevices);
        SubscriberCommandAudit = new ReadOnlyObservableCollection<SubscriberCommandAuditEntry>(subscriberCommandAudit);
        DebugLogEntries = new ReadOnlyObservableCollection<DebugLogEntry>(debugLogEntries);
        RecentCodeplugPaths = new ReadOnlyObservableCollection<string>(recentCodeplugPaths);
        WebStreams = new ReadOnlyObservableCollection<WebStreamViewModel>(webStreams);
        foreach (WebStreamViewModel stream in Zones.SelectMany(zone => zone.WebStreams))
        {
            stream.SetOutputDeviceOptions(AudioOutputDevices);
            stream.SetInitialVolume(
                userSettings.WebStreamVolumes.TryGetValue(stream.Name, out double savedVolume)
                    ? savedVolume
                    : 1.0);
            stream.RestoreOutputDeviceId(
                userSettings.WebStreamOutputDeviceIds.TryGetValue(stream.Name, out string? savedOutputDeviceId)
                    ? savedOutputDeviceId
                    : string.Empty);
            stream.VolumeChanged += HandleWebStreamVolumeChanged;
            stream.PropertyChanged += HandleWebStreamPropertyChanged;
            stream.Configure(StartWebStreamAsync, StopWebStreamAsync);
            webStreams.Add(stream);
        }
        _ = RestoreSelectedWebStreamsAsync();
        callRecordings.PruneExpired();
        RefreshRecordingsCore();
        foreach (ChannelViewModel channel in Systems.SelectMany(system => system.Channels))
        {
            channel.SetOutputDeviceOptions(AudioOutputDevices);
            if (channel.Definition.SelectableEncryption &&
                userSettings.TransmitEncryptionStates.TryGetValue(channel.SettingsKey, out bool savedEncryptionState))
            {
                channel.RestoreTransmitEncryption(savedEncryptionState);
            }

            channel.RestoreVolume(
                userSettings.ChannelVolumes.TryGetValue(channel.SettingsKey, out double savedVolume)
                    ? savedVolume
                    : 1.0);
            channel.RestoreOutputDeviceId(
                userSettings.ChannelOutputDeviceIds.TryGetValue(channel.SettingsKey, out string? savedOutputDeviceId)
                    ? savedOutputDeviceId
                    : string.Empty);
            channel.RestoreRecordingEnabled(userSettings.RecordingEnabledChannelKeys.Contains(
                channel.SettingsKey,
                StringComparer.OrdinalIgnoreCase));
            channel.TransmitEncryptionChanged += HandleChannelEncryptionChanged;
            channel.RecordingStateChanged += HandleChannelRecordingChanged;
            channel.VolumeChanged += HandleChannelVolumeChanged;
            channel.SetIgnoredSubscriberIds(
                userSettings.RecordingIgnoredSubscriberIds.TryGetValue(
                    channel.SettingsKey,
                    out List<uint>? ignoredSubscriberIds)
                    ? ignoredSubscriberIds
                    : []);
            channel.ConfigureAudio(StartAudioAsync, StopAudioAsync);
            channel.ConfigureTransmit(StartTransmitAsync, StopTransmitAsync);
            channel.RestoreTransmitSelection(userSettings.TransmitSelectedChannelKeys.Contains(
                channel.SettingsKey,
                StringComparer.OrdinalIgnoreCase));
            if (channel.IsRecordingEnabled)
                _ = EnsureRecordingAudioAsync(channel);
        }

        foreach (SystemViewModel system in Systems)
        {
            system.StatusChanged += (_, status) => HandleSystemStatus(system, status);
            system.LogReceived += HandleSystemLog;
            system.TrafficReceived += (_, traffic) => HandleSystemTraffic(system, traffic);
            system.KeyResponseReceived += HandleSystemKeyResponse;
        }

        selectedChannel = userSettings.RestoreSelectedChannelsOnStartup
            ? Systems
                .SelectMany(system => system.Channels)
                .FirstOrDefault(channel => channel.SettingsKey.Equals(
                    userSettings.LastSelectedChannelKey,
                    StringComparison.Ordinal))
            : null;
        selectedSystem = userSettings.RestoreSelectedChannelsOnStartup
            ? Systems.FirstOrDefault(system => system.Name.Equals(
                userSettings.LastSelectedSystemName,
                StringComparison.OrdinalIgnoreCase)) ??
                Systems.FirstOrDefault(system => selectedChannel is not null && system.Channels.Contains(selectedChannel)) ??
                Systems.FirstOrDefault()
            : Systems.FirstOrDefault();
        foreach (SystemViewModel system in Systems)
            system.SetSelected(ReferenceEquals(system, selectedSystem));

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !busy && Systems.Count > 0);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => !busy && Systems.Count > 0);
        SendDtmfCommand = new AsyncRelayCommand(SendDtmfAsync, CanSendGeneratedAudio);
        SendToneCommand = new AsyncRelayCommand(SendToneAsync, CanSendGeneratedAudio);
        SaveDtmfPresetCommand = new RelayCommand(SaveDtmfPreset);
        SaveTonePresetCommand = new RelayCommand(SaveTonePreset);
        ApplyAudioInputSettingsCommand = new RelayCommand(ApplyAudioInputSettings);
        ApplyRecordingRetentionCommand = new RelayCommand(ApplyRecordingRetention);
        RefreshAudioDevicesCommand = new RelayCommand(RefreshAudioDevices);
        RefreshAudioDevices();
        transmitCoordinator.Faulted += HandleTransmitFaulted;
        keyboardPtt.StateChanged += HandleKeyboardPttStateChanged;
        if (serialPtt is not null)
            serialPtt.StateChanged += HandleKeyboardPttStateChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string StatusText
    {
        get => statusText;
        private set => SetField(ref statusText, value);
    }

    public bool IsCodeplugLoaded => Systems.Count > 0;

    public string? CurrentCodeplugPath => userSettings.LastCodeplugPath;

    public string SettingsVersionText => userSettings.SchemaVersion == UserSettings.CurrentSchemaVersion
        ? $"Profile format v{userSettings.SchemaVersion}"
        : userSettings.SchemaVersion > UserSettings.CurrentSchemaVersion
            ? $"Profile format v{userSettings.SchemaVersion} (newer than this build)"
            : $"Profile format v{userSettings.SchemaVersion} (legacy)";

    public ReadOnlyObservableCollection<string> RecentCodeplugPaths { get; }

    public IReadOnlyList<string> NamedSettingsProfiles => userSettingsStore.ListNamedProfiles();

    public bool HasCodeplugDiagnostics => !IsCodeplugLoaded || codeplugDiagnosticsText.Contains('\n');

    public string CodeplugDiagnosticsText => codeplugDiagnosticsText;

    public bool ShowCallHistoryPane
    {
        get => userSettings.ShowCallHistoryPane;
        set
        {
            if (userSettings.ShowCallHistoryPane == value)
                return;
            userSettings.ShowCallHistoryPane = value;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowCallHistoryPane)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActivitySidebarCollapsed)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActivitySidebarWidth)));
        }
    }

    public bool IsActivitySidebarCollapsed => !ShowCallHistoryPane;

    public double ActivitySidebarWidth => ShowCallHistoryPane ? 250 : 34;

    public bool SnapCallHistoryToWindow
    {
        get => userSettings.SnapCallHistoryToWindow;
        set
        {
            if (userSettings.SnapCallHistoryToWindow == value)
                return;
            userSettings.SnapCallHistoryToWindow = value;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SnapCallHistoryToWindow)));
        }
    }

    public bool ShowSystemStatus
    {
        get => userSettings.ShowSystemStatus;
        set
        {
            if (userSettings.ShowSystemStatus == value)
                return;
            userSettings.ShowSystemStatus = value;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowSystemStatus)));
        }
    }

    public double UiFontSize
    {
        get => userSettings.UiFontSize;
        set
        {
            double normalized = Math.Clamp(value, 11, 20);
            if (Math.Abs(userSettings.UiFontSize - normalized) < 0.001)
                return;
            userSettings.UiFontSize = normalized;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UiFontSize)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UiFontSizeText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UiSmallFontSize)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UiCompactFontSize)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UiHeadingFontSize)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChannelCardHeight)));
            if (userSettings.ChannelWidgetPositions.Count == 0)
                ApplyDefaultChannelWidgetLayout();
            foreach (ZoneViewModel zone in Zones)
            {
                zone.SetWidgetCardHeight(ChannelCardHeight);
                zone.RefreshWidgetCanvasBounds();
            }
        }
    }

    public string UiFontSizeText => $"Text size: {UiFontSize:0}";
    public double UiSmallFontSize => UiFontSize - 2;
    public double UiCompactFontSize => UiFontSize - 3;
    public double UiHeadingFontSize => UiFontSize + 4;
    public double ChannelCardHeight => 122 + ((UiFontSize - 14) * 3);

    public double UiScale
    {
        get => userSettings.UiScale;
        set
        {
            double normalized = Math.Clamp(value, 0.75, 1.5);
            if (Math.Abs(userSettings.UiScale - normalized) < 0.001)
                return;
            userSettings.UiScale = normalized;
            uiScaleTransform.ScaleX = normalized;
            uiScaleTransform.ScaleY = normalized;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UiScale)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UiScaleText)));
        }
    }

    public string UiScaleText => $"Interface scale: {UiScale * 100:0}%";
    public ScaleTransform UiScaleTransform => uiScaleTransform;

    public bool ShowChannels
    {
        get => userSettings.ShowChannels;
        set
        {
            if (userSettings.ShowChannels == value)
                return;
            userSettings.ShowChannels = value;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowChannels)));
        }
    }

    public bool ShowAlertTones
    {
        get => userSettings.ShowAlertTones;
        set
        {
            if (userSettings.ShowAlertTones == value)
                return;
            userSettings.ShowAlertTones = value;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowAlertTones)));
        }
    }

    public bool LockWidgets
    {
        get => userSettings.LockWidgets;
        set
        {
            if (userSettings.LockWidgets == value)
                return;
            userSettings.LockWidgets = value;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LockWidgets)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanResizeLayout)));
        }
    }

    public IBrush MainBackgroundBrush => mainBackgroundBrush;

    public bool CanResizeLayout => !userSettings.LockWidgets;

    public string? UserBackgroundImage => userSettings.UserBackgroundImage;

    public bool SetUserBackground(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("The background image was not found.", fullPath);

            Bitmap bitmap = new(fullPath);
            userBackgroundBitmap?.Dispose();
            userBackgroundBitmap = bitmap;
            mainBackgroundBrush = new ImageBrush(bitmap)
            {
                Stretch = Stretch.UniformToFill,
                Opacity = 0.22
            };
            userSettings.UserBackgroundImage = fullPath;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MainBackgroundBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UserBackgroundImage)));
            StatusText = $"Background loaded: {Path.GetFileName(fullPath)}.";
            return true;
        }
        catch (Exception exception)
        {
            StatusText = $"Background unavailable: {exception.Message}";
            return false;
        }
    }

    public void ClearUserBackground()
    {
        userBackgroundBitmap?.Dispose();
        userBackgroundBitmap = null;
        mainBackgroundBrush = CreateShellBackgroundBrush(userSettings.DarkMode);
        userSettings.UserBackgroundImage = null;
        PersistUserSettings();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MainBackgroundBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UserBackgroundImage)));
        StatusText = "User background cleared.";
    }

    public void ResetLayout()
    {
        userSettings.ShowSystemStatus = true;
        userSettings.ShowChannels = true;
        userSettings.ShowAlertTones = true;
        userSettings.LockWidgets = true;
        userSettings.ShowCallHistoryPane = true;
        userSettings.SnapCallHistoryToWindow = false;
        userSettings.CallHistoryWindowPlacement = new WindowPlacementSetting();
        userSettings.ChannelWidgetPositions.Clear();
        ApplyDefaultChannelWidgetLayout();
        PersistUserSettings();
        foreach (string propertyName in new[]
                 {
                     nameof(ShowSystemStatus),
                     nameof(ShowChannels),
                     nameof(ShowAlertTones),
                     nameof(LockWidgets),
                     nameof(CanResizeLayout),
                     nameof(ShowCallHistoryPane),
                     nameof(SnapCallHistoryToWindow)
                 })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        StatusText = "Channel widgets reset to their default positions and locked.";
    }

    public void MoveChannelWidget(ChannelViewModel channel, double x, double y, bool persist)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (userSettings.LockWidgets)
            return;

        channel.SetWidgetPosition(x, y);
        if (!persist)
            return;

        userSettings.ChannelWidgetPositions[channel.SettingsKey] = new WidgetPositionSetting
        {
            X = channel.WidgetX,
            Y = channel.WidgetY
        };
        PersistUserSettings();
        StatusText = $"Moved {channel.Name} to {channel.WidgetX:0}, {channel.WidgetY:0}.";
    }

    public WindowPlacementSetting GetCallHistoryWindowPlacement()
    {
        WindowPlacementSetting placement = userSettings.CallHistoryWindowPlacement;
        return new WindowPlacementSetting
        {
            Left = placement.Left,
            Top = placement.Top,
            Width = placement.Width,
            Height = placement.Height
        };
    }

    public void SaveCallHistoryWindowPlacement(WindowPlacementSetting placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        userSettings.CallHistoryWindowPlacement = new WindowPlacementSetting
        {
            Left = placement.Left,
            Top = placement.Top,
            Width = placement.Width,
            Height = placement.Height
        };
        PersistUserSettings();
    }

    public void ExportSettings(string path)
        => userSettingsStore.Export(userSettings, path);

    public SettingsImportPreview PreviewSettingsImport(string path)
        => userSettingsStore.PreviewImport(path);

    public SettingsImportPreview PreviewNamedSettingsProfile(string profileName)
        => userSettingsStore.PreviewNamedProfile(profileName);

    public void ImportSettings(string path, SettingsImportScope scope = SettingsImportScope.All)
        => userSettingsStore.Import(path, scope);

    public void SaveNamedSettingsProfile(string profileName)
    {
        userSettingsStore.SaveNamedProfile(profileName, userSettings);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NamedSettingsProfiles)));
        StatusText = $"Settings profile '{profileName.Trim()}' saved.";
    }

    public void ImportNamedSettingsProfile(
        string profileName,
        SettingsImportScope scope = SettingsImportScope.OperatorState)
    {
        userSettingsStore.ImportNamedProfile(profileName, scope);
        StatusText = $"Settings profile '{profileName.Trim()}' imported.";
    }

    public void DeleteNamedSettingsProfile(string profileName)
    {
        userSettingsStore.DeleteNamedProfile(profileName);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NamedSettingsProfiles)));
        StatusText = $"Settings profile '{profileName.Trim()}' deleted.";
    }

    public void ResetSettings()
        => userSettingsStore.Reset();

    public void ClearCallHistory()
    {
        callHistory.Clear();
        NotifyCallHistoryChanged();
        StatusText = "Activity history cleared.";
    }

    public void AddEventHistory(
        string source,
        string message,
        string? ridText = null,
        string? tgidText = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        void Apply()
        {
            callHistory.AddEvent(DateTimeOffset.Now, source, message, ridText, tgidText);
            NotifyCallHistoryChanged();
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
    }

    public void ExportCallHistory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var lines = new List<string>
        {
            "Start,End,DurationSeconds,System,Channel,SourceId,Caller,Talkgroup,Protocol,Encryption,StreamId"
        };
        lines.AddRange(CallHistory.Select(entry => string.Join(",",
            Csv(entry.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            Csv(entry.EndTimestamp?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty),
            Csv(entry.Duration?.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty),
            Csv(entry.SystemName),
            Csv(entry.DisplayChannelText),
            Csv(entry.DisplaySourceText),
            Csv(entry.CallerText),
            Csv(entry.DisplayDestinationText),
            Csv(entry.ProtocolText),
            Csv(entry.EncryptionText),
            entry.StreamId.ToString(CultureInfo.InvariantCulture))));
        File.WriteAllLines(fullPath, lines);
        StatusText = $"Exported {CallHistory.Count} activity-history entr{(CallHistory.Count == 1 ? "y" : "ies")}.";
    }

    private static string Csv(string value)
        => $"\"{value.Replace("\"", "\"\"")}\"";

    public string AudioStatusText
    {
        get => audioStatusText;
        private set => SetField(ref audioStatusText, value);
    }

    public string TransmitStatusText
    {
        get => transmitStatusText;
        private set => SetField(ref transmitStatusText, value);
    }

    public string DtmfDigits
    {
        get => dtmfDigits;
        set => SetField(ref dtmfDigits, value ?? string.Empty);
    }

    public string ToneFrequencyText
    {
        get => toneFrequencyText;
        set => SetField(ref toneFrequencyText, value ?? string.Empty);
    }

    public string ToneDurationText
    {
        get => toneDurationText;
        set => SetField(ref toneDurationText, value ?? string.Empty);
    }

    public string AudioInputDeviceIdText
    {
        get => audioInputDeviceIdText;
        set => SetField(ref audioInputDeviceIdText, value ?? string.Empty);
    }

    public string AudioOutputDeviceIdText
    {
        get => audioOutputDeviceIdText;
        set => SetField(ref audioOutputDeviceIdText, value ?? string.Empty);
    }

    public AudioDeviceOptionViewModel? SelectedAudioInputDevice
    {
        get => selectedAudioInputDevice;
        set
        {
            if (ReferenceEquals(selectedAudioInputDevice, value))
                return;
            selectedAudioInputDevice = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedAudioInputDevice)));
            if (value is not null)
                ApplySelectedAudioDevices();
        }
    }

    public AudioDeviceOptionViewModel? SelectedAudioOutputDevice
    {
        get => selectedAudioOutputDevice;
        set
        {
            if (ReferenceEquals(selectedAudioOutputDevice, value))
                return;
            selectedAudioOutputDevice = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedAudioOutputDevice)));
            if (value is not null)
                ApplySelectedAudioDevices();
        }
    }

    public string AudioInputGainText
    {
        get => audioInputGainText;
        set => SetField(ref audioInputGainText, value ?? string.Empty);
    }

    public string AudioInputLowGainText
    {
        get => audioInputLowGainText;
        set => SetField(ref audioInputLowGainText, value ?? string.Empty);
    }

    public string AudioInputMidGainText
    {
        get => audioInputMidGainText;
        set => SetField(ref audioInputMidGainText, value ?? string.Empty);
    }

    public string AudioInputHighGainText
    {
        get => audioInputHighGainText;
        set => SetField(ref audioInputHighGainText, value ?? string.Empty);
    }

    public bool AudioInputAgcEnabled
    {
        get => audioInputAgcEnabled;
        set => SetField(ref audioInputAgcEnabled, value);
    }

    public string AudioInputPresetNameText
    {
        get => audioInputPresetNameText;
        set => SetField(ref audioInputPresetNameText, value ?? string.Empty);
    }

    public bool MuteRxAudioWhileTransmitting
    {
        get => userSettings.MuteRxAudioWhileTransmitting;
        set
        {
            if (userSettings.MuteRxAudioWhileTransmitting == value)
                return;
            userSettings.MuteRxAudioWhileTransmitting = value;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MuteRxAudioWhileTransmitting)));
        }
    }

    public bool TalkPermitTone
    {
        get => userSettings.TalkPermitTone;
        set
        {
            if (userSettings.TalkPermitTone == value)
                return;
            userSettings.TalkPermitTone = value;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TalkPermitTone)));
        }
    }

    public bool ConnectionChimes
    {
        get => userSettings.ConnectionChimes;
        set
        {
            if (userSettings.ConnectionChimes == value)
                return;
            userSettings.ConnectionChimes = value;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionChimes)));
        }
    }

    public bool DarkMode
    {
        get => userSettings.DarkMode;
        set
        {
            if (userSettings.DarkMode == value)
                return;
            userSettings.DarkMode = value;
            ApplyTheme(value);
            foreach (ChannelViewModel channel in Systems.SelectMany(system => system.Channels).Distinct())
                channel.SetDarkMode(value);
            foreach (ZoneViewModel zone in Zones)
                zone.SetDarkMode(value);
            if (userBackgroundBitmap is null)
            {
                mainBackgroundBrush = CreateShellBackgroundBrush(value);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MainBackgroundBrush)));
            }
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DarkMode)));
        }
    }

    public string ClockText => clockText;

    public bool ClockUse24HourTime
    {
        get => userSettings.ClockUse24HourTime;
        set
        {
            if (userSettings.ClockUse24HourTime == value)
                return;
            userSettings.ClockUse24HourTime = value;
            RefreshClock();
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ClockUse24HourTime)));
        }
    }

    public bool ClockShowSeconds
    {
        get => userSettings.ClockShowSeconds;
        set
        {
            if (userSettings.ClockShowSeconds == value)
                return;
            userSettings.ClockShowSeconds = value;
            RefreshClock();
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ClockShowSeconds)));
        }
    }

    public bool SaveToolbarClocks()
    {
        List<ToolbarClockSetting> settings = [];
        foreach (ToolbarClockViewModel clock in toolbarClocks)
        {
            if (!clock.TryGetUtcOffset(out _))
            {
                StatusText = $"{clock.SlotLabel} must use a UTC offset from -12 to +14.";
                return false;
            }
            settings.Add(clock.ToSetting());
        }

        userSettings.ToolbarClocks = settings;
        PersistUserSettings();
        RefreshClock();
        StatusText = $"Saved {settings.Count(clock => clock.Enabled)} toolbar clock(s).";
        return true;
    }

    public bool KeepWindowOnTop
    {
        get => userSettings.KeepWindowOnTop;
        set
        {
            if (userSettings.KeepWindowOnTop == value)
                return;
            userSettings.KeepWindowOnTop = value;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KeepWindowOnTop)));
        }
    }

    public bool TogglePttMode
    {
        get => userSettings.TogglePttMode;
        set
        {
            if (userSettings.TogglePttMode == value)
                return;
            userSettings.TogglePttMode = value;
            keyboardPtt.ToggleMode = value;
            if (globalKeyboardPtt is not null)
                globalKeyboardPtt.ToggleMode = value;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TogglePttMode)));
        }
    }

    public string GlobalPttKeyText => keyboardPtt.ActivationKey.ToString();

    public bool SerialPttEnabled
    {
        get => serialPttEnabled;
        set => SetField(ref serialPttEnabled, value);
    }

    public string SerialPttPortName
    {
        get => serialPttPortName;
        set => SetField(ref serialPttPortName, value?.Trim() ?? string.Empty);
    }

    public int SerialPttBaudRate
    {
        get => serialPttBaudRate;
        set => SetField(ref serialPttBaudRate, value);
    }

    public IReadOnlyList<string> SerialPttPortOptions => serialPttPortOptions;

    public IReadOnlyList<int> SerialPttBaudRates
        => SerialPttBaudRateOptions
            .Append(SerialPttBaudRate)
            .Where(baudRate => baudRate > 0)
            .Distinct()
            .Order()
            .ToArray();

    public string SerialPttStatusText
    {
        get => serialPttStatusText;
        private set => SetField(ref serialPttStatusText, value);
    }

    public void RefreshSerialPttDevices()
    {
        try
        {
            string[] devices = serialPortProvider()
                .Where(portName => !string.IsNullOrWhiteSpace(portName))
                .Select(portName => portName.Trim())
                .Append(SerialPttPortName)
                .Where(portName => portName.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(portName => portName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            serialPttPortOptions.Clear();
            foreach (string device in devices)
                serialPttPortOptions.Add(device);

            if (SerialPttPortName.Length == 0 && devices.Length > 0)
                SerialPttPortName = devices[0];
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SerialPttPortOptions)));
            SerialPttStatusText = serialPtt is not null && SerialPttEnabled
                ? $"Serial PTT configured for {SerialPttPortName} at {SerialPttBaudRate:N0} baud."
                : devices.Length == 0
                    ? "Serial PTT is disabled; no serial devices were detected."
                    : $"Serial PTT is disabled; detected {devices.Length} serial device(s).";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            serialPttPortOptions.Clear();
            if (SerialPttPortName.Length > 0)
                serialPttPortOptions.Add(SerialPttPortName);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SerialPttPortOptions)));
            SerialPttStatusText = $"Serial device discovery unavailable: {exception.Message}";
        }
    }

    public async Task<bool> ApplySerialPttSettingsAsync()
    {
        string portName = SerialPttPortName.Trim();
        int baudRate = SerialPttBaudRate;
        if (SerialPttEnabled && portName.Length == 0)
        {
            SerialPttStatusText = "Select a serial device before enabling hardware PTT.";
            return false;
        }
        if (baudRate is < 300 or > 4_000_000)
        {
            SerialPttStatusText = "Serial PTT baud rate must be between 300 and 4,000,000.";
            return false;
        }

        await serialPttChangeLock.WaitAsync();
        try
        {
            IPttSource? previous = serialPtt;
            serialPtt = null;
            if (previous is not null)
                await StopAndDisposeSerialPttAsync(previous);

            userSettings.SerialPttEnabled = SerialPttEnabled;
            userSettings.SerialPttPortName = portName;
            userSettings.SerialPttBaudRate = baudRate;
            PersistUserSettings();
            if (!SerialPttEnabled)
            {
                SerialPttStatusText = "Serial PTT is disabled.";
                TransmitStatusText = "PTT idle; serial hardware source disabled.";
                return true;
            }

            IPttSource? candidate = null;
            try
            {
                candidate = serialPttFactory(portName, baudRate);
                candidate.StateChanged += HandleKeyboardPttStateChanged;
                if (pttStarted)
                    await candidate.StartAsync();
                serialPtt = candidate;
                SerialPttStatusText = pttStarted
                    ? $"Serial PTT ready on {portName} at {baudRate:N0} baud."
                    : $"Serial PTT configured for {portName} at {baudRate:N0} baud.";
                TransmitStatusText = pttStarted
                    ? $"PTT idle; serial source {portName} ready."
                    : $"PTT idle; serial source {portName} will start with global PTT.";
                return true;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
            {
                if (candidate is not null)
                {
                    candidate.StateChanged -= HandleKeyboardPttStateChanged;
                    await candidate.DisposeAsync();
                }
                SerialPttStatusText = $"Serial PTT unavailable on {portName}: {exception.Message}";
                TransmitStatusText = $"PTT idle; serial source unavailable: {exception.Message}";
                return false;
            }
        }
        finally
        {
            serialPttChangeLock.Release();
        }
    }

    public bool RestoreSelectedChannelsOnStartup
    {
        get => userSettings.RestoreSelectedChannelsOnStartup;
        set
        {
            if (userSettings.RestoreSelectedChannelsOnStartup == value)
                return;
            userSettings.RestoreSelectedChannelsOnStartup = value;
            if (!value)
                userSettings.SelectedWebStreams.Clear();
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RestoreSelectedChannelsOnStartup)));
        }
    }

    public string DtmfPresetName
    {
        get => dtmfPresetName;
        set => SetField(ref dtmfPresetName, value ?? string.Empty);
    }

    public string TonePresetName
    {
        get => tonePresetName;
        set => SetField(ref tonePresetName, value ?? string.Empty);
    }

    public string QuickCallToneAText
    {
        get => quickCallToneAText;
        set => SetField(ref quickCallToneAText, value ?? string.Empty);
    }

    public string QuickCallToneBText
    {
        get => quickCallToneBText;
        set => SetField(ref quickCallToneBText, value ?? string.Empty);
    }

    public string AlertToneNameText
    {
        get => alertToneNameText;
        set => SetField(ref alertToneNameText, value ?? string.Empty);
    }

    public string RecordingRetentionDaysText
    {
        get => recordingRetentionDaysText;
        set => SetField(ref recordingRetentionDaysText, value ?? string.Empty);
    }

    public string RecordingRootPathText
    {
        get => recordingRootPathText;
        set => SetField(ref recordingRootPathText, value ?? string.Empty);
    }

    public string SelectionStatusText => selectedChannel is null
        ? $"Choose TX on one or more cards, then hold {GlobalPttKeyText}."
        : $"RX focus: {selectedChannel.Name}. Global PTT: {GlobalPttKeyText}.";

    public IReadOnlyList<SystemViewModel> Systems { get; }
    public IReadOnlyList<KeyStatusItemViewModel> KeyStatusItems
        => Systems
            .SelectMany(system => system.Channels)
            .Where(channel => channel.Definition.IsEncrypted)
            .Select(channel => KeyStatusItemViewModel.From(channel, p25KeyRing))
            .ToArray();
    public bool HasNoKeyStatusItems => KeyStatusItems.Count == 0;
    public IReadOnlyList<ZoneViewModel> Zones { get; }
    public IReadOnlyList<string> PatchGroupNames => patchForwarding.GroupNames;
    public IReadOnlyList<PatchGroupEditorViewModel> PatchGroups { get; }
    public ReadOnlyObservableCollection<DtmfPresetViewModel> DtmfPresets { get; }
    public ReadOnlyObservableCollection<TonePresetViewModel> TonePresets { get; }
    public ReadOnlyObservableCollection<AlertToneViewModel> AlertTones { get; }
    public ReadOnlyObservableCollection<BuiltInAlertToneViewModel> BuiltInAlertTones { get; }
    public ReadOnlyObservableCollection<ToolbarClockViewModel> ToolbarClocks { get; }
    public ReadOnlyObservableCollection<AudioInputPresetViewModel> AudioInputPresets { get; }
    public ReadOnlyObservableCollection<AudioDeviceOptionViewModel> AudioInputDevices { get; }
    public ReadOnlyObservableCollection<AudioDeviceOptionViewModel> AudioOutputDevices { get; }
    public ReadOnlyObservableCollection<SubscriberCommandAuditEntry> SubscriberCommandAudit { get; }
    public ReadOnlyObservableCollection<DebugLogEntry> DebugLogEntries { get; }
    public ReadOnlyObservableCollection<WebStreamViewModel> WebStreams { get; }
    public System.Collections.ObjectModel.ReadOnlyObservableCollection<CallHistoryEntry> CallHistory { get; }
    public IReadOnlyList<CallHistoryEntry> ActivityCallHistory
        => SelectedSystem is null
            ? []
            : CallHistory
                .Where(entry => entry.SystemName.Equals(SelectedSystem.Name, StringComparison.OrdinalIgnoreCase))
                .ToArray();
    public IReadOnlyList<SubscriberCommandAuditEntry> ActivitySubscriberCommandAudit
        => SelectedSystem is null
            ? []
            : SubscriberCommandAudit
                .Where(entry => entry.SystemName.Equals(SelectedSystem.Name, StringComparison.OrdinalIgnoreCase))
                .ToArray();
    public IReadOnlyList<CallHistoryEntry> FilteredCallHistory
        => CallHistory
            .Where(entry =>
            {
                if (string.IsNullOrWhiteSpace(CallHistoryFilterText))
                    return true;

                string filter = CallHistoryFilterText.Trim();
                return entry.SystemName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    entry.DisplayChannelText.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    entry.EventMessage.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    entry.CallerText.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    entry.DisplaySourceText.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    entry.DisplayDestinationText.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    entry.StreamId.ToString(CultureInfo.InvariantCulture).Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    entry.ProtocolText.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    entry.EncryptionText.Contains(filter, StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();
    public ReadOnlyObservableCollection<CallRecordingMetadata> Recordings { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand SendDtmfCommand { get; }
    public ICommand SendToneCommand { get; }
    public ICommand SaveDtmfPresetCommand { get; }
    public ICommand SaveTonePresetCommand { get; }
    public ICommand ApplyAudioInputSettingsCommand { get; }
    public ICommand ApplyRecordingRetentionCommand { get; }
    public ICommand RefreshAudioDevicesCommand { get; }
    public ICommand ConnectionCommand => SelectedSystem?.IsConnected == true ? DisconnectCommand : ConnectCommand;
    public string ConnectionButtonText => SelectedSystem?.IsConnected == true ? "Disconnect" : "Connect";
    public string ConnectionPillText => SelectedSystem?.IsConnected == true ? "CONNECTED" : "OFFLINE";
    public string SelectedSystemName => SelectedSystem?.Name ?? "No system";
    public string SystemStatusText => SelectedSystem?.ConnectionStatus ?? "No configured system";
    public IReadOnlyList<string> DebugLogSeverityFilters { get; } = ["All", "Debug", "Info", "Warning", "Error", "Fatal"];
    public IReadOnlyList<DebugLogEntry> FilteredDebugLogs
        => DebugLogEntries
            .Where(entry =>
                (DebugLogSeverityFilter == "All" || entry.Severity.ToString().Equals(DebugLogSeverityFilter, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(DebugLogFilterText) || entry.Summary.Contains(DebugLogFilterText, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

    public string DebugLogFilterText
    {
        get => debugLogFilterText;
        set
        {
            string normalized = value ?? string.Empty;
            if (debugLogFilterText == normalized)
                return;
            debugLogFilterText = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DebugLogFilterText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilteredDebugLogs)));
        }
    }

    public string DebugLogSeverityFilter
    {
        get => debugLogSeverityFilter;
        set
        {
            string normalized = string.IsNullOrWhiteSpace(value) ? "All" : value;
            if (debugLogSeverityFilter == normalized)
                return;
            debugLogSeverityFilter = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DebugLogSeverityFilter)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilteredDebugLogs)));
        }
    }

    public string CallHistoryFilterText
    {
        get => callHistoryFilterText;
        set
        {
            string normalized = value ?? string.Empty;
            if (callHistoryFilterText == normalized)
                return;
            callHistoryFilterText = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CallHistoryFilterText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilteredCallHistory)));
        }
    }

    public string RecordingFilterText
    {
        get => recordingFilterText;
        set
        {
            string normalized = value ?? string.Empty;
            if (recordingFilterText == normalized)
                return;
            recordingFilterText = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecordingFilterText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilteredRecordings)));
        }
    }

    public IReadOnlyList<string> RecordingDirectionFilters { get; } = ["All", "RX", "TX"];
    public IReadOnlyList<string> RecordingProtocolFilters { get; } = ["All", "DMR", "P25", "ANALOG", "NXDN"];
    public IReadOnlyList<string> RecordingEncryptionFilters { get; } = ["All", "Clear", "Encrypted"];

    public string RecordingDirectionFilter
    {
        get => recordingDirectionFilter;
        set => SetRecordingFilter(ref recordingDirectionFilter, value, nameof(RecordingDirectionFilter));
    }

    public string RecordingProtocolFilter
    {
        get => recordingProtocolFilter;
        set => SetRecordingFilter(ref recordingProtocolFilter, value, nameof(RecordingProtocolFilter));
    }

    public string RecordingEncryptionFilter
    {
        get => recordingEncryptionFilter;
        set => SetRecordingFilter(ref recordingEncryptionFilter, value, nameof(RecordingEncryptionFilter));
    }

    public string RecordingSystemFilterText
    {
        get => recordingSystemFilterText;
        set => SetRecordingFilter(ref recordingSystemFilterText, value, nameof(RecordingSystemFilterText), allowEmpty: true);
    }

    public string RecordingChannelFilterText
    {
        get => recordingChannelFilterText;
        set => SetRecordingFilter(ref recordingChannelFilterText, value, nameof(RecordingChannelFilterText), allowEmpty: true);
    }

    public string RecordingTalkgroupFilterText
    {
        get => recordingTalkgroupFilterText;
        set => SetRecordingFilter(ref recordingTalkgroupFilterText, value, nameof(RecordingTalkgroupFilterText), allowEmpty: true);
    }

    public string RecordingSubscriberFilterText
    {
        get => recordingSubscriberFilterText;
        set => SetRecordingFilter(ref recordingSubscriberFilterText, value, nameof(RecordingSubscriberFilterText), allowEmpty: true);
    }

    public string RecordingAliasFilterText
    {
        get => recordingAliasFilterText;
        set => SetRecordingFilter(ref recordingAliasFilterText, value, nameof(RecordingAliasFilterText), allowEmpty: true);
    }

    public DateTimeOffset? RecordingStartDateFilter
    {
        get => recordingStartDateFilter;
        set => SetRecordingDateFilter(ref recordingStartDateFilter, value, nameof(RecordingStartDateFilter));
    }

    public DateTimeOffset? RecordingEndDateFilter
    {
        get => recordingEndDateFilter;
        set => SetRecordingDateFilter(ref recordingEndDateFilter, value, nameof(RecordingEndDateFilter));
    }

    public bool ShowRecordingTimeColumn
    {
        get => recordingTimeColumnVisible;
        set => SetRecordingColumnVisibility(ref recordingTimeColumnVisible, value, nameof(ShowRecordingTimeColumn));
    }

    public bool ShowRecordingDurationColumn
    {
        get => recordingDurationColumnVisible;
        set => SetRecordingColumnVisibility(ref recordingDurationColumnVisible, value, nameof(ShowRecordingDurationColumn));
    }

    public bool ShowRecordingChannelColumn
    {
        get => recordingChannelColumnVisible;
        set => SetRecordingColumnVisibility(ref recordingChannelColumnVisible, value, nameof(ShowRecordingChannelColumn));
    }

    public bool ShowRecordingTalkgroupColumn
    {
        get => recordingTalkgroupColumnVisible;
        set => SetRecordingColumnVisibility(ref recordingTalkgroupColumnVisible, value, nameof(ShowRecordingTalkgroupColumn));
    }

    public bool ShowRecordingSourceIdColumn
    {
        get => recordingSourceIdColumnVisible;
        set => SetRecordingColumnVisibility(ref recordingSourceIdColumnVisible, value, nameof(ShowRecordingSourceIdColumn));
    }

    public bool ShowRecordingAliasColumn
    {
        get => recordingAliasColumnVisible;
        set => SetRecordingColumnVisibility(ref recordingAliasColumnVisible, value, nameof(ShowRecordingAliasColumn));
    }

    public bool ShowRecordingDirectionColumn
    {
        get => recordingDirectionColumnVisible;
        set => SetRecordingColumnVisibility(ref recordingDirectionColumnVisible, value, nameof(ShowRecordingDirectionColumn));
    }

    public bool ShowRecordingProtocolColumn
    {
        get => recordingProtocolColumnVisible;
        set => SetRecordingColumnVisibility(ref recordingProtocolColumnVisible, value, nameof(ShowRecordingProtocolColumn));
    }

    public bool ShowRecordingSystemColumn
    {
        get => recordingSystemColumnVisible;
        set => SetRecordingColumnVisibility(ref recordingSystemColumnVisible, value, nameof(ShowRecordingSystemColumn));
    }

    public bool ShowRecordingEncryptionColumn
    {
        get => recordingEncryptionColumnVisible;
        set => SetRecordingColumnVisibility(ref recordingEncryptionColumnVisible, value, nameof(ShowRecordingEncryptionColumn));
    }

    public bool ShowRecordingDiagnosticsColumn
    {
        get => recordingDiagnosticsColumnVisible;
        set => SetRecordingColumnVisibility(ref recordingDiagnosticsColumnVisible, value, nameof(ShowRecordingDiagnosticsColumn));
    }

    public void ResetRecordingColumns()
    {
        ShowRecordingTimeColumn = true;
        ShowRecordingDurationColumn = true;
        ShowRecordingChannelColumn = true;
        ShowRecordingTalkgroupColumn = true;
        ShowRecordingSourceIdColumn = true;
        ShowRecordingAliasColumn = true;
        ShowRecordingDirectionColumn = false;
        ShowRecordingProtocolColumn = false;
        ShowRecordingSystemColumn = false;
        ShowRecordingEncryptionColumn = false;
        ShowRecordingDiagnosticsColumn = true;
    }

    public void ClearRecordingFilters()
    {
        RecordingFilterText = string.Empty;
        RecordingDirectionFilter = "All";
        RecordingProtocolFilter = "All";
        RecordingEncryptionFilter = "All";
        RecordingSystemFilterText = string.Empty;
        RecordingChannelFilterText = string.Empty;
        RecordingTalkgroupFilterText = string.Empty;
        RecordingSubscriberFilterText = string.Empty;
        RecordingAliasFilterText = string.Empty;
        RecordingStartDateFilter = null;
        RecordingEndDateFilter = null;
    }

    public bool ApplyRecordingRoot()
    {
        if (!callRecordings.TrySetRootPath(RecordingRootPathText, out string errorMessage))
        {
            RecordingRootPathText = callRecordings.RootPath;
            AudioStatusText = $"TAR storage unchanged: {errorMessage}";
            return false;
        }

        userSettings.RecordingRootPath = callRecordings.RootPath;
        PersistUserSettings();
        callRecordings.PruneExpired();
        RefreshRecordings();
        RecordingRootPathText = callRecordings.RootPath;
        AudioStatusText = $"TAR recordings now use {callRecordings.RootPath}.";
        return true;
    }

    public IReadOnlyList<CallRecordingMetadata> FilteredRecordings
        => Recordings
            .Where(metadata => new RecordingCatalogFilter(
                RecordingFilterText,
                RecordingDirectionFilter,
                RecordingProtocolFilter,
                RecordingEncryptionFilter,
                RecordingSystemFilterText,
                RecordingChannelFilterText,
                RecordingTalkgroupFilterText,
                RecordingSubscriberFilterText,
                RecordingAliasFilterText,
                RecordingStartDateFilter,
                RecordingEndDateFilter).Matches(metadata))
            .ToArray();

    private void SetRecordingDateFilter(
        ref DateTimeOffset? field,
        DateTimeOffset? value,
        string propertyName)
    {
        DateTimeOffset? normalized = value is DateTimeOffset date
            ? new DateTimeOffset(date.Date, date.Offset)
            : null;
        if (field == normalized)
            return;
        field = normalized;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilteredRecordings)));
    }

    private void SetRecordingColumnVisibility(ref bool field, bool value, string propertyName)
    {
        if (field == value)
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void SetRecordingFilter(
        ref string field,
        string? value,
        string propertyName,
        bool allowEmpty = false)
    {
        string normalized = string.IsNullOrWhiteSpace(value)
            ? (allowEmpty ? string.Empty : "All")
            : value.Trim();
        if (field.Equals(normalized, StringComparison.Ordinal))
            return;
        field = normalized;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilteredRecordings)));
    }

    public void ClearDebugLogs()
    {
        debugLogEntries.Clear();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilteredDebugLogs)));
        StatusText = "Debug log capture cleared.";
    }

    public void ExportDebugLogs(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var lines = new List<string> { "Timestamp\tSeverity\tSource\tMessage" };
        lines.AddRange(DebugLogEntries.Select(entry => string.Join("\t",
            entry.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            entry.SeverityText,
            entry.Source,
            DebugLogRedactor.Redact(entry.Message).Replace("\r", " ").Replace("\n", " "))));
        File.WriteAllLines(fullPath, lines);
        StatusText = $"Exported {DebugLogEntries.Count} redacted debug log entr{(DebugLogEntries.Count == 1 ? "y" : "ies")}.";
    }
    public IBrush ConnectionBrush => SelectedSystem?.IsConnected == true
        ? new SolidColorBrush(Color.Parse("#00C86A"))
        : new SolidColorBrush(Color.Parse("#7B8794"));

    public bool TrySendSubscriberCommand(
        SystemViewModel system,
        P25SubscriberCommand command,
        string? destinationText,
        out string message)
    {
        ArgumentNullException.ThrowIfNull(system);

        if (!P25SubscriberCommandCodec.TryParseSubscriberId(destinationText, out uint destinationId))
        {
            message = "Enter a P25 subscriber RID from 1 to 16777215.";
            RecordSubscriberCommandAudit(system.Name, command, 0, false, message);
            StatusText = message;
            return false;
        }

        if (!system.IsConnected)
        {
            message = $"{system.Name} is not connected to an FNE.";
            RecordSubscriberCommandAudit(system.Name, command, destinationId, false, message);
            StatusText = message;
            return false;
        }

        if (system.SourceId is not uint sourceId || !P25SubscriberCommandCodec.IsValidSubscriberId(sourceId))
        {
            message = $"{system.Name} does not have a configured source RID.";
            RecordSubscriberCommandAudit(system.Name, command, destinationId, false, message);
            StatusText = message;
            return false;
        }

        try
        {
            system.SendP25SubscriberCommand(command, destinationId);
            message = "Sent; acknowledgement decoding is pending.";
            RecordSubscriberCommandAudit(system.Name, command, destinationId, true, message);
            StatusText = $"{system.Name}: {CommandName(command)} to RID {destinationId} sent.";
            return true;
        }
        catch (Exception exception)
        {
            message = $"Unable to send command: {exception.Message}";
            RecordSubscriberCommandAudit(system.Name, command, destinationId, false, message);
            StatusText = $"{system.Name}: {message}";
            return false;
        }
    }

    public bool RetainPatchStateOnStartup
    {
        get => userSettings.RetainPatchStateOnStartup;
        set
        {
            if (userSettings.RetainPatchStateOnStartup == value)
                return;
            userSettings.RetainPatchStateOnStartup = value;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RetainPatchStateOnStartup)));
        }
    }

    public void OpenRecording(CallRecordingMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (!callRecordings.TryGetRecordingPath(metadata, out string recordingPath))
        {
            AudioStatusText = "The selected recording file is no longer available.";
            RefreshRecordings();
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = recordingPath,
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            AudioStatusText = $"Unable to open recording: {exception.Message}";
        }
    }

    public async Task PlayRecordingAsync(CallRecordingMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (!callRecordings.TryGetRecordingPath(metadata, out string recordingPath))
        {
            AudioStatusText = "The selected recording file is no longer available.";
            RefreshRecordings();
            return;
        }

        try
        {
            await recordingPlayback.StartAsync(recordingPath).ConfigureAwait(false);
            AudioStatusText = $"Playing recording: {metadata.FileName}";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or NotSupportedException)
        {
            AudioStatusText = $"Unable to play recording: {exception.Message}";
        }
    }

    public async Task PlayCallHistoryRecordingAsync(CallHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Recording is not CallRecordingMetadata metadata)
        {
            AudioStatusText = "No TAR recording is available for this event.";
            return;
        }

        await PlayRecordingAsync(metadata).ConfigureAwait(false);
    }

    public async Task StopRecordingPlaybackAsync()
    {
        await recordingPlayback.StopAsync().ConfigureAwait(false);
        AudioStatusText = "Recording playback stopped.";
    }

    public async Task DeleteRecordingAsync(CallRecordingMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (callRecordings.TryGetRecordingPath(metadata, out string recordingPath) &&
            recordingPlayback.IsPlaying(recordingPath))
        {
            await recordingPlayback.StopAsync().ConfigureAwait(false);
        }

        if (!callRecordings.DeleteRecording(metadata))
        {
            AudioStatusText = "The selected recording could not be deleted.";
            RefreshRecordings();
            return;
        }

        AudioStatusText = $"Deleted recording: {metadata.FileName}";
        RefreshRecordings();
    }

    public void SetRecordingIgnoredSubscribers(ChannelViewModel channel, IEnumerable<uint> subscriberIds)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(subscriberIds);
        List<uint> normalized = subscriberIds
            .Where(subscriberId => subscriberId != 0)
            .Distinct()
            .OrderBy(subscriberId => subscriberId)
            .ToList();
        userSettings.RecordingIgnoredSubscriberIds[channel.SettingsKey] = normalized;
        channel.SetIgnoredSubscriberIds(normalized);
        PersistUserSettings();
    }

    public bool TrySaveRecordingIgnoredSubscribers(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        List<uint> subscriberIds = [];
        foreach (string token in channel.IgnoredSubscriberIdsText.Split(
                     [',', ';', ' ', '\t', '\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!uint.TryParse(token, out uint subscriberId) || subscriberId == 0)
            {
                AudioStatusText = $"Ignored subscriber IDs must be positive integers: '{token}'.";
                return false;
            }

            subscriberIds.Add(subscriberId);
        }

        SetRecordingIgnoredSubscribers(channel, subscriberIds);
        AudioStatusText = subscriberIds.Count == 0
            ? $"Recording ignores cleared for {channel.Name}."
            : $"Recording ignores {subscriberIds.Distinct().Count()} subscriber ID(s) on {channel.Name}.";
        return true;
    }

    public ChannelViewModel? SelectedChannel => selectedChannel;

    public SystemViewModel? SelectedSystem
    {
        get => selectedSystem;
        set
        {
            if (ReferenceEquals(selectedSystem, value))
                return;

            selectedSystem = value;
            foreach (SystemViewModel system in Systems)
                system.SetSelected(ReferenceEquals(system, selectedSystem));
            userSettings.LastSelectedSystemName = value?.Name;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSystem)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActivityCallHistory)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActivitySubscriberCommandAudit)));
            NotifyConnectionPresentationChanged();
            RaiseGeneratedAudioCanExecuteChanged();
        }
    }

    public async ValueTask StartKeyboardPttAsync(CancellationToken cancellationToken = default)
    {
        if (!pttStarted)
        {
            await StartKeyboardPttSourceAsync(cancellationToken).ConfigureAwait(false);
            pttStarted = true;
        }

        await serialPttChangeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (serialPtt is null)
                return;

            try
            {
                await serialPtt.StartAsync(cancellationToken).ConfigureAwait(false);
                SerialPttStatusText = $"Serial PTT ready on {SerialPttPortName} at {SerialPttBaudRate:N0} baud.";
                TransmitStatusText = $"PTT idle; serial source {SerialPttPortName} ready.";
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
            {
                SerialPttStatusText = $"Serial PTT unavailable on {SerialPttPortName}: {exception.Message}";
                TransmitStatusText = $"PTT idle; serial source unavailable: {exception.Message}";
            }
        }
        finally
        {
            serialPttChangeLock.Release();
        }
    }

    private async Task StopAndDisposeSerialPttAsync(IPttSource source)
    {
        try
        {
            await source.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            source.StateChanged -= HandleKeyboardPttStateChanged;
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask StartKeyboardPttSourceAsync(CancellationToken cancellationToken)
    {
        if (GlobalKeyboardPttSource.IsPlatformSupported)
        {
            var candidate = new GlobalKeyboardPttSource(keyboardPtt.ActivationKey)
            {
                ToggleMode = userSettings.TogglePttMode
            };
            candidate.StateChanged += HandleKeyboardPttStateChanged;
            try
            {
                await candidate.StartAsync(cancellationToken).ConfigureAwait(false);
                globalKeyboardPtt = candidate;
                TransmitStatusText = $"PTT idle; OS-global {GlobalPttKeyText} ready.";
                return;
            }
            catch (Exception exception) when (exception is PlatformNotSupportedException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                candidate.StateChanged -= HandleKeyboardPttStateChanged;
                await candidate.DisposeAsync().ConfigureAwait(false);
                TransmitStatusText = $"OS-global PTT unavailable; using window keyboard fallback: {exception.Message}";
            }
        }

        await keyboardPtt.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public void SelectChannel(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (AnyPttSourcePressed && selectedChannel is not null && !ReferenceEquals(selectedChannel, channel))
            return;
        if (ReferenceEquals(selectedChannel, channel))
            return;

        selectedChannel = channel;
        selectedSystem = Systems.FirstOrDefault(system => system.Channels.Contains(channel)) ?? selectedSystem;
        userSettings.LastSelectedSystemName = selectedSystem?.Name;
        userSettings.LastSelectedChannelKey = channel.SettingsKey;
        PersistUserSettings();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedChannel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionStatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSystem)));
        RaiseGeneratedAudioCanExecuteChanged();
    }

    public void ToggleChannelTransmitSelection(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!channel.CanTransmit)
        {
            TransmitStatusText = $"{channel.Name} cannot be selected for TX.";
            return;
        }

        channel.SetTransmitSelected(!channel.IsTransmitSelected);
        userSettings.TransmitSelectedChannelKeys = Systems
            .SelectMany(system => system.Channels)
            .Where(candidate => candidate.IsTransmitSelected)
            .Select(candidate => candidate.SettingsKey)
            .ToList();
        PersistUserSettings();
        TransmitStatusText = channel.IsTransmitSelected
            ? $"{channel.Name} selected for global TX."
            : $"{channel.Name} removed from global TX.";
    }

    public void ToggleAllTransmitSelection()
    {
        ChannelViewModel[] candidates = (SelectedSystem?.Channels ?? Systems.SelectMany(system => system.Channels))
            .Where(channel => channel.CanTransmit)
            .ToArray();
        if (candidates.Length == 0)
        {
            TransmitStatusText = "No transmit-capable channels are available in the selected system.";
            return;
        }

        bool select = candidates.Any(channel => !channel.IsTransmitSelected);
        foreach (ChannelViewModel channel in candidates)
            channel.SetTransmitSelected(select);

        userSettings.TransmitSelectedChannelKeys = Systems
            .SelectMany(system => system.Channels)
            .Where(channel => channel.IsTransmitSelected)
            .Select(channel => channel.SettingsKey)
            .ToList();
        PersistUserSettings();
        TransmitStatusText = select
            ? $"Selected {candidates.Length} transmit-capable channel(s) for global TX."
            : "Cleared global TX selection.";
    }

    public void ToggleChannelPageSelection(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!channel.CanTransmit)
        {
            TransmitStatusText = $"{channel.Name} cannot be selected for paging.";
            return;
        }

        channel.SetPageSelected(!channel.IsPageSelected);
        TransmitStatusText = channel.IsPageSelected
            ? $"{channel.Name} armed for QCII paging."
            : $"{channel.Name} removed from QCII paging.";
    }

    public void ToggleChannelAlertSelection(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!channel.CanTransmit)
        {
            TransmitStatusText = $"{channel.Name} cannot be selected for alerts.";
            return;
        }

        channel.SetAlertSelected(!channel.IsAlertSelected);
        RaiseGeneratedAudioCanExecuteChanged();
        TransmitStatusText = channel.IsAlertSelected
            ? $"{channel.Name} armed for DTMF and alert tones."
            : $"{channel.Name} removed from alert-tone targeting.";
    }

    public async Task SetGlobalPttKeyAsync(KeyboardPttKey key)
    {
        if (keyboardPtt.ActivationKey == key &&
            (globalKeyboardPtt is null || globalKeyboardPtt.ActivationKey == key))
            return;
        if (AnyPttSourcePressed)
            await HandleKeyboardPttStateChangedAsync(false).ConfigureAwait(false);

        keyboardPtt.StateChanged -= HandleKeyboardPttStateChanged;
        await keyboardPtt.DisposeAsync().ConfigureAwait(false);
        if (globalKeyboardPtt is not null)
        {
            globalKeyboardPtt.StateChanged -= HandleKeyboardPttStateChanged;
            await globalKeyboardPtt.DisposeAsync().ConfigureAwait(false);
            globalKeyboardPtt = null;
        }

        keyboardPtt = new KeyboardPttSource(key) { ToggleMode = userSettings.TogglePttMode };
        keyboardPtt.StateChanged += HandleKeyboardPttStateChanged;
        if (pttStarted)
            await StartKeyboardPttSourceAsync(CancellationToken.None).ConfigureAwait(false);
        userSettings.GlobalPttKey = key.ToString();
        PersistUserSettings();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GlobalPttKeyText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionStatusText)));
        TransmitStatusText = $"Global PTT key set to {key}.";
    }

    public async Task ToggleChannelReceiveAsync(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        SelectChannel(channel);
        if (channel.IsAudioEnabled)
            await StopAudioAsync(channel).ConfigureAwait(false);
        else
            await StartAudioAsync(channel).ConfigureAwait(false);
    }

    public async Task DisableAllReceiveAsync()
    {
        ChannelViewModel[] activeChannels = Systems
            .SelectMany(system => system.Channels)
            .Where(channel => channel.IsAudioEnabled)
            .ToArray();
        foreach (ChannelViewModel channel in activeChannels)
            await StopAudioAsync(channel).ConfigureAwait(false);
    }

    public async Task StartChannelTransmitAsync(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        SelectChannel(channel);
        if (channel.IsTransmitting)
            return;
        if (!channel.CanTransmit)
        {
            TransmitStatusText = $"PTT unavailable for {channel.Name}: the channel is RX-only or its encryption key is unavailable.";
            return;
        }
        await StartTransmitAsync(channel).ConfigureAwait(false);
    }

    public async Task StopChannelTransmitAsync(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!channel.IsTransmitting)
            return;
        await StopTransmitAsync(channel).ConfigureAwait(false);
    }

    public bool HandleKeyboardPttDown(KeyboardPttKey key)
    {
        return keyboardPtt.HandleKeyDown(key);
    }

    public bool HandleKeyboardPttUp(KeyboardPttKey key)
    {
        return keyboardPtt.HandleKeyUp(key);
    }

    public bool IsConfiguredPttKey(KeyboardPttKey key) => keyboardPtt.ActivationKey == key;

    public static MainWindowViewModel Load(string? configurationPath)
        => Load(configurationPath, new UserSettingsStore(UserSettingsStore.DefaultPath));

    internal static MainWindowViewModel Load(
        string? configurationPath,
        UserSettingsStore userSettingsStore,
        Func<IReadOnlyList<string>>? serialPortProvider = null,
        Func<string, int, IPttSource>? serialPttFactory = null)
    {
        ArgumentNullException.ThrowIfNull(userSettingsStore);
        if (string.IsNullOrWhiteSpace(configurationPath))
            configurationPath = userSettingsStore.Load().LastCodeplugPath;

        if (string.IsNullOrWhiteSpace(configurationPath))
        {
            return new MainWindowViewModel(
                "No codeplug selected. Launch with a path to a codeplug YAML file.",
                [],
                [],
                userSettingsStore: userSettingsStore,
                groupDefinitions: [],
                serialPortProvider: serialPortProvider,
                serialPttFactory: serialPttFactory);
        }

        try
        {
            ConsoleConfiguration configuration = ConfigurationLoader.Load(configurationPath);
            IReadOnlyList<string> errors = ConfigurationLoader.Validate(configuration);
            P25KeyRing p25KeyRing = LoadP25KeyRing(configuration, out string? keyWarning);
            IReadOnlyList<ZoneViewModel> zones = configuration.Zones.Select(zone => new ZoneViewModel(
                zone.Name,
                zone.Channels.Select(channel => new ChannelViewModel(
                    channel,
                    p25KeyRing,
                    configuration.Systems
                        .FirstOrDefault(system => system.Name.Equals(channel.System, StringComparison.OrdinalIgnoreCase))
                        ?.RidAlias)).ToArray(),
                zone.WebStreams.Select(stream => new WebStreamViewModel(stream)).ToArray(),
                zone.TabColor,
                zone.TabTextColor)).ToArray();
            string status = errors.Count == 0
                ? $"Loaded {configuration.Systems.Count} system(s) and {configuration.Zones.Count} zone(s). Connections are idle until Connect is pressed."
                : $"Configuration has {errors.Count} validation error(s):\n• {string.Join("\n• ", errors)}";
            if (!string.IsNullOrWhiteSpace(keyWarning))
                status = $"{status}\n{keyWarning}";

            var viewModel = new MainWindowViewModel(
                status,
                errors.Count == 0
                    ? CreateSystemViewModels(configuration, zones)
                    : [],
                zones,
                p25KeyRing,
                userSettingsStore,
                configuration.EffectiveGroups(),
                configuration.PatchSourceIdPassthrough,
                serialPortProvider,
                serialPttFactory);
            if (errors.Count == 0)
                viewModel.RecordLoadedCodeplug(configuration.SourcePath ?? Path.GetFullPath(configurationPath));
            return viewModel;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException or YamlDotNet.Core.YamlException)
        {
            return new MainWindowViewModel(
                $"Unable to load codeplug: {exception.Message}",
                [],
                [],
                userSettingsStore: userSettingsStore,
                groupDefinitions: [],
                serialPortProvider: serialPortProvider,
                serialPttFactory: serialPttFactory);
        }
    }

    private static P25KeyRing LoadP25KeyRing(
        ConsoleConfiguration configuration,
        out string? warning)
    {
        warning = null;
        if (string.IsNullOrWhiteSpace(configuration.KeyFile))
            return new P25KeyRing(new KeyContainer());

        try
        {
            return new P25KeyRing(KeyFileLoader.Load(
                ConfigurationLoader.ResolvePath(configuration, configuration.KeyFile)));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException or YamlDotNet.Core.YamlException)
        {
            warning = $"Encryption keys unavailable: {exception.Message} Encrypted P25 channels are disabled.";
            return new P25KeyRing(new KeyContainer());
        }
    }

    private static IReadOnlyList<SystemViewModel> CreateSystemViewModels(
        ConsoleConfiguration configuration,
        IReadOnlyList<ZoneViewModel> zones)
    {
        var channelsBySystem = new Dictionary<string, List<ChannelViewModel>>(StringComparer.OrdinalIgnoreCase);
        foreach (ChannelViewModel channel in zones.SelectMany(zone => zone.Channels))
        {
            if (!channelsBySystem.TryGetValue(channel.Definition.SystemName, out List<ChannelViewModel>? channels))
            {
                channels = [];
                channelsBySystem.Add(channel.Definition.SystemName, channels);
            }

            channels.Add(channel);
        }

        return configuration.Systems.Select(system =>
        {
            IReadOnlyList<ZoneViewModel> systemZones = zones
                .Select(zone => new ZoneViewModel(
                    zone.Name,
                    zone.Channels.Where(channel => channel.Definition.SystemName.Equals(
                        system.Name,
                        StringComparison.OrdinalIgnoreCase)).ToArray(),
                    zone.WebStreams,
                    zone.TabColor,
                    zone.TabTextColor))
                .Where(zone => zone.Channels.Count > 0)
                .ToArray();

            return new SystemViewModel(
                FneConnectionOptions.FromConfiguration(system),
                system.Name,
                $"{system.Address}:{system.Port}",
                channelsBySystem.TryGetValue(system.Name, out List<ChannelViewModel>? channels)
                    ? channels
                    : [],
                systemZones);
        }).ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
            return;

        clockTimer.Stop();
        clockTimer.Tick -= HandleClockTick;
        transmitCoordinator.Faulted -= HandleTransmitFaulted;
        keyboardPtt.StateChanged -= HandleKeyboardPttStateChanged;
        if (globalKeyboardPtt is not null)
            globalKeyboardPtt.StateChanged -= HandleKeyboardPttStateChanged;
        await keyboardPtt.DisposeAsync().ConfigureAwait(false);
        if (globalKeyboardPtt is not null)
            await globalKeyboardPtt.DisposeAsync().ConfigureAwait(false);
        await serialPttChangeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            IPttSource? currentSerialPtt = serialPtt;
            serialPtt = null;
            if (currentSerialPtt is not null)
                await StopAndDisposeSerialPttAsync(currentSerialPtt).ConfigureAwait(false);
        }
        finally
        {
            serialPttChangeLock.Release();
        }
        await toneTransmitCoordinator.DisposeAsync().ConfigureAwait(false);
        await talkPermitTonePlayer.DisposeAsync().ConfigureAwait(false);
        await transmitCoordinator.DisposeAsync().ConfigureAwait(false);
        foreach (SystemViewModel system in Systems)
        {
            system.KeyResponseReceived -= HandleSystemKeyResponse;
            system.LogReceived -= HandleSystemLog;
            await system.DisposeAsync().ConfigureAwait(false);
        }
        await DrainFrameWorkAsync().ConfigureAwait(false);
        await patchSourceDecode.DisposeAsync().ConfigureAwait(false);
        patchForwarding.Dispose();
        await audioCoordinator.DisposeAsync().ConfigureAwait(false);
        await webStreamPlayback.DisposeAsync().ConfigureAwait(false);
        await recordingPlayback.DisposeAsync().ConfigureAwait(false);
        callRecordings.Dispose();
        userBackgroundBitmap?.Dispose();
        userBackgroundBitmap = null;
        foreach (ChannelViewModel channel in Systems.SelectMany(system => system.Channels))
        {
            channel.TransmitEncryptionChanged -= HandleChannelEncryptionChanged;
            channel.RecordingStateChanged -= HandleChannelRecordingChanged;
            channel.VolumeChanged -= HandleChannelVolumeChanged;
        }
        foreach (WebStreamViewModel stream in WebStreams)
        {
            stream.VolumeChanged -= HandleWebStreamVolumeChanged;
            stream.PropertyChanged -= HandleWebStreamPropertyChanged;
        }
    }

    private async Task ConnectAsync()
    {
        SetBusy(true);
        StatusText = "Starting FNE connection services...";
        try
        {
            await Task.WhenAll(Systems.Select(system => StartSystemAsync(system)));
            await SyncPatchSourceDecodeAsync().ConfigureAwait(false);
            StatusText = "FNE connection services started; waiting for login acknowledgements.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task StartSystemAsync(SystemViewModel system)
    {
        try
        {
            await system.StartAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            HandleSystemStatus(system, new FneConnectionStatus(
                system.Name,
                FneConnectionState.Faulted,
                exception.Message,
                DateTimeOffset.UtcNow));
        }
    }

    public async Task ToggleSystemConnectionAsync(SystemViewModel system)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (!Systems.Contains(system))
            throw new ArgumentException("The FNE is not part of this console.", nameof(system));

        SelectedSystem = system;
        if (system.IsConnectionActive)
        {
            StatusText = $"Stopping {system.Name}...";
            try
            {
                await system.StopAsync();
                StatusText = $"{system.Name}: disconnected.";
            }
            catch (Exception exception)
            {
                StatusText = $"{system.Name}: disconnect failed — {exception.Message}";
            }
            return;
        }

        StatusText = $"Starting {system.Name}...";
        await StartSystemAsync(system);
        await SyncPatchSourceDecodeAsync();
    }

    private async Task DisconnectAsync()
    {
        SetBusy(true);
        StatusText = "Stopping FNE connection services...";
        try
        {
            await patchSourceDecode.StopAllAsync().ConfigureAwait(false);
            patchForwarding.StopAll();
            await Task.WhenAll(Systems.Select(system => system.StopAsync()));
            StatusText = "FNE connections stopped.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void HandleSystemStatus(SystemViewModel system, FneConnectionStatus status)
    {
        void Apply()
        {
            system.ApplyStatus(status);
            StatusText = $"{system.Name}: {status.State} — {status.Message}";
            NotifyConnectionPresentationChanged();
            if (status.State == FneConnectionState.Connected)
                RequestMissingP25Keys(system);
            bool stateChanged = !lastConnectionStates.TryGetValue(system.Name, out FneConnectionState previousState) ||
                previousState != status.State;
            lastConnectionStates[system.Name] = status.State;
            if (stateChanged && status.State is FneConnectionState.Connected or FneConnectionState.Disconnected or FneConnectionState.Faulted)
            {
                string stateText = status.State.ToString().ToLowerInvariant();
                AddEventHistory(
                    "FNE",
                    $"{system.Name} {stateText}",
                    system.SourceId?.ToString(CultureInfo.InvariantCulture),
                    system.Endpoint);
            }
            bool shouldPlayChime = connectionChimeTracker.ShouldPlay(system.Name, status.State);
            if (stateChanged && shouldPlayChime)
                _ = PlayConnectionChimeAsync(system.Name, status.State);
            RaiseGeneratedAudioCanExecuteChanged();
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
    }

    private async Task PlayConnectionChimeAsync(string systemName, FneConnectionState state)
    {
        if (!ConnectionChimes)
            return;

        try
        {
            await talkPermitTonePlayer.PlayAsync(
                frequency: state == FneConnectionState.Connected ? 1500 : 500,
                duration: state == FneConnectionState.Connected
                    ? TimeSpan.FromMilliseconds(80)
                    : TimeSpan.FromMilliseconds(160),
                amplitude: 0.25).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Dispatcher.UIThread.Post(() =>
                AudioStatusText = $"{systemName} connection chime unavailable: {exception.Message}");
        }
    }

    private void HandleSystemLog(object? sender, FneLogEntry entry)
    {
        void Apply()
        {
            if (debugLogEntries.Count >= 500)
                debugLogEntries.RemoveAt(debugLogEntries.Count - 1);

            debugLogEntries.Insert(0, new DebugLogEntry(
                entry.Timestamp,
                entry.SystemName,
                entry.Severity,
                entry.Message));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilteredDebugLogs)));
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
    }

    private void RequestMissingP25Keys(SystemViewModel system)
    {
        if (p25KeyRing is null)
            return;

        foreach (ChannelViewModel channel in system.Channels)
        {
            if (channel.Definition.Mode != "p25" || !channel.Definition.IsEncrypted ||
                !P25KeyRing.TryParseAlgorithmId(channel.Definition.EncryptionAlgorithm, out byte algorithmId) ||
                !P25KeyRing.TryParseKeyId(channel.Definition.EncryptionKeyId, out ushort keyId) ||
                p25KeyRing.CanResolve(channel.Definition.EncryptionAlgorithm, channel.Definition.EncryptionKeyId))
            {
                continue;
            }

            try
            {
                system.RequestP25Key(algorithmId, keyId);
            }
            catch (Exception exception)
            {
                StatusText = $"{system.Name}: P25 key request unavailable — {exception.Message}";
            }
        }
    }

    private void HandleSystemKeyResponse(object? sender, FneKeyResponse response)
    {
        if (sender is not SystemViewModel system || p25KeyRing is null)
            return;

        void Apply()
        {
            p25KeyRing.AddOrReplace(response.AlgorithmId, response.KeyId, response.KeyMaterial.Span);
            foreach (ChannelViewModel channel in Systems.SelectMany(candidate => candidate.Channels))
                channel.RefreshEncryptionState();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KeyStatusItems)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasNoKeyStatusItems)));
            StatusText = $"{system.Name}: P25 key material received.";
            _ = SyncPatchSourceDecodeAsync();
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
    }

    private void HandleChannelEncryptionChanged(object? sender, bool encrypted)
    {
        if (sender is not ChannelViewModel channel)
            return;

        userSettings.TransmitEncryptionStates[channel.SettingsKey] = encrypted;
        PersistUserSettings();
    }

    private void HandleChannelRecordingChanged(object? sender, bool enabled)
    {
        if (sender is not ChannelViewModel channel)
            return;

        userSettings.RecordingEnabledChannelKeys.RemoveAll(
            key => key.Equals(channel.SettingsKey, StringComparison.OrdinalIgnoreCase));
        if (enabled)
            userSettings.RecordingEnabledChannelKeys.Add(channel.SettingsKey);
        PersistUserSettings();

        if (!enabled)
        {
            callRecordings.StopChannel(channel);
            RefreshRecordings();
            return;
        }

        _ = EnsureRecordingAudioAsync(channel);
    }

    private void HandleChannelVolumeChanged(object? sender, double volume)
    {
        if (sender is not ChannelViewModel channel)
            return;

        userSettings.ChannelVolumes[channel.SettingsKey] = volume;
        PersistUserSettings();
        _ = audioCoordinator.SetGainAsync(channel, volume);
    }

    private async Task StartWebStreamAsync(WebStreamViewModel stream)
    {
        try
        {
            await webStreamPlayback.StartAsync(stream).ConfigureAwait(false);
            PersistSelectedWebStreamState(stream);
            AudioStatusText = stream.IsFailed
                ? $"Web stream {stream.Name}: {stream.StatusText}"
                : $"Web stream {stream.Name}: {stream.StatusText}";
        }
        catch (OperationCanceledException)
        {
            stream.SetPlaybackState(false, false, false, false, "Off");
        }
        catch (Exception exception)
        {
            stream.SetPlaybackState(false, false, false, true, $"Failed: {exception.Message}");
            AudioStatusText = $"Web stream {stream.Name}: {stream.StatusText}";
        }
    }

    private async Task StopWebStreamAsync(WebStreamViewModel stream)
    {
        try
        {
            await webStreamPlayback.StopAsync(stream).ConfigureAwait(false);
            PersistSelectedWebStreamState(stream);
            AudioStatusText = $"Web stream {stream.Name}: Off";
        }
        catch (OperationCanceledException)
        {
            stream.SetPlaybackState(false, false, false, false, "Off");
        }
        catch (Exception exception)
        {
            stream.SetPlaybackState(false, false, false, true, $"Failed to stop: {exception.Message}");
            AudioStatusText = $"Web stream {stream.Name}: {stream.StatusText}";
        }
    }

    private void HandleWebStreamVolumeChanged(object? sender, double volume)
    {
        if (sender is not WebStreamViewModel stream)
            return;

        userSettings.WebStreamVolumes[stream.Name] = volume;
        webStreamPlayback.SetVolume(stream, volume);
        PersistUserSettings();
    }

    private void HandleWebStreamPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(WebStreamViewModel.IsActive) && sender is WebStreamViewModel stream)
            PersistSelectedWebStreamState(stream);
    }

    private async Task RestoreSelectedWebStreamsAsync()
    {
        if (!userSettings.RestoreSelectedChannelsOnStartup || userSettings.SelectedWebStreams.Count == 0)
            return;

        HashSet<string> selectedNames = userSettings.SelectedWebStreams
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (WebStreamViewModel stream in webStreams.Where(stream => selectedNames.Contains(stream.Name)))
            await StartWebStreamAsync(stream).ConfigureAwait(false);
    }

    private void PersistSelectedWebStreamState(WebStreamViewModel stream)
    {
        if (!userSettings.RestoreSelectedChannelsOnStartup)
            return;

        HashSet<string> selectedNames = userSettings.SelectedWebStreams
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (stream.IsActive && !stream.IsFailed)
            selectedNames.Add(stream.Name);
        else
            selectedNames.Remove(stream.Name);
        userSettings.SelectedWebStreams = selectedNames.ToList();
        PersistUserSettings();
    }

    public bool SaveWebStreamOutputDevice(WebStreamViewModel stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        string deviceId = stream.OutputDeviceIdText.Trim();
        if (deviceId.Length > 256)
        {
            AudioStatusText = "Output device IDs must be 256 characters or fewer.";
            return false;
        }

        if (deviceId.Length == 0 || deviceId.Equals("default", StringComparison.OrdinalIgnoreCase))
            userSettings.WebStreamOutputDeviceIds.Remove(stream.Name);
        else
            userSettings.WebStreamOutputDeviceIds[stream.Name] = deviceId;

        PersistUserSettings();
        stream.RestoreOutputDeviceId(deviceId);
        AudioStatusText = stream.IsActive
            ? $"Output route saved for {stream.Name}; stop and start it again to apply the route."
            : $"Output route saved for {stream.Name}.";
        return true;
    }

    public bool SaveChannelOutputDevice(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        string deviceId = channel.OutputDeviceIdText.Trim();
        if (deviceId.Length > 256)
        {
            AudioStatusText = "Output device IDs must be 256 characters or fewer.";
            return false;
        }

        if (deviceId.Length == 0 || deviceId.Equals("default", StringComparison.OrdinalIgnoreCase))
            userSettings.ChannelOutputDeviceIds.Remove(channel.SettingsKey);
        else
            userSettings.ChannelOutputDeviceIds[channel.SettingsKey] = deviceId;

        PersistUserSettings();
        channel.RestoreOutputDeviceId(deviceId);
        AudioStatusText = channel.IsAudioEnabled
            ? $"Output route saved for {channel.Name}; stop and listen again to apply it."
            : $"Output route saved for {channel.Name}.";
        return true;
    }

    private double GetChannelVolume(ChannelViewModel channel)
    {
        return userSettings.ChannelVolumes.TryGetValue(channel.SettingsKey, out double volume)
            ? volume
            : 1.0;
    }

    private string? GetChannelOutputDeviceId(ChannelViewModel channel)
    {
        if (userSettings.ChannelOutputDeviceIds.TryGetValue(channel.SettingsKey, out string? channelDeviceId))
            return channelDeviceId;
        return userSettings.AudioOutputDeviceId;
    }

    private string? GetWebStreamOutputDeviceId(WebStreamViewModel stream)
    {
        if (userSettings.WebStreamOutputDeviceIds.TryGetValue(stream.Name, out string? streamDeviceId))
            return streamDeviceId;
        return userSettings.AudioOutputDeviceId;
    }

    private async Task EnsureRecordingAudioAsync(ChannelViewModel channel)
    {
        if (!audioCoordinator.IsActive(channel))
            await StartAudioAsync(channel);
    }

    private void HandleDecodedSamples(ChannelViewModel channel, ReadOnlyMemory<short> samples)
    {
        patchForwarding.ObserveDecodedSamples(channel, samples);
        callRecordings.WriteSamples(channel, samples);
        UpdateChannelAudioLevel(channel, samples, ChannelAudioDirection.Receive);
    }

    private void HandleTransmitSamples(
        ChannelViewModel channel,
        uint streamId,
        uint sourceId,
        ReadOnlyMemory<short> samples)
    {
        callRecordings.WriteTransmitSamples(channel, streamId, sourceId, samples);
        UpdateChannelAudioLevel(channel, samples, ChannelAudioDirection.Transmit);
    }

    private static void UpdateChannelAudioLevel(
        ChannelViewModel channel,
        ReadOnlyMemory<short> samples,
        ChannelAudioDirection direction)
    {
        double level = ChannelAudioMeter.Calculate(samples.Span, direction);
        Dispatcher.UIThread.Post(() => channel.SetAudioLevel(level, direction));
    }

    private void ObservePatchDecodedSamples(ChannelViewModel channel, ReadOnlyMemory<short> samples)
    {
        patchForwarding.ObserveDecodedSamples(channel, samples);
    }

    private async Task SyncPatchSourceDecodeAsync()
    {
        try
        {
            ChannelViewModel[] channels = PatchGroups
                .Where(group => group.IsEnabled)
                .SelectMany(group => group.Members
                    .Where(member => member.IsMember)
                    .Select(member => member.Channel))
                .Distinct()
                .ToArray();
            await patchSourceDecode.ApplyChannelsAsync(channels).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Dispatcher.UIThread.Post(() =>
                AudioStatusText = $"Patch source decode unavailable: {exception.Message}");
        }
    }

    private async Task ProcessPatchSourceAsync(ChannelViewModel channel, FneTrafficFrame traffic)
    {
        try
        {
            await patchSourceDecode.ProcessAsync(channel, traffic).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Dispatcher.UIThread.Post(() =>
                AudioStatusText = $"Patch source decode stopped: {exception.Message}");
        }
    }

    private void EnqueuePatchSource(ChannelViewModel channel, FneTrafficFrame traffic)
    {
        Task current;
        lock (patchSourceWorkSync)
        {
            Task previous = patchSourceWork.TryGetValue(channel, out Task? pending)
                ? pending
                : Task.CompletedTask;
            current = previous
                .ContinueWith(
                    _ => ProcessPatchSourceAsync(channel, traffic),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default)
                .Unwrap();
            patchSourceWork[channel] = current;
        }

        _ = current.ContinueWith(
            _ =>
            {
                lock (patchSourceWorkSync)
                {
                    if (patchSourceWork.TryGetValue(channel, out Task? pending) &&
                        ReferenceEquals(pending, current))
                    {
                        patchSourceWork.Remove(channel);
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private bool ShouldRecordSource(ChannelViewModel channel, uint sourceId)
    {
        return !userSettings.RecordingIgnoredSubscriberIds.TryGetValue(
                channel.SettingsKey,
                out List<uint>? ignoredSubscriberIds) ||
            !ignoredSubscriberIds.Contains(sourceId);
    }

    private void HandleRecordingFaulted(ChannelViewModel channel, Exception exception)
    {
        Dispatcher.UIThread.Post(() =>
        {
            channel.SetRecordingEnabled(false);
            AudioStatusText = $"TAR recording stopped: {exception.Message}";
        });
    }

    private void HandleRecordingPlaybackFaulted(Exception exception)
    {
        Dispatcher.UIThread.Post(() =>
            AudioStatusText = $"Recording playback stopped: {exception.Message}");
    }

    private void RefreshRecordings()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            RefreshRecordingsCore();
            return;
        }

        Dispatcher.UIThread.Post(RefreshRecordingsCore);
    }

    private void RefreshRecordingsCore()
    {
        recordingEntries.Clear();
        foreach (CallRecordingMetadata metadata in callRecordings.LoadRecordings())
            recordingEntries.Add(metadata);
        foreach (CallHistoryEntry entry in callHistory.Entries)
            entry.SetRecording(FindRecordingForHistoryEntry(entry));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilteredRecordings)));
    }

    private CallRecordingMetadata? FindRecordingForHistoryEntry(CallHistoryEntry entry)
    {
        if (entry.IsEvent || entry.StreamId == 0)
            return null;

        string direction = entry.IsConsoleTransmission ? "TX" : "RX";
        return recordingEntries
            .Where(metadata => metadata.StreamId == entry.StreamId &&
                metadata.Direction.Equals(direction, StringComparison.OrdinalIgnoreCase) &&
                metadata.SystemName.Equals(entry.SystemName, StringComparison.OrdinalIgnoreCase) &&
                metadata.Protocol.Equals(entry.ProtocolText, StringComparison.OrdinalIgnoreCase))
            .OrderBy(metadata => Math.Abs((metadata.UtcStartTime - entry.Timestamp).TotalMilliseconds))
            .FirstOrDefault();
    }

    private void HandleSystemTraffic(SystemViewModel system, FneTrafficFrame traffic)
    {
        if (Volatile.Read(ref disposeStarted) != 0)
            return;

        void Apply()
        {
            if (Volatile.Read(ref disposeStarted) == 0)
                ProcessTraffic(system, traffic);
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
    }

    internal void ProcessTraffic(SystemViewModel system, FneTrafficFrame traffic)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(traffic);
        system.RecordTraffic(traffic);

        List<ChannelViewModel> activeAudioChannels = [];
        List<ChannelViewModel> activePatchSourceChannels = [];
        bool callHistoryChanged = false;
        bool matchedAnyChannel = false;
        ProtocolEncryptionMetadata? protocolEncryption = TryResolveProtocolEncryption(traffic);
        bool? protocolEncrypted = protocolEncryption?.Encrypted;
        foreach (SystemViewModel configuredSystem in Systems)
        {
            foreach (ChannelViewModel channel in configuredSystem.Channels)
            {
                bool sameActiveStream = channel.State == ChannelRuntimeState.Receiving &&
                    channel.StreamId == traffic.StreamId;
                bool matched = channel.TryApplyTraffic(system.Name, traffic);
                if (!matched)
                    continue;
                matchedAnyChannel = true;

                patchForwarding.ObserveTraffic(channel, traffic);
                if (patchSourceDecode.IsActive(channel))
                    activePatchSourceChannels.Add(channel);

                if (sameActiveStream && channel.State != ChannelRuntimeState.Receiving)
                {
                    callHistoryChanged = callHistory.Complete(
                        system.Name,
                        traffic.Protocol,
                        traffic.StreamId,
                        DateTimeOffset.Now) || callHistoryChanged;
                }
                else if (!sameActiveStream)
                {
                    callHistory.Add(new CallHistoryEntry(
                        DateTimeOffset.Now,
                        system.Name,
                        channel.Name,
                        traffic.SourceId,
                        traffic.DestinationId,
                        traffic.Protocol,
                        traffic.StreamId,
                        channel.LastCallerText,
                        protocolEncrypted ?? channel.Definition.IsEncrypted));
                    callHistoryChanged = true;
                }

                if (protocolEncrypted is bool encrypted)
                {
                    callHistoryChanged = callHistory.UpdateEncryption(
                        system.Name,
                        traffic.Protocol,
                        traffic.StreamId,
                        encrypted,
                        protocolEncryption?.AlgorithmId,
                        protocolEncryption?.KeyId) || callHistoryChanged;
                }

                if (audioCoordinator.IsActive(channel))
                    activeAudioChannels.Add(channel);
            }
        }

        if (!matchedAnyChannel &&
            traffic.Protocol == FneTrafficProtocol.Dmr &&
            IsDmrTerminator(traffic))
        {
            system.RecordNonCallDmrTerminator();
        }

        foreach (ChannelViewModel channel in activeAudioChannels)
            EnqueueReceiveAudio(channel, traffic);
        foreach (ChannelViewModel channel in activePatchSourceChannels)
            EnqueuePatchSource(channel, traffic);
        if (callHistoryChanged)
            NotifyCallHistoryChanged();
    }

    private static ProtocolEncryptionMetadata? TryResolveProtocolEncryption(FneTrafficFrame traffic)
    {
        if (traffic.Protocol == FneTrafficProtocol.P25 &&
            P25DfsiFrameCodec.TryExtractEncryptionMetadata(
                traffic,
                out P25DfsiFrameCodec.P25EncryptionMetadata p25Metadata))
        {
            return new ProtocolEncryptionMetadata(
                p25Metadata.AlgorithmId != P25Defines.P25_ALGO_UNENCRYPT,
                p25Metadata.AlgorithmId,
                p25Metadata.KeyId);
        }

        if (traffic.Protocol == FneTrafficProtocol.Dmr &&
            traffic.FrameType.Equals("DATA_SYNC", StringComparison.OrdinalIgnoreCase) &&
            traffic.Subtype.Equals("VOICE_PI_HEADER", StringComparison.OrdinalIgnoreCase) &&
            DmrVoicePacketCodec.TryExtractEncryptionMetadata(
                traffic.Payload,
                out DmrVoicePacketCodec.DmrEncryptionMetadata dmrMetadata))
        {
            return new ProtocolEncryptionMetadata(
                dmrMetadata.AlgorithmId != 0,
                dmrMetadata.AlgorithmId,
                dmrMetadata.KeyId);
        }

        return null;
    }

    private readonly record struct ProtocolEncryptionMetadata(
        bool Encrypted,
        byte AlgorithmId,
        ushort KeyId);

    private static bool IsDmrTerminator(FneTrafficFrame traffic)
    {
        return traffic.FrameType.Equals("TERMINATOR", StringComparison.OrdinalIgnoreCase) ||
            traffic.Subtype.Equals("TERMINATOR_WITH_LC", StringComparison.OrdinalIgnoreCase);
    }

    private async Task StartAudioAsync(ChannelViewModel channel)
    {
        try
        {
            await Task.Run(() => audioCoordinator.StartAsync(channel));
            channel.SetAudioEnabled(true);
            AudioStatusText = $"Listening to {channel.Name} ({channel.ModeText}); {audioCoordinator.ActiveChannels.Count} channel(s) active.";
        }
        catch (Exception exception)
        {
            channel.SetAudioEnabled(false);
            AudioStatusText = $"RX audio unavailable: {exception.Message}";
        }
    }

    private async Task StopAudioAsync(ChannelViewModel channel)
    {
        try
        {
            await Task.Run(() => audioCoordinator.StopAsync(channel));
        }
        finally
        {
            callRecordings.StopChannel(channel);
            RefreshRecordings();
            channel.SetAudioEnabled(false);
            AudioStatusText = audioCoordinator.ActiveChannels.Count == 0
                ? "RX audio disabled."
                : $"Listening to {audioCoordinator.ActiveChannels.Count} channel(s).";
        }
    }

    private async Task ProcessAudioAsync(ChannelViewModel channel, FneTrafficFrame traffic)
    {
        try
        {
            await audioCoordinator.ProcessAsync(channel, traffic).ConfigureAwait(false);
            ReceiveAudioDiagnostics diagnostics = audioCoordinator.GetDiagnostics(channel);
            if (diagnostics.HasIssues)
            {
                Dispatcher.UIThread.Post(() =>
                    AudioStatusText = $"RX {channel.Name}: {diagnostics.SummaryText} (audio continues)");
            }
        }
        catch (Exception exception)
        {
            if (IsAudioDeviceFailure(exception) &&
                await audioCoordinator.TryRecoverAsync(channel).ConfigureAwait(false))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    channel.SetAudioEnabled(true);
                    AudioStatusText = $"RX audio restarted for {channel.Name} after an output-device interruption.";
                });
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                channel.SetAudioEnabled(false);
                AudioStatusText = $"RX audio stopped: {exception.Message}";
            });
            await Task.Run(() => audioCoordinator.StopAsync(channel)).ConfigureAwait(false);
        }
        finally
        {
            // A terminator must close TAR even when the output device failed
            // while decoding the same frame; recording lifecycle is separate
            // from playback recovery.
            callRecordings.ObserveTraffic(channel, traffic);
            RefreshRecordings();
        }
    }

    private void EnqueueReceiveAudio(ChannelViewModel channel, FneTrafficFrame traffic)
    {
        if (Volatile.Read(ref disposeStarted) != 0)
            return;

        Task current;
        lock (receiveAudioWorkSync)
        {
            Task previous = receiveAudioWork.TryGetValue(channel, out Task? pending)
                ? pending
                : Task.CompletedTask;
            current = previous
                .ContinueWith(
                    _ => ProcessAudioAsync(channel, traffic),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default)
                .Unwrap();
            receiveAudioWork[channel] = current;
        }

        _ = current.ContinueWith(
            _ => RemoveCompletedWork(receiveAudioWorkSync, receiveAudioWork, channel, current),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task DrainFrameWorkAsync()
    {
        Task[] pending;
        lock (receiveAudioWorkSync)
            pending = receiveAudioWork.Values.ToArray();
        lock (patchSourceWorkSync)
            pending = pending.Concat(patchSourceWork.Values).ToArray();
        if (pending.Length > 0)
            await Task.WhenAll(pending).ConfigureAwait(false);
    }

    private static void RemoveCompletedWork(
        object sync,
        Dictionary<ChannelViewModel, Task> work,
        ChannelViewModel channel,
        Task completed)
    {
        lock (sync)
        {
            if (work.TryGetValue(channel, out Task? pending) && ReferenceEquals(pending, completed))
                work.Remove(channel);
        }
    }

    private static bool IsAudioDeviceFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is IOException or ObjectDisposedException)
                return true;

            if (current is InvalidOperationException &&
                (current.Message.Contains("audio", StringComparison.OrdinalIgnoreCase) ||
                 current.Message.Contains("playback", StringComparison.OrdinalIgnoreCase) ||
                 current.Message.Contains("device", StringComparison.OrdinalIgnoreCase) ||
                 current.Message.Contains("stream", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private async Task StartTransmitAsync(ChannelViewModel channel)
    {
        await StartTransmitAsync([channel]).ConfigureAwait(false);
    }

    private async Task StartTransmitAsync(IReadOnlyCollection<ChannelViewModel> channels)
    {
        if (channels.Count == 0 || transmitCoordinator.ActiveChannel is not null)
            return;

        TransmitTarget[] targets = channels
            .Select(channel => new TransmitTarget(
                channel,
                Systems.FirstOrDefault(candidate => candidate.Name.Equals(
                    channel.Definition.SystemName,
                    StringComparison.OrdinalIgnoreCase))!))
            .ToArray();
        ChannelViewModel? missingSystemChannel = targets
            .FirstOrDefault(target => target.System is null)?.Channel;
        if (missingSystemChannel is not null)
        {
            TransmitStatusText = $"PTT unavailable: system '{missingSystemChannel.Definition.SystemName}' was not found.";
            return;
        }

        try
        {
            ChannelViewModel[] receivingChannels = userSettings.MuteRxAudioWhileTransmitting
                ? audioCoordinator.ActiveChannels.ToArray()
                : [];
            if (receivingChannels.Length > 0)
            {
                suspendedAudioChannels = receivingChannels;
                await Task.Run(() => audioCoordinator.StopAsync());
                foreach (ChannelViewModel receivingChannel in receivingChannels)
                    receivingChannel.SetAudioSuspended(true);
                AudioStatusText = "RX audio muted while transmitting.";
            }

            await Task.Run(() => transmitCoordinator.StartAsync(targets));
            foreach (ChannelViewModel channel in transmitCoordinator.ActiveChannels)
                channel.SetTransmitEnabled(true, transmitCoordinator.GetActiveStreamId(channel));
            foreach (ChannelViewModel channel in transmitCoordinator.ActiveChannels)
            {
                TransmitTarget target = targets.First(candidate => ReferenceEquals(candidate.Channel, channel));
                callHistory.AddConsoleTransmission(
                    DateTimeOffset.Now,
                    target.System.Name,
                    channel.Name,
                    target.System.SourceId ?? 0,
                    channel.Definition.DestinationId,
                    ProtocolFor(channel),
                    transmitCoordinator.GetActiveStreamId(channel),
                    callerText: "Console",
                    encrypted: channel.Definition.IsEncrypted);
            }
            NotifyCallHistoryChanged();
            TransmitStatusText = transmitCoordinator.ActiveChannels.Count == 1
                ? $"Transmitting on {transmitCoordinator.ActiveChannel!.Name}."
                : $"Transmitting on {transmitCoordinator.ActiveChannels.Count} selected channels.";
            if (TalkPermitTone)
                await PlayTalkPermitToneAsync(reportSuccess: false).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            foreach (ChannelViewModel channel in channels)
            {
                channel.SetTransmitEnabled(false);
                callRecordings.StopTransmit(channel);
            }
            RefreshRecordings();
            await RestoreSuspendedAudioAsync();
            TransmitStatusText = $"PTT unavailable: {exception.Message}";
        }
    }

    private async Task StopTransmitAsync(ChannelViewModel channel)
        => await StopTransmitAsync([channel]).ConfigureAwait(false);

    private async Task StopTransmitAsync(IReadOnlyCollection<ChannelViewModel> channels)
    {
        (ChannelViewModel Channel, uint StreamId)[] activeStreams = channels
            .Select(channel => (channel, transmitCoordinator.GetActiveStreamId(channel)))
            .Where(entry => entry.Item2 != 0)
            .ToArray();
        Exception? stopFailure = null;
        try
        {
            await Task.Run(() => transmitCoordinator.StopAsync());
        }
        catch (Exception exception)
        {
            // A failed audio-device stop or final FNE terminator must release
            // the UI call state without escaping through an async-void PTT
            // pointer/key callback and terminating the desktop process.
            stopFailure = exception;
        }
        finally
        {
            foreach (ChannelViewModel channel in channels)
            {
                channel.SetTransmitEnabled(false);
                callRecordings.StopTransmit(channel);
            }
            foreach ((ChannelViewModel channel, uint streamId) in activeStreams)
            {
                SystemViewModel? system = Systems.FirstOrDefault(candidate => candidate.Channels.Contains(channel));
                if (system is not null)
                    callHistory.CompleteConsoleTransmission(
                        system.Name,
                        ProtocolFor(channel),
                        streamId,
                        DateTimeOffset.Now);
            }
            if (activeStreams.Length > 0)
                NotifyCallHistoryChanged();
            RefreshRecordings();
            try
            {
                await RestoreSuspendedAudioAsync();
            }
            catch (Exception exception)
            {
                stopFailure ??= exception;
            }
            TransmitStatusText = stopFailure is null
                ? "PTT idle."
                : $"Transmission stopped safely after an error: {stopFailure.Message}";
        }
    }

    public async Task TestTalkPermitToneAsync()
        => await PlayTalkPermitToneAsync(reportSuccess: true).ConfigureAwait(false);

    private async Task PlayTalkPermitToneAsync(bool reportSuccess)
    {
        try
        {
            AudioDeviceInfo output = await talkPermitTonePlayer.PlayAsync().ConfigureAwait(false);
            if (reportSuccess)
            {
                string drainText = talkPermitTonePlayer.LastQueuedSamples is int queued &&
                                    talkPermitTonePlayer.LastConsumedSamples is int consumed
                    ? $" queued {queued} / consumed {consumed} samples"
                    : string.Empty;
                AudioStatusText = $"Talk permit tone sent to {output.Name}.{drainText}";
            }
        }
        catch (Exception exception)
        {
            AudioStatusText = $"Talk permit tone unavailable: {exception.Message}";
        }
    }

    private async Task RestoreSuspendedAudioAsync()
    {
        ChannelViewModel[] channels = suspendedAudioChannels;
        suspendedAudioChannels = [];
        foreach (ChannelViewModel channel in channels)
        {
            if (channel.IsAudioSuspended)
                await StartAudioAsync(channel).ConfigureAwait(false);
        }
    }

    private bool CanSendGeneratedAudio()
    {
        if (busy || toneTransmitCoordinator.IsSending || transmitCoordinator.ActiveChannel is not null)
            return false;

        ChannelViewModel[] targets = ResolveGeneratedToneChannels();
        return targets.Length > 0 && targets.All(channel =>
        {
            SystemViewModel? system = Systems.FirstOrDefault(candidate => candidate.Name.Equals(
                channel.Definition.SystemName,
                StringComparison.OrdinalIgnoreCase));
            return channel.CanTransmit && system?.IsConnected == true && system.SourceId is uint sourceId && sourceId != 0;
        });
    }

    private void SaveDtmfPreset()
    {
        try
        {
            string digits = NormalizeDtmfInput(DtmfDigits);
            string name = string.IsNullOrWhiteSpace(DtmfPresetName)
                ? $"DTMF preset {dtmfPresets.Count + 1}"
                : DtmfPresetName.Trim();
            if (name.Length > 80)
                throw new ArgumentException("Preset names must be 80 characters or fewer.", nameof(DtmfPresetName));

            DtmfPresetViewModel next = new(new DtmfPresetSetting
            {
                Name = name,
                Digits = digits,
                Steps = digits
                    .Select(digit => new DtmfPresetStepSetting
                    {
                        Kind = AudioPresetStepKinds.Digit,
                        Digit = digit.ToString(),
                        DurationSeconds = 0.25
                    })
                    .ToList()
            });
            int existingIndex = dtmfPresets
                .Select((preset, index) => (preset, index))
                .Where(item => item.preset.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.index)
                .DefaultIfEmpty(-1)
                .First();
            if (existingIndex >= 0 && existingIndex < dtmfPresets.Count)
                dtmfPresets[existingIndex] = next;
            else
                dtmfPresets.Add(next);

            userSettings.DtmfPresets = dtmfPresets
                .Select(ToDtmfPresetSetting)
                .ToList();
            PersistUserSettings();
            DtmfPresetName = string.Empty;
            TransmitStatusText = $"DTMF preset '{name}' saved.";
        }
        catch (Exception exception)
        {
            TransmitStatusText = $"DTMF preset unavailable: {exception.Message}";
        }
    }

    private void ApplyRecordingRetention()
    {
        if (!int.TryParse(
                RecordingRetentionDaysText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int days) ||
            days < 0 || days > 3650)
        {
            TransmitStatusText = "Recording retention must be a whole number from 0 to 3650 days; 0 disables pruning.";
            return;
        }

        userSettings.RecordingRetentionDays = days;
        callRecordings.RetentionDays = days;
        PersistUserSettings();
        callRecordings.PruneExpired();
        RefreshRecordings();
        RecordingRetentionDaysText = days.ToString(CultureInfo.InvariantCulture);
        AudioStatusText = days == 0
            ? "TAR retention pruning disabled."
            : $"TAR retention set to {days} day(s).";
    }

    public void SaveAudioInputPreset()
    {
        if (!TryParseBounded(AudioInputGainText, 0.25, 3.0, out double gain) ||
            !TryParseBounded(AudioInputLowGainText, -12, 12, out double lowGainDb) ||
            !TryParseBounded(AudioInputMidGainText, -12, 12, out double midGainDb) ||
            !TryParseBounded(AudioInputHighGainText, -12, 12, out double highGainDb))
        {
            AudioStatusText = "Microphone presets require gain 0.25–3.0 and EQ values from -12 to 12 dB.";
            return;
        }

        string name = string.IsNullOrWhiteSpace(AudioInputPresetNameText)
            ? $"Mic preset {audioInputPresets.Count + 1}"
            : AudioInputPresetNameText.Trim();
        if (name.Length > 80)
        {
            AudioStatusText = "Microphone preset names must be 80 characters or fewer.";
            return;
        }

        AudioInputPresetViewModel next = new(new AudioInputPresetSetting
        {
            Name = name,
            Gain = gain,
            LowGainDb = lowGainDb,
            MidGainDb = midGainDb,
            HighGainDb = highGainDb
        });
        int existingIndex = audioInputPresets
            .Select((preset, index) => (preset, index))
            .Where(item => item.preset.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .DefaultIfEmpty(-1)
            .First();
        if (existingIndex >= 0 && existingIndex < audioInputPresets.Count)
            audioInputPresets[existingIndex] = next;
        else
            audioInputPresets.Add(next);

        AudioInputPresetNameText = name;
        PersistAudioInputPresetState();
        AudioStatusText = $"Microphone preset '{name}' saved.";
    }

    public void UseAudioInputPreset(AudioInputPresetViewModel preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        AudioInputPresetNameText = preset.Name;
        AudioInputGainText = preset.Gain.ToString("0.###", CultureInfo.InvariantCulture);
        AudioInputLowGainText = preset.LowGainDb.ToString("0.###", CultureInfo.InvariantCulture);
        AudioInputMidGainText = preset.MidGainDb.ToString("0.###", CultureInfo.InvariantCulture);
        AudioInputHighGainText = preset.HighGainDb.ToString("0.###", CultureInfo.InvariantCulture);
        ApplyAudioInputSettings();
        AudioStatusText = $"Microphone preset '{preset.Name}' loaded.";
    }

    public void DeleteAudioInputPreset(AudioInputPresetViewModel preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (!audioInputPresets.Remove(preset))
            return;

        if (AudioInputPresetNameText.Equals(preset.Name, StringComparison.OrdinalIgnoreCase))
            AudioInputPresetNameText = string.Empty;
        PersistAudioInputPresetState();
        AudioStatusText = $"Microphone preset '{preset.Name}' deleted.";
    }

    private void ApplyAudioInputSettings()
    {
        if (string.IsNullOrWhiteSpace(AudioInputDeviceIdText) || AudioInputDeviceIdText.Trim().Length > 256 ||
            string.IsNullOrWhiteSpace(AudioOutputDeviceIdText) || AudioOutputDeviceIdText.Trim().Length > 256 ||
            !TryParseBounded(AudioInputGainText, 0.25, 3.0, out double gain) ||
            !TryParseBounded(AudioInputLowGainText, -12, 12, out double lowGainDb) ||
            !TryParseBounded(AudioInputMidGainText, -12, 12, out double midGainDb) ||
            !TryParseBounded(AudioInputHighGainText, -12, 12, out double highGainDb))
        {
            AudioStatusText = "Microphone settings require a device ID, gain 0.25–3.0, and EQ values from -12 to 12 dB.";
            return;
        }

        string deviceId = AudioInputDeviceIdText.Trim();
        string outputDeviceId = AudioOutputDeviceIdText.Trim();
        userSettings.AudioInputDeviceId = deviceId;
        userSettings.AudioOutputDeviceId = outputDeviceId;
        userSettings.AudioInputAgcEnabled = AudioInputAgcEnabled;
        userSettings.AudioInputGain = gain;
        userSettings.AudioInputEqLowGainDb = lowGainDb;
        userSettings.AudioInputEqMidGainDb = midGainDb;
        userSettings.AudioInputEqHighGainDb = highGainDb;
        PersistAudioInputPresetState();
        transmitCoordinator.UpdateAudioInputOptions(new AudioInputProcessingOptions
        {
            DeviceId = deviceId,
            AgcEnabled = AudioInputAgcEnabled,
            Gain = gain,
            LowGainDb = lowGainDb,
            MidGainDb = midGainDb,
            HighGainDb = highGainDb
        });
        PersistUserSettings();
        AudioInputDeviceIdText = deviceId;
        AudioOutputDeviceIdText = outputDeviceId;
        AudioInputGainText = gain.ToString("0.###", CultureInfo.InvariantCulture);
        AudioInputLowGainText = lowGainDb.ToString("0.###", CultureInfo.InvariantCulture);
        AudioInputMidGainText = midGainDb.ToString("0.###", CultureInfo.InvariantCulture);
        AudioInputHighGainText = highGainDb.ToString("0.###", CultureInfo.InvariantCulture);
        AudioStatusText = "Audio device and microphone settings saved; device routes apply to the next audio session and PTT call.";
    }

    public void RefreshAudioDevices()
    {
        try
        {
            using IAudioBackend backend = AudioBackendFactory.CreateDefault(
                Environment.GetEnvironmentVariable("DVM_AUDIO_LIBRARY"));
            IReadOnlyList<AudioDeviceInfo> inputs = backend.EnumerateDevices(AudioDirection.Input);
            IReadOnlyList<AudioDeviceInfo> outputs = backend.EnumerateDevices(AudioDirection.Output);

            ReplaceAudioDeviceOptions(audioInputDevices, inputs);
            ReplaceAudioDeviceOptions(audioOutputDevices, outputs);
            foreach (WebStreamViewModel stream in webStreams)
                stream.RefreshOutputDeviceSelection();
            foreach (ChannelViewModel channel in Systems.SelectMany(system => system.Channels))
                channel.RefreshOutputDeviceSelection();
            selectedAudioInputDevice = ResolveAudioDeviceOption(audioInputDevices, AudioInputDeviceIdText);
            selectedAudioOutputDevice = ResolveAudioDeviceOption(audioOutputDevices, AudioOutputDeviceIdText);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedAudioInputDevice)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedAudioOutputDevice)));
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or DllNotFoundException or PlatformNotSupportedException)
        {
            audioInputDevices.Clear();
            audioOutputDevices.Clear();
            foreach (WebStreamViewModel stream in webStreams)
                stream.RefreshOutputDeviceSelection();
            foreach (ChannelViewModel channel in Systems.SelectMany(system => system.Channels))
                channel.RefreshOutputDeviceSelection();
            selectedAudioInputDevice = null;
            selectedAudioOutputDevice = null;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedAudioInputDevice)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedAudioOutputDevice)));
            AudioStatusText = $"Audio device list unavailable: {exception.Message}";
        }
    }

    private void ApplySelectedAudioDevices()
    {
        if (selectedAudioInputDevice is null || selectedAudioOutputDevice is null)
            return;

        AudioInputDeviceIdText = selectedAudioInputDevice.Id;
        AudioOutputDeviceIdText = selectedAudioOutputDevice.Id;
        userSettings.AudioInputDeviceId = selectedAudioInputDevice.Id;
        userSettings.AudioOutputDeviceId = selectedAudioOutputDevice.Id;
        transmitCoordinator.UpdateAudioInputOptions(new AudioInputProcessingOptions
        {
            DeviceId = selectedAudioInputDevice.Id,
            AgcEnabled = userSettings.AudioInputAgcEnabled,
            Gain = userSettings.AudioInputGain,
            LowGainDb = userSettings.AudioInputEqLowGainDb,
            MidGainDb = userSettings.AudioInputEqMidGainDb,
            HighGainDb = userSettings.AudioInputEqHighGainDb
        });
        PersistUserSettings();
        AudioStatusText = "Audio devices selected; restart an active receive channel before its output route changes.";
    }

    private static void ReplaceAudioDeviceOptions(
        ObservableCollection<AudioDeviceOptionViewModel> target,
        IReadOnlyList<AudioDeviceInfo> devices)
    {
        target.Clear();
        target.Add(new AudioDeviceOptionViewModel("default", "System default", true));
        foreach (AudioDeviceInfo device in devices)
        {
            if (device.Id.Equals("default", StringComparison.OrdinalIgnoreCase))
                continue;
            target.Add(new AudioDeviceOptionViewModel(device.Id, device.Name, device.IsDefault));
        }
    }

    private static AudioDeviceOptionViewModel? ResolveAudioDeviceOption(
        IEnumerable<AudioDeviceOptionViewModel> devices,
        string? requestedId)
    {
        return devices.FirstOrDefault(device => !string.IsNullOrWhiteSpace(requestedId) &&
                                                 device.Id.Equals(requestedId, StringComparison.OrdinalIgnoreCase))
               ?? devices.FirstOrDefault(device => device.IsDefault)
               ?? devices.FirstOrDefault();
    }

    private void PersistAudioInputPresetState()
    {
        userSettings.AudioInputPresetName = AudioInputPresetNameText.Trim();
        userSettings.AudioInputPresets = audioInputPresets
            .Select(preset => preset.ToSetting())
            .ToList();
        PersistUserSettings();
    }

    private static bool TryParseBounded(string value, double minimum, double maximum, out double result)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) &&
            double.IsFinite(result) && result >= minimum && result <= maximum;
    }

    private void SaveTonePreset()
    {
        if (!TryParseTone(out double frequency, out double durationSeconds, out string? error))
        {
            TransmitStatusText = error!;
            return;
        }

        string name = string.IsNullOrWhiteSpace(TonePresetName)
            ? $"Tone preset {tonePresets.Count + 1}"
            : TonePresetName.Trim();
        if (name.Length > 80)
        {
            TransmitStatusText = "Preset names must be 80 characters or fewer.";
            return;
        }

        TonePresetViewModel next = new(new TonePresetSetting
        {
            Name = name,
            FrequencyHz = frequency,
            DurationSeconds = durationSeconds,
            Steps =
            [
                new TonePresetStepSetting
                {
                    Kind = AudioPresetStepKinds.Tone,
                    FrequencyHz = frequency,
                    DurationSeconds = durationSeconds
                }
            ]
        });
        int existingIndex = tonePresets
            .Select((preset, index) => (preset, index))
            .Where(item => item.preset.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .DefaultIfEmpty(-1)
            .First();
        if (existingIndex >= 0 && existingIndex < tonePresets.Count)
            tonePresets[existingIndex] = next;
        else
            tonePresets.Add(next);

        userSettings.TonePresets = tonePresets
            .Select(ToTonePresetSetting)
            .ToList();
        PersistUserSettings();
        TonePresetName = string.Empty;
        TransmitStatusText = $"Tone preset '{name}' saved.";
    }

    public void UseDtmfPreset(DtmfPresetViewModel preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        DtmfDigits = preset.Digits;
        TransmitStatusText = $"DTMF preset '{preset.Name}' loaded.";
    }

    public void DeleteDtmfPreset(DtmfPresetViewModel preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (!dtmfPresets.Remove(preset))
            return;
        userSettings.DtmfPresets = dtmfPresets
            .Select(ToDtmfPresetSetting)
            .ToList();
        PersistUserSettings();
        TransmitStatusText = $"DTMF preset '{preset.Name}' deleted.";
    }

    public void UseTonePreset(TonePresetViewModel preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ToneFrequencyText = preset.FrequencyHz.ToString("0.###", CultureInfo.InvariantCulture);
        ToneDurationText = preset.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        TransmitStatusText = $"Tone preset '{preset.Name}' loaded.";
    }

    public void DeleteTonePreset(TonePresetViewModel preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (!tonePresets.Remove(preset))
            return;
        userSettings.TonePresets = tonePresets
            .Select(ToTonePresetSetting)
            .ToList();
        PersistUserSettings();
        TransmitStatusText = $"Tone preset '{preset.Name}' deleted.";
    }

    private async Task SendDtmfAsync()
    {
        try
        {
            string normalizedDigits = NormalizeDtmfInput(DtmfDigits);
            var generator = new DtmfToneGenerator();
            short[] samples = generator.GenerateSequence(
                normalizedDigits,
                TimeSpan.FromMilliseconds(240),
                TimeSpan.FromMilliseconds(60),
                amplitude: 0.35);
            userSettings.LastDtmfDigits = normalizedDigits;
            PersistUserSettings();
            await SendGeneratedToneAsync(samples, "DTMF");
        }
        catch (Exception exception)
        {
            TransmitStatusText = $"DTMF unavailable: {exception.Message}";
        }
    }

    public async Task SendDtmfPresetAsync(DtmfPresetViewModel preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        try
        {
            short[] samples = new DtmfToneGenerator().GenerateSteps(
                preset.Steps.Select(step => new DtmfToneStep(
                    string.IsNullOrWhiteSpace(step.Digit) ? '1' : step.Digit[0],
                    TimeSpan.FromSeconds(step.DurationSeconds),
                    string.Equals(step.Kind, AudioPresetStepKinds.Hold, StringComparison.OrdinalIgnoreCase))),
                amplitude: 0.35);
            await SendGeneratedToneAsync(samples, $"DTMF preset '{preset.Name}'");
        }
        catch (Exception exception)
        {
            TransmitStatusText = $"DTMF preset unavailable: {exception.Message}";
        }
    }

    private async Task SendToneAsync()
    {
        if (!TryParseTone(out double frequency, out double durationSeconds, out string? error))
        {
            TransmitStatusText = error!;
            return;
        }

        try
        {
            short[] samples = new PcmToneGenerator().GenerateTone(
                frequency,
                TimeSpan.FromSeconds(durationSeconds),
                amplitude: 0.35);
            userSettings.ToneFrequencyHz = frequency;
            userSettings.ToneDurationSeconds = durationSeconds;
            PersistUserSettings();
            await SendGeneratedToneAsync(samples, "Alert tone");
        }
        catch (Exception exception)
        {
            TransmitStatusText = $"Alert tone unavailable: {exception.Message}";
        }
    }

    public async Task SendTonePresetAsync(TonePresetViewModel preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        try
        {
            short[] samples = new PcmToneGenerator().GenerateSteps(
                preset.Steps.Select(step => new PcmToneStep(
                    step.FrequencyHz,
                    TimeSpan.FromSeconds(step.DurationSeconds),
                    string.Equals(step.Kind, AudioPresetStepKinds.Hold, StringComparison.OrdinalIgnoreCase))),
                amplitude: 0.35);
            await SendGeneratedToneAsync(samples, $"Tone preset '{preset.Name}'");
        }
        catch (Exception exception)
        {
            TransmitStatusText = $"Tone preset unavailable: {exception.Message}";
        }
    }

    public async Task SendQuickCallAsync()
    {
        if (!QuickCallToneGenerator.TryParse(
                QuickCallToneAText,
                QuickCallToneBText,
                out double toneAFrequencyHz,
                out double toneBFrequencyHz,
                out string? error))
        {
            TransmitStatusText = error!;
            return;
        }

        ChannelViewModel[] pageTargets = ResolvePageToneChannels();
        if (pageTargets.Length == 0)
        {
            TransmitStatusText = "Arm PAGE on one or more channel cards before sending QCII.";
            return;
        }

        try
        {
            short[] samples = QuickCallToneGenerator.Generate(toneAFrequencyHz, toneBFrequencyHz);
            userSettings.QuickCallToneAFrequencyHz = toneAFrequencyHz;
            userSettings.QuickCallToneBFrequencyHz = toneBFrequencyHz;
            PersistUserSettings();
            await SendGeneratedToneAsync(samples, "QCII page", pageTargets);
            foreach (ChannelViewModel channel in pageTargets)
                channel.SetPageSelected(false);
        }
        catch (Exception exception)
        {
            TransmitStatusText = $"QCII page unavailable: {exception.Message}";
        }
    }

    public bool AddAlertTone(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("The alert audio file was not found.", fullPath);

            string name = string.IsNullOrWhiteSpace(AlertToneNameText)
                ? Path.GetFileNameWithoutExtension(fullPath)
                : AlertToneNameText.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 80)
                throw new ArgumentException("Alert tone names must contain 1–80 characters.", nameof(path));

            AlertToneViewModel? existing = alertTones.FirstOrDefault(tone =>
                tone.FilePath.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
                alertTones.Remove(existing);
            alertTones.Add(new AlertToneViewModel(new AlertToneSetting
            {
                Name = name,
                FilePath = fullPath
            }));
            userSettings.AlertTones = alertTones.Select(tone => tone.ToSetting()).ToList();
            PersistUserSettings();
            AlertToneNameText = string.Empty;
            TransmitStatusText = $"Alert asset '{name}' imported.";
            return true;
        }
        catch (Exception exception)
        {
            TransmitStatusText = $"Alert asset unavailable: {exception.Message}";
            return false;
        }
    }

    public void DeleteAlertTone(AlertToneViewModel tone)
    {
        ArgumentNullException.ThrowIfNull(tone);
        if (!alertTones.Remove(tone))
            return;
        userSettings.AlertTones = alertTones.Select(item => item.ToSetting()).ToList();
        PersistUserSettings();
        TransmitStatusText = $"Alert asset '{tone.Name}' removed.";
    }

    public async Task SendAlertToneAsync(AlertToneViewModel tone)
    {
        ArgumentNullException.ThrowIfNull(tone);
        try
        {
            short[] samples = await PcmAudioFileLoader.LoadAsync(tone.FilePath);
            ChannelViewModel[] alertTargets = ResolveGeneratedToneChannels();
            await SendGeneratedToneAsync(
                samples,
                $"Alert asset '{tone.Name}'",
                alertTargets);
        }
        catch (Exception exception)
        {
            TransmitStatusText = $"Alert asset unavailable: {exception.Message}";
        }
    }

    public async Task SendBuiltInAlertToneAsync(BuiltInAlertToneViewModel tone)
    {
        ArgumentNullException.ThrowIfNull(tone);
        try
        {
            short[] samples = LegacyAlertToneGenerator.Generate(tone.Tone, amplitude: 0.35);
            await SendGeneratedToneAsync(
                samples,
                tone.Name,
                ResolveGeneratedToneChannels());
        }
        catch (Exception exception)
        {
            TransmitStatusText = $"{tone.Name} unavailable: {exception.Message}";
        }
    }

    private static DtmfPresetSetting ToDtmfPresetSetting(DtmfPresetViewModel preset)
        => new()
        {
            Name = preset.Name,
            Digits = preset.Digits,
            Steps = preset.Steps
                .Select(step => new DtmfPresetStepSetting
                {
                    Kind = step.Kind,
                    Digit = step.Digit,
                    DurationSeconds = step.DurationSeconds
                })
                .ToList()
        };

    private static TonePresetSetting ToTonePresetSetting(TonePresetViewModel preset)
        => new()
        {
            Name = preset.Name,
            FrequencyHz = preset.FrequencyHz,
            DurationSeconds = preset.DurationSeconds,
            Steps = preset.Steps
                .Select(step => new TonePresetStepSetting
                {
                    Kind = step.Kind,
                    FrequencyHz = step.FrequencyHz,
                    DurationSeconds = step.DurationSeconds
                })
                .ToList()
        };

    private bool TryParseTone(out double frequency, out double durationSeconds, out string? error)
    {
        frequency = 0;
        durationSeconds = 0;
        error = null;
        if (!double.TryParse(
                ToneFrequencyText,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out frequency) ||
            !double.TryParse(
                ToneDurationText,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out durationSeconds) ||
            frequency < 1 || frequency >= 4000 || durationSeconds <= 0 || durationSeconds > 10)
        {
            error = "Tone frequency must be 1–3999 Hz and duration must be 0–10 seconds.";
            return false;
        }

        return true;
    }

    private static string NormalizeDtmfInput(string value)
    {
        string normalized = new string((value ?? string.Empty)
            .Where(character => !char.IsWhiteSpace(character))
            .Select(char.ToUpperInvariant)
            .ToArray());
        if (normalized.Length is 0 or > 64 || normalized.Any(character => !DtmfToneGenerator.IsDigit(character)))
            throw new ArgumentException("DTMF must contain 1–64 digits from 0–9, *, #, or A–D.", nameof(value));
        return normalized;
    }

    private async Task SendGeneratedToneAsync(
        ReadOnlyMemory<short> samples,
        string label,
        IReadOnlyCollection<ChannelViewModel>? explicitTargets = null)
    {
        ChannelViewModel[] channels = explicitTargets?.ToArray() ?? ResolveGeneratedToneChannels();
        if (channels.Length == 0)
            throw new InvalidOperationException("Arm ALERT on one or more channel cards before sending DTMF or alert audio.");

        TransmitTarget[] targets = channels
            .Distinct()
            .Select(channel => new TransmitTarget(
                channel,
                Systems.FirstOrDefault(candidate => candidate.Name.Equals(
                    channel.Definition.SystemName,
                    StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException(
                        $"The system '{channel.Definition.SystemName}' was not found.")))
            .ToArray();
        if (transmitCoordinator.ActiveChannel is not null)
            throw new InvalidOperationException("Release PTT before sending generated audio.");

        ChannelViewModel[] receivingChannels = audioCoordinator.ActiveChannels.ToArray();
        if (receivingChannels.Length > 0)
        {
            await audioCoordinator.StopAsync();
            suspendedAudioChannels = receivingChannels;
            foreach (ChannelViewModel receivingChannel in receivingChannels)
                receivingChannel.SetAudioSuspended(true);
            AudioStatusText = "RX audio muted while sending generated audio.";
        }

        try
        {
            await toneTransmitCoordinator.SendAsync(targets, samples);
            string targetText = FormatToneTargetText(targets.Select(target => target.Channel));
            await RunOnUiThreadAsync(() => TransmitStatusText = $"{label} sent on {targetText}.");
        }
        finally
        {
            await RestoreSuspendedAudioAsync();
            await RunOnUiThreadAsync(RaiseGeneratedAudioCanExecuteChanged);
        }
    }

    private static async Task RunOnUiThreadAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(action);
    }

    internal ChannelViewModel[] ResolveGeneratedToneChannels()
        => Systems
            .SelectMany(system => system.Channels)
            .Where(channel => channel.IsAlertSelected)
            .Distinct()
            .ToArray();

    internal ChannelViewModel[] ResolvePageToneChannels()
        => Systems
            .SelectMany(system => system.Channels)
            .Where(channel => channel.IsPageSelected)
            .Distinct()
            .ToArray();

    private static string FormatToneTargetText(IEnumerable<ChannelViewModel> channels)
    {
        string[] names = channels.Select(channel => channel.Name).Distinct().ToArray();
        return names.Length <= 4
            ? string.Join(", ", names)
            : $"{names.Length} ALERT/PAGE-selected channels";
    }

    private void RaiseGeneratedAudioCanExecuteChanged()
    {
        (SendDtmfCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SendToneCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private static FneTrafficProtocol ProtocolFor(ChannelViewModel channel)
        => channel.Definition.Mode switch
        {
            "dmr" => FneTrafficProtocol.Dmr,
            "p25" => FneTrafficProtocol.P25,
            "nxdn" => FneTrafficProtocol.Nxdn,
            _ => FneTrafficProtocol.Analog
        };

    private void HandleKeyboardPttStateChanged(object? sender, bool pressed)
    {
        if (Dispatcher.UIThread.CheckAccess())
            _ = HandleKeyboardPttStateChangedAsync(pressed);
        else
            Dispatcher.UIThread.Post(() => _ = HandleKeyboardPttStateChangedAsync(pressed));
    }

    private async Task HandleKeyboardPttStateChangedAsync(bool pressed)
    {
        if (pressed)
        {
            ChannelViewModel[] targets = Systems
                .SelectMany(system => system.Channels)
                .Where(channel => channel.IsTransmitSelected)
                .ToArray();
            if (targets.Length == 0)
            {
                TransmitStatusText = $"Choose TX on one or more cards before using {GlobalPttKeyText}.";
                return;
            }
            if (transmitCoordinator.ActiveChannel is not null)
                return;

            await StartTransmitAsync(targets);
            if (!AnyPttSourcePressed && transmitCoordinator.ActiveChannel is not null)
                await StopTransmitAsync(transmitCoordinator.ActiveChannels);
            return;
        }

        if (AnyPttSourcePressed)
            return;

        ChannelViewModel[] active = transmitCoordinator.ActiveChannels.ToArray();
        if (active.Length > 0)
            await StopTransmitAsync(active);
    }

    private bool AnyPttSourcePressed
        => (globalKeyboardPtt?.IsPressed ?? keyboardPtt.IsPressed) || serialPtt?.IsPressed == true;

    private static int ReadSerialPttBaudRate()
    {
        string? configured = Environment.GetEnvironmentVariable("DVM_PTT_SERIAL_BAUD");
        return int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out int baudRate) && baudRate > 0
            ? baudRate
            : 9_600;
    }

    private static KeyboardPttKey ParseGlobalPttKey(string? value)
        => Enum.TryParse(value, ignoreCase: true, out KeyboardPttKey key)
            ? key
            : KeyboardPttKey.Space;

    private void HandleTransmitFaulted(object? sender, Exception exception)
    {
        ChannelViewModel[] channels = transmitCoordinator.ActiveChannels.ToArray();
        (ChannelViewModel Channel, uint StreamId)[] activeStreams = channels
            .Select(channel => (channel, transmitCoordinator.GetActiveStreamId(channel)))
            .Where(entry => entry.Item2 != 0)
            .ToArray();
        Dispatcher.UIThread.Post(() =>
        {
            foreach (ChannelViewModel channel in channels)
                channel.SetTransmitEnabled(false);
            activeMultiSelectGroup?.SetPttActive(false);
            activeMultiSelectGroup = null;
            TransmitStatusText = $"Transmission stopped: {exception.Message}";
        });
        _ = Task.Run(async () =>
        {
            try
            {
                await transmitCoordinator.StopAsync().ConfigureAwait(false);
            }
            catch
            {
                // The original fault is already reported to the operator.
            }
            finally
            {
                foreach ((ChannelViewModel channel, uint streamId) in activeStreams)
                {
                    callRecordings.StopTransmit(channel);
                    SystemViewModel? system = Systems.FirstOrDefault(candidate => candidate.Channels.Contains(channel));
                    if (system is not null)
                        callHistory.CompleteConsoleTransmission(
                            system.Name,
                            ProtocolFor(channel),
                            streamId,
                            DateTimeOffset.Now);
                }
                if (activeStreams.Length > 0)
                    Dispatcher.UIThread.Post(NotifyCallHistoryChanged);
                RefreshRecordings();
            }
            try
            {
                await RestoreSuspendedAudioAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                Dispatcher.UIThread.Post(() =>
                    TransmitStatusText = $"Transmission stopped; audio recovery failed: {cleanupException.Message}");
            }
        });
    }

    private void SetBusy(bool value)
    {
        busy = value;
        (ConnectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DisconnectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        RaiseGeneratedAudioCanExecuteChanged();
    }

    private void NotifyConnectionPresentationChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionCommand)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionButtonText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionPillText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSystemName)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SystemStatusText)));
    }

    private void RecordSubscriberCommandAudit(
        string systemName,
        P25SubscriberCommand command,
        uint destinationId,
        bool succeeded,
        string detail)
    {
        if (subscriberCommandAudit.Count >= MaximumSubscriberCommandAuditEntries)
            subscriberCommandAudit.RemoveAt(subscriberCommandAudit.Count - 1);

        subscriberCommandAudit.Insert(0, new SubscriberCommandAuditEntry(
            DateTimeOffset.UtcNow,
            systemName,
            command,
            destinationId,
            succeeded,
            detail));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActivitySubscriberCommandAudit)));
    }

    private static string CommandName(P25SubscriberCommand command)
        => command switch
        {
            P25SubscriberCommand.CallAlert => "Page",
            P25SubscriberCommand.RadioCheck => "Radio check",
            P25SubscriberCommand.Inhibit => "Inhibit",
            P25SubscriberCommand.Uninhibit => "Uninhibit",
            _ => command.ToString()
        };

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void LoadUserBackground(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            mainBackgroundBrush = CreateShellBackgroundBrush(userSettings.DarkMode);
            return;
        }

        try
        {
            userBackgroundBitmap = new Bitmap(path);
            mainBackgroundBrush = new ImageBrush(userBackgroundBitmap)
            {
                Stretch = Stretch.UniformToFill,
                Opacity = 0.22
            };
        }
        catch
        {
            userBackgroundBitmap = null;
            mainBackgroundBrush = CreateShellBackgroundBrush(userSettings.DarkMode);
        }
    }

    private static IBrush CreateShellBackgroundBrush(bool darkMode)
        => new SolidColorBrush(Color.Parse(darkMode ? "#0D1116" : "#F3F5F7"));

    private void RestoreChannelWidgetLayout()
    {
        ApplyDefaultChannelWidgetLayout();
        foreach (ChannelViewModel channel in Zones.SelectMany(zone => zone.Channels).Distinct())
        {
            if (userSettings.ChannelWidgetPositions.TryGetValue(channel.SettingsKey, out WidgetPositionSetting? position))
                channel.SetWidgetPosition(position.X, position.Y);
        }
    }

    private void ApplyDefaultChannelWidgetLayout()
    {
        foreach (ZoneViewModel zone in Zones)
        {
            double x = 0;
            double y = 0;
            foreach (ChannelViewModel channel in zone.Channels)
            {
                if (x > 0 && x + channel.CardWidth > DefaultWidgetCanvasWidth)
                {
                    x = 0;
                    y += ChannelCardHeight + ChannelWidgetSpacing;
                }

                channel.SetWidgetPosition(x, y);
                x += channel.CardWidth + ChannelWidgetSpacing;
            }
        }
    }

    private void HandleClockTick(object? sender, EventArgs e)
    {
        RefreshClock();
        ExpireStaleReceiveStates(DateTimeOffset.UtcNow);
    }

    private void ExpireStaleReceiveStates(DateTimeOffset now)
    {
        bool recordingStateChanged = false;
        bool callHistoryChanged = false;
        foreach (ChannelViewModel channel in Systems.SelectMany(system => system.Channels))
        {
            uint? activeStreamId = channel.StreamId;
            if (!channel.TryExpireReceiveState(now, TimeSpan.FromSeconds(2)))
                continue;
            if (activeStreamId is uint streamId)
            {
                callHistoryChanged = callHistory.Complete(
                    channel.Definition.SystemName,
                    channel.Definition.Mode switch
                    {
                        "dmr" => FneTrafficProtocol.Dmr,
                        "p25" => FneTrafficProtocol.P25,
                        "nxdn" => FneTrafficProtocol.Nxdn,
                        _ => FneTrafficProtocol.Analog
                    },
                    streamId,
                    now) || callHistoryChanged;
            }
            callRecordings.StopChannel(channel);
            recordingStateChanged = true;
        }
        if (recordingStateChanged)
            RefreshRecordings();
        if (callHistoryChanged)
            NotifyCallHistoryChanged();
    }

    private void NotifyCallHistoryChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilteredCallHistory)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActivityCallHistory)));
    }

    private void RefreshClock()
    {
        SetField(
            ref clockText,
            FormatClock(DateTime.Now, userSettings.ClockUse24HourTime, userSettings.ClockShowSeconds),
            nameof(ClockText));
        DateTimeOffset utcNow = DateTimeOffset.UtcNow;
        foreach (ToolbarClockViewModel clock in toolbarClocks)
            clock.Update(utcNow, userSettings.ClockUse24HourTime, userSettings.ClockShowSeconds);
    }

    internal static string FormatClock(DateTime value, bool use24HourTime, bool showSeconds)
    {
        string format = use24HourTime
            ? showSeconds ? "HH:mm:ss" : "HH:mm"
            : showSeconds ? "h:mm:ss tt" : "h:mm tt";
        return value.ToString(format, CultureInfo.CurrentCulture);
    }

    private static void ApplyTheme(bool darkMode)
    {
        if (Application.Current is not Application application)
            return;

        application.RequestedThemeVariant = darkMode ? ThemeVariant.Dark : ThemeVariant.Light;
        IReadOnlyDictionary<string, string> colors = darkMode
            ? new Dictionary<string, string>
            {
                ["ShellBackgroundBrush"] = "#0D1116",
                ["ShellHeaderBrush"] = "#1A2028",
                ["PrimaryTextBrush"] = "#DCE3EB",
                ["CardBackgroundBrush"] = "#151D26",
                ["MutedTextBrush"] = "#B7C0C9",
                ["ButtonBackgroundBrush"] = "#1A222D",
                ["ButtonHoverBrush"] = "#253446",
                ["ControlBorderBrush"] = "#273443",
                ["PttBackgroundBrush"] = "#17202B",
                ["SelectorBackgroundBrush"] = "#242938",
                ["TabTextBrush"] = "#AEB9C5",
                ["SelectedTabTextBrush"] = "#F4F7FA",
                ["SidebarBackgroundBrush"] = "#151C25",
                ["ActivityBackgroundBrush"] = "#1C2530",
                ["StatusBarBackgroundBrush"] = "#1A2028",
                ["SplitterBrush"] = "#25313D",
                ["ClockTextBrush"] = "#FFFFFF",
                ["ClockBorderBrush"] = "#3A4654",
                ["WarningBackgroundBrush"] = "#332A1A",
                ["WarningBorderBrush"] = "#7A5C28"
            }
            : new Dictionary<string, string>
            {
                ["ShellBackgroundBrush"] = "#F3F5F7",
                ["ShellHeaderBrush"] = "#E4E8EC",
                ["PrimaryTextBrush"] = "#18212B",
                ["CardBackgroundBrush"] = "#FFFFFF",
                ["MutedTextBrush"] = "#4D5965",
                ["ButtonBackgroundBrush"] = "#FFFFFF",
                ["ButtonHoverBrush"] = "#DDE5ED",
                ["ControlBorderBrush"] = "#8996A3",
                ["PttBackgroundBrush"] = "#E2E8EF",
                ["SelectorBackgroundBrush"] = "#E8EDF3",
                ["TabTextBrush"] = "#40505F",
                ["SelectedTabTextBrush"] = "#111820",
                ["SidebarBackgroundBrush"] = "#E9EEF3",
                ["ActivityBackgroundBrush"] = "#FFFFFF",
                ["StatusBarBackgroundBrush"] = "#E1E7ED",
                ["SplitterBrush"] = "#8996A3",
                ["ClockTextBrush"] = "#FFFFFF",
                ["ClockBorderBrush"] = "#65717D",
                ["WarningBackgroundBrush"] = "#FFF4D6",
                ["WarningBorderBrush"] = "#B47B18"
            };
        foreach (KeyValuePair<string, string> entry in colors)
            application.Resources[entry.Key] = new SolidColorBrush(Color.Parse(entry.Value));
    }

    private void PersistUserSettings()
    {
        try
        {
            userSettingsStore.Save(userSettings);
        }
        catch (IOException)
        {
            // Operator state must never prevent the console from running.
        }
    }

    public void ApplyPatchGroup(
        string groupName,
        IEnumerable<PatchMemberAddress> members,
        bool enabled,
        bool oneWay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);
        ArgumentNullException.ThrowIfNull(members);

        string normalizedName = groupName.Trim();
        List<PatchMemberAddress> normalizedMembers = members
            .Where(member => !string.IsNullOrWhiteSpace(member.SystemName) && member.DestinationId != 0)
            .Select(member => new PatchMemberAddress(member.SystemName.Trim(), member.DestinationId))
            .GroupBy(member => member.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        PersistGroupDefinition(normalizedName, normalizedMembers, enabled, oneWay);
        ReapplyPatchState();
        PersistUserSettings();
        RefreshPatchMembershipConflicts();
        _ = SyncPatchSourceDecodeAsync();
    }

    public void ApplyPatchGroup(PatchGroupEditorViewModel group)
    {
        ArgumentNullException.ThrowIfNull(group);
        List<PatchMemberAddress> members = group.Members
            .Where(member => member.IsMember)
            .Select(member => new PatchMemberAddress(
                member.Channel.Definition.SystemName,
                member.Channel.Definition.DestinationId))
            .ToList();
        if (group.IsMultiSelect)
        {
            if (ReferenceEquals(activeMultiSelectGroup, group))
            {
                StatusText = $"Stop multi-select PTT for '{group.Name}' before changing its membership.";
                return;
            }
            PersistGroupDefinition(group.Name, members, enabled: true, oneWay: false);
            PersistUserSettings();
            RefreshPatchMembershipConflicts();
            StatusText = $"Multi-select group '{group.Name}' saved with {members.Count} member(s).";
            return;
        }

        ApplyPatchGroup(group.Name, members, group.IsEnabled, group.IsOneWay);
        RefreshPatchMembershipConflicts();
        StatusText = $"Patch group '{group.Name}' {(group.IsEnabled ? "enabled" : "disabled")}.";
    }

    public async Task ToggleMultiSelectPttAsync(PatchGroupEditorViewModel group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!group.IsMultiSelect)
            return;

        if (group.IsPttActive)
        {
            ChannelViewModel[] active = transmitCoordinator.ActiveChannels.ToArray();
            if (active.Length > 0)
                await StopTransmitAsync(active).ConfigureAwait(false);
            group.SetPttActive(false);
            if (ReferenceEquals(activeMultiSelectGroup, group))
                activeMultiSelectGroup = null;
            return;
        }

        if (transmitCoordinator.ActiveChannels.Count > 0)
        {
            TransmitStatusText = "Stop the current transmission before starting multi-select PTT.";
            return;
        }

        ChannelViewModel[] targets = group.Members
            .Where(member => member.IsMember && member.CanTransmit)
            .Select(member => member.Channel)
            .Distinct()
            .ToArray();
        if (targets.Length == 0)
        {
            TransmitStatusText = $"Multi-select group '{group.Name}' has no transmit-capable members.";
            return;
        }

        await StartTransmitAsync(targets).ConfigureAwait(false);
        if (transmitCoordinator.ActiveChannels.Count == targets.Length)
        {
            activeMultiSelectGroup?.SetPttActive(false);
            activeMultiSelectGroup = group;
            group.SetPttActive(true);
        }
    }

    private void PersistGroupDefinition(
        string groupName,
        IEnumerable<PatchMemberAddress> members,
        bool enabled,
        bool oneWay)
    {
        string normalizedName = groupName.Trim();
        userSettings.PatchGroupMemberships[normalizedName] = members
            .Select(member => new PatchMemberSetting
            {
                SystemName = member.SystemName,
                DestinationId = member.DestinationId
            })
            .ToList();
        userSettings.PatchGroupModes[normalizedName] = oneWay;
        userSettings.PatchGroupEnabledStates[normalizedName] = enabled;
    }

    private IReadOnlyList<PatchGroupEditorViewModel> BuildPatchGroups(
        IEnumerable<GroupConfiguration> groupDefinitions)
    {
        IReadOnlyList<ChannelViewModel> channels = Systems
            .SelectMany(system => system.Channels)
            .ToArray();
        List<PatchGroupEditorViewModel> groups = [];
        foreach (GroupConfiguration definition in groupDefinitions)
        {
            string groupName = definition.Name.Trim();
            if (groupName.Length == 0)
                continue;

            HashSet<string> configuredMembers = userSettings.PatchGroupMemberships
                .TryGetValue(groupName, out List<PatchMemberSetting>? savedMembers)
                ? (savedMembers ?? [])
                    .Select(member => $"{member.SystemName.Trim().ToLowerInvariant()}|{member.DestinationId}")
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];
            bool isMultiSelect = definition.IsMultiselectGroup();
            bool enabled = isMultiSelect ||
                (userSettings.PatchGroupEnabledStates.TryGetValue(groupName, out bool savedEnabled) && savedEnabled);
            bool oneWay = userSettings.PatchGroupModes.TryGetValue(groupName, out bool savedOneWay) && savedOneWay;
            var group = new PatchGroupEditorViewModel(
                groupName,
                enabled,
                oneWay,
                channels.Select(channel => new PatchMemberEditorViewModel(
                    channel,
                    configuredMembers.Contains($"{channel.Definition.SystemName.ToLowerInvariant()}|{channel.Definition.DestinationId}"))),
                isMultiSelect);
            group.MembershipChanged += HandlePatchMembershipChanged;
            groups.Add(group);
        }

        return groups;
    }

    private void HandlePatchMembershipChanged(object? sender, EventArgs args)
        => RefreshPatchMembershipConflicts();

    private void RefreshPatchMembershipConflicts()
    {
        Dictionary<string, List<(PatchGroupEditorViewModel Group, PatchMemberEditorViewModel Member)>> memberships =
            PatchGroups
                .SelectMany(group => group.Members
                    .Where(member => member.IsMember)
                    .Select(member => (Group: group, Member: member)))
                .GroupBy(item => item.Member.Channel.SettingsKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (PatchGroupEditorViewModel group in PatchGroups)
        {
            List<string> conflictingChannels = [];
            foreach (PatchMemberEditorViewModel member in group.Members)
            {
                if (!member.IsMember ||
                    !memberships.TryGetValue(member.Channel.SettingsKey, out List<(PatchGroupEditorViewModel Group, PatchMemberEditorViewModel Member)>? owners) ||
                    owners.Count < 2)
                {
                    member.SetConflictText(null);
                    continue;
                }

                string otherGroups = string.Join(", ", owners
                    .Where(owner => !ReferenceEquals(owner.Group, group))
                    .Select(owner => owner.Group.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase));
                member.SetConflictText($"Also assigned to: {otherGroups}");
                conflictingChannels.Add(member.Channel.Name);
            }

            group.SetConflictSummary(conflictingChannels.Count == 0
                ? null
                : $"{conflictingChannels.Count} member overlap(s): {string.Join(", ", conflictingChannels.Distinct(StringComparer.OrdinalIgnoreCase))}");
        }
    }

    private void RestorePatchState(IEnumerable<GroupConfiguration>? groupDefinitions)
    {
        if (!userSettings.RetainPatchStateOnStartup || groupDefinitions is null)
            return;

        ReapplyPatchState(groupDefinitions);
    }

    private void ReapplyPatchState(IEnumerable<GroupConfiguration>? groupDefinitions = null)
    {
        IEnumerable<string> configuredPatchNames = groupDefinitions is not null
            ? groupDefinitions
                .Where(group => group.IsPatchGroup())
                .Select(group => group.Name.Trim())
            : PatchGroups
                .Where(group => group.IsPatchGroup)
                .Select(group => group.Name);
        HashSet<string> patchGroupNames = configuredPatchNames
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var memberships = new Dictionary<string, IReadOnlyList<PatchMemberAddress>>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, List<PatchMemberSetting>> entry in userSettings.PatchGroupMemberships)
        {
            if (!patchGroupNames.Contains(entry.Key))
                continue;
            if (!userSettings.PatchGroupEnabledStates.TryGetValue(entry.Key, out bool enabled) || !enabled)
                continue;

            memberships[entry.Key] = entry.Value
                .Where(member => !string.IsNullOrWhiteSpace(member.SystemName) && member.DestinationId != 0)
                .Select(member => new PatchMemberAddress(member.SystemName.Trim(), member.DestinationId))
                .GroupBy(member => member.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
        }

        patchForwarding.ApplyMemberships(memberships, userSettings.PatchGroupModes);
    }

    private void RecordLoadedCodeplug(string path)
    {
        string normalizedPath = Path.GetFullPath(path);
        userSettings.LastCodeplugPath = normalizedPath;
        userSettings.RecentCodeplugPaths = new[] { normalizedPath }
            .Concat(userSettings.RecentCodeplugPaths ?? [])
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(candidate => Path.GetFullPath(candidate.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(UserSettings.MaximumRecentCodeplugs)
            .ToList();
        recentCodeplugPaths.Clear();
        foreach (string recentPath in userSettings.RecentCodeplugPaths)
            recentCodeplugPaths.Add(recentPath);
        if (selectedChannel is not null &&
            !Systems.SelectMany(system => system.Channels).Contains(selectedChannel))
        {
            selectedChannel = null;
            selectedSystem = null;
            userSettings.LastSelectedSystemName = null;
            userSettings.LastSelectedChannelKey = null;
        }
        PersistUserSettings();
    }

    private static string GetDefaultRecordingRoot(string? configuredRootPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredRootPath))
            return Path.GetFullPath(configuredRootPath.Trim());

        string settingsPath = UserSettingsStore.DefaultPath;
        string? settingsDirectory = Path.GetDirectoryName(settingsPath);
        return Path.Combine(settingsDirectory ?? AppContext.BaseDirectory, "Recordings");
    }
}

public sealed class SystemViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly FneConnection connection;
    private readonly FneConnectionOptions options;
    private string connectionStatus = "Disconnected";
    private readonly HashSet<(byte AlgorithmId, ushort KeyId)> requestedP25Keys = [];
    private long receivedPacketCount;
    private long receivedPacketBytes;
    private long sentPacketCount;
    private long sentPacketBytes;
    private long nonCallDmrTerminatorCount;
    private string lastPacketText = "No media packets received.";
    private bool isSelected;

    public SystemViewModel(
        FneConnectionOptions options,
        string name,
        string endpoint,
        IEnumerable<ChannelViewModel>? channels = null,
        IEnumerable<ZoneViewModel>? zones = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        connection = new FneConnection(this.options);
        Name = name;
        Endpoint = endpoint;
        Channels = channels?.ToArray() ?? [];
        Zones = zones?.ToArray() ?? [];
        connection.StatusChanged += HandleConnectionStatus;
        connection.LogReceived += HandleLogReceived;
        connection.TrafficReceived += HandleTrafficReceived;
        connection.KeyResponseReceived += HandleKeyResponse;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<FneConnectionStatus>? StatusChanged;
    public event EventHandler<FneLogEntry>? LogReceived;
    public event EventHandler<FneTrafficFrame>? TrafficReceived;
    public event EventHandler<FneKeyResponse>? KeyResponseReceived;
    public string Name { get; }
    public string Endpoint { get; }
    public IReadOnlyList<ChannelViewModel> Channels { get; }
    public IReadOnlyList<ZoneViewModel> Zones { get; }
    public uint? SourceId => options.SourceId;
    public string Identity => options.Identity;
    public bool IsConnected => connection.Status.State == FneConnectionState.Connected;
    public bool IsConnectionActive => connection.Status.State is not (FneConnectionState.Disconnected or FneConnectionState.Faulted);
    public bool IsSelected => isSelected;
    public string ConnectionPillText => connection.Status.State.ToString().ToUpperInvariant();
    public string ConnectionActionText => IsConnectionActive ? $"Disconnect {Name}" : $"Start {Name}";
    public IBrush ConnectionBrush => new SolidColorBrush(Color.Parse(connection.Status.State switch
    {
        FneConnectionState.Connected => "#00BE5A",
        FneConnectionState.Starting or
        FneConnectionState.WaitingForLogin or
        FneConnectionState.Authenticating or
        FneConnectionState.Configuring or
        FneConnectionState.Stopping => "#E5A93C",
        FneConnectionState.Faulted => "#E05252",
        _ => "#8794A1"
    }));
    public string SystemTabText => $"{Name} {(ConnectionStatus.StartsWith("Connected:", StringComparison.OrdinalIgnoreCase) ? "●" : "○")}";
    public string PacketDiagnosticsText
        => $"RX {receivedPacketCount:N0} packets / {receivedPacketBytes:N0} bytes · TX {sentPacketCount:N0} packets / {sentPacketBytes:N0} bytes" +
            (nonCallDmrTerminatorCount > 0
                ? $" · non-call DMR terminators {nonCallDmrTerminatorCount:N0}"
                : string.Empty);
    public string LastPacketText => lastPacketText;
    public string ConnectionStatus
    {
        get => connectionStatus;
        private set
        {
            if (connectionStatus == value)
                return;
            connectionStatus = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionStatus)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsConnected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsConnectionActive)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionPillText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionActionText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SystemTabText)));
        }
    }

    internal void SetSelected(bool selected)
    {
        if (isSelected == selected)
            return;
        isSelected = selected;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ResetPacketDiagnostics();
        await connection.StartOrReconnectAsync(cancellationToken).ConfigureAwait(false);
    }
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await connection.StopAsync(cancellationToken).ConfigureAwait(false);
        requestedP25Keys.Clear();
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);
        await StartAsync(cancellationToken).ConfigureAwait(false);
    }
    public uint CreateStreamId() => connection.CreateStreamId();
    public void SendTraffic(FneTrafficProtocol protocol, ReadOnlySpan<byte> payload, ushort packetSequence, uint streamId)
    {
        connection.SendTraffic(protocol, payload, packetSequence, streamId);
        sentPacketCount++;
        sentPacketBytes += payload.Length;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PacketDiagnosticsText)));
    }

    public void RequestP25Key(byte algorithmId, ushort keyId)
    {
        if (!requestedP25Keys.Add((algorithmId, keyId)))
            return;

        try
        {
            connection.RequestP25Key(algorithmId, keyId);
        }
        catch
        {
            requestedP25Keys.Remove((algorithmId, keyId));
            throw;
        }
    }

    public void SendP25SubscriberCommand(P25SubscriberCommand command, uint destinationId)
        => connection.SendP25SubscriberCommand(command, destinationId);

    public void ApplyStatus(FneConnectionStatus status)
    {
        ConnectionStatus = $"{status.State}: {status.Message}";
    }

    internal void RecordTraffic(FneTrafficFrame traffic)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        receivedPacketCount++;
        receivedPacketBytes += traffic.Payload.Length;
        lastPacketText = $"{traffic.Protocol.ToString().ToUpperInvariant()} {traffic.CallType}/{traffic.FrameType} · seq {traffic.PacketSequence} · stream {traffic.StreamId} · {traffic.SourceId}→{traffic.DestinationId}";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PacketDiagnosticsText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastPacketText)));
    }

    internal void RecordNonCallDmrTerminator()
    {
        nonCallDmrTerminatorCount++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PacketDiagnosticsText)));
    }

    public async ValueTask DisposeAsync()
    {
        connection.StatusChanged -= HandleConnectionStatus;
        connection.LogReceived -= HandleLogReceived;
        connection.TrafficReceived -= HandleTrafficReceived;
        connection.KeyResponseReceived -= HandleKeyResponse;
        await connection.DisposeAsync().ConfigureAwait(false);
    }

    private void HandleConnectionStatus(object? sender, FneConnectionStatus status)
    {
        StatusChanged?.Invoke(this, status);
    }

    private void HandleLogReceived(object? sender, FneLogEntry entry)
    {
        LogReceived?.Invoke(this, entry);
    }

    private void HandleTrafficReceived(object? sender, FneTrafficFrame traffic)
    {
        TrafficReceived?.Invoke(this, traffic);
    }

    private void ResetPacketDiagnostics()
    {
        receivedPacketCount = 0;
        receivedPacketBytes = 0;
        sentPacketCount = 0;
        sentPacketBytes = 0;
        nonCallDmrTerminatorCount = 0;
        lastPacketText = "No media packets received.";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PacketDiagnosticsText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastPacketText)));
    }

    private void HandleKeyResponse(object? sender, FneKeyResponse response)
    {
        KeyResponseReceived?.Invoke(this, response);
    }
}

public sealed class ZoneViewModel : INotifyPropertyChanged
{
    private bool darkMode;

    public ZoneViewModel(
        string name,
        IReadOnlyList<ChannelViewModel> channels,
        IReadOnlyList<WebStreamViewModel> webStreams,
        string? tabColor = null,
        string? tabTextColor = null)
    {
        Name = name;
        Channels = channels;
        WebStreams = webStreams;
        TabColor = tabColor;
        TabTextColor = tabTextColor;
        foreach (ChannelViewModel channel in Channels)
            channel.PropertyChanged += HandleChannelPropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Name { get; }
    public IReadOnlyList<ChannelViewModel> Channels { get; }
    public IReadOnlyList<WebStreamViewModel> WebStreams { get; }
    public string? TabColor { get; }
    public string? TabTextColor { get; }
    public IBrush TabBrush => CreateBrush(TabColor, darkMode ? "#151D26" : "#E8EDF3");
    public IBrush TabTextBrush => CreateBrush(TabTextColor, darkMode ? "#DCE3EB" : "#18212B");
    private double widgetCardHeight = 122;
    public double WidgetCanvasWidth => Math.Max(1, Channels.Count == 0 ? 0 : Channels.Max(channel => channel.WidgetX + channel.CardWidth + 12));
    public double WidgetCanvasHeight => Math.Max(1, Channels.Count == 0 ? 0 : Channels.Max(channel => channel.WidgetY + widgetCardHeight + 12));

    public void SetWidgetCardHeight(double height)
    {
        if (Math.Abs(widgetCardHeight - height) < 0.001)
            return;
        widgetCardHeight = height;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WidgetCanvasHeight)));
    }

    public void RefreshWidgetCanvasBounds()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WidgetCanvasWidth)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WidgetCanvasHeight)));
    }

    public void SetDarkMode(bool enabled)
    {
        if (darkMode == enabled)
            return;
        darkMode = enabled;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TabBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TabTextBrush)));
    }

    private void HandleChannelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChannelViewModel.WidgetX) or nameof(ChannelViewModel.WidgetY))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WidgetCanvasWidth)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WidgetCanvasHeight)));
        }
    }

    private static IBrush CreateBrush(string? color, string fallback)
    {
        try
        {
            return new SolidColorBrush(Color.Parse(
                string.IsNullOrWhiteSpace(color) ? fallback : color.Trim()));
        }
        catch (FormatException)
        {
            return new SolidColorBrush(Color.Parse(fallback));
        }
    }
}

public sealed record AudioDeviceOptionViewModel(string Id, string Name, bool IsDefault)
{
    public string DisplayName => IsDefault ? $"{Name} (default)" : Name;
}

public sealed class ChannelViewModel : INotifyPropertyChanged
{
    private readonly ChannelConfiguration configuration;
    private readonly ChannelRuntime runtime;
    private readonly IP25KeyResolver? p25KeyResolver;
    private readonly IReadOnlyList<RadioAlias> aliases;
    private Func<ChannelViewModel, Task>? startAudio;
    private Func<ChannelViewModel, Task>? stopAudio;
    private Func<ChannelViewModel, Task>? startTransmit;
    private Func<ChannelViewModel, Task>? stopTransmit;
    private bool audioEnabled;
    private bool audioSuspended;
    private bool audioBusy;
    private bool transmitEnabled;
    private bool transmitSelected;
    private bool pageSelected;
    private bool alertSelected;
    private bool transmitBusy;
    private bool transmitEncrypted;
    private bool recordingEnabled;
    private string lastCallerText = "--";
    private double audioLevel;
    private double volume = 1.0;
    private string ignoredSubscriberIdsText = string.Empty;
    private string outputDeviceIdText = string.Empty;
    private IReadOnlyList<AudioDeviceOptionViewModel> outputDeviceOptions = [];
    private double widgetX;
    private double widgetY;
    private bool darkMode;

    public ChannelViewModel(
        ChannelConfiguration configuration,
        IP25KeyResolver? p25KeyResolver = null,
        IEnumerable<RadioAlias>? aliases = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        this.configuration = configuration;
        this.p25KeyResolver = p25KeyResolver;
        this.aliases = aliases?.ToArray() ?? [];
        runtime = new ChannelRuntime(ChannelRuntimeDefinition.FromConfiguration(configuration));
        transmitEncrypted = runtime.Definition.IsEncrypted;
        runtime.PropertyChanged += HandleRuntimePropertyChanged;
        AudioCommand = new AsyncRelayCommand(() => Task.CompletedTask, () => false);
        PttCommand = new AsyncRelayCommand(() => Task.CompletedTask, () => false);
        EncryptionCommand = new AsyncRelayCommand(ToggleEncryptionAsync, () => CanToggleEncryption && !transmitBusy && !audioBusy);
        RecordingCommand = new AsyncRelayCommand(ToggleRecordingAsync, () => CanRecord);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<bool>? TransmitEncryptionChanged;
    public event EventHandler<bool>? RecordingStateChanged;
    public event EventHandler<double>? VolumeChanged;

    public string Name => runtime.Definition.Name;
    public string SettingsKey => $"{runtime.Definition.SystemName}\u001F{runtime.Definition.Name}";
    public string ModeText => runtime.Definition.Mode.ToUpperInvariant();
    public string TalkgroupText => $"TG {runtime.Definition.DestinationId} - {ModeText}";
    public string DestinationText => $"{runtime.Definition.SystemName} / TGID {runtime.Definition.DestinationId}";
    public string LastCallerText => lastCallerText;
    public string LastCallerDisplayText => $"Last: {lastCallerText}";
    public double AudioLevel => audioLevel;
    public double CardWidth => (configuration.CardSize ?? "normal").Trim().ToLowerInvariant() switch
    {
        "small" => 180,
        "large" => 330,
        _ => 235
    };
    public double WidgetX => widgetX;
    public double WidgetY => widgetY;
    public IBrush CardBackgroundBrush => runtime.State switch
    {
        ChannelRuntimeState.Receiving => new SolidColorBrush(Color.Parse("#008A3A")),
        ChannelRuntimeState.Transmitting => new SolidColorBrush(Color.Parse("#0B6B9C")),
        _ when audioEnabled => new SolidColorBrush(Color.Parse(darkMode ? "#1B2B22" : "#E2F3E8")),
        _ => new SolidColorBrush(Color.Parse(darkMode ? "#151D26" : "#FFFFFF"))
    };
    public IBrush CardBorderBrush => runtime.State switch
    {
        ChannelRuntimeState.Receiving => new SolidColorBrush(Color.Parse("#00C86A")),
        ChannelRuntimeState.Transmitting => new SolidColorBrush(Color.Parse("#2497D3")),
        _ when audioEnabled => new SolidColorBrush(Color.Parse("#4E8060")),
        _ => CreateBrush(configuration.ResourceColor, darkMode ? "#2A3A4B" : "#9BA8B5")
    };
    public IBrush CardTextBrush => new SolidColorBrush(Color.Parse(
        runtime.State is ChannelRuntimeState.Receiving or ChannelRuntimeState.Transmitting
            ? "#FFFFFF"
            : darkMode ? "#DCE3EB" : "#18212B"));
    public string StateText
    {
        get
        {
            if (audioSuspended && runtime.State != ChannelRuntimeState.Transmitting)
                return "RX muted during console transmit";

            if (runtime.State == ChannelRuntimeState.Receiving && runtime.SourceId is uint sourceId)
            {
                string alias = AliasFileLoader.FindAlias(aliases, sourceId);
                if (!string.IsNullOrWhiteSpace(alias))
                    return $"Receiving from {alias} ({sourceId}) (stream {runtime.StreamId})";
            }

            return runtime.StateText;
        }
    }
    public ChannelRuntimeState State => runtime.State;
    public uint? SourceId => runtime.SourceId;
    public uint? StreamId => runtime.StreamId;
    public ChannelRuntimeDefinition Definition => runtime.Definition;
    public bool IsAudioEnabled => audioEnabled;
    public bool IsAudioSuspended => audioSuspended;
    public string AudioButtonText => audioSuspended ? "RX muted" : audioEnabled ? "Stop audio" : "Listen";
    public bool IsTransmitting => transmitEnabled;
    public bool IsTransmitSelected => transmitSelected;
    public bool IsPageSelected => pageSelected;
    public bool IsAlertSelected => alertSelected;
    public bool IsTransmitEncrypted => transmitEncrypted;
    public bool IsRecordingEnabled => recordingEnabled;
    public string RecordButtonText => "TAR";
    public string RecordingConfigurationButtonText => recordingEnabled ? "Disable TAR" : "Enable TAR";
    public double Volume
    {
        get => volume;
        set => SetVolume(value, raiseChanged: true);
    }
    public string OutputDeviceIdText
    {
        get => outputDeviceIdText;
        set
        {
            string normalized = value ?? string.Empty;
            if (outputDeviceIdText == normalized)
                return;
            outputDeviceIdText = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OutputDeviceIdText)));
        }
    }
    public IReadOnlyList<AudioDeviceOptionViewModel> OutputDeviceOptions => outputDeviceOptions;
    public AudioDeviceOptionViewModel? SelectedOutputDevice
    {
        get => ResolveOutputDevice();
        set
        {
            if (value is not null)
                OutputDeviceIdText = value.Id;
        }
    }
    public string IgnoredSubscriberIdsText
    {
        get => ignoredSubscriberIdsText;
        set
        {
            if (ignoredSubscriberIdsText == value)
                return;
            ignoredSubscriberIdsText = value ?? string.Empty;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IgnoredSubscriberIdsText)));
        }
    }
    public bool CanRecord => runtime.Definition.Mode is ("dmr" or "p25" or "analog") && CanListen;
    public bool CanToggleEncryption =>
        runtime.Definition.Mode == "p25" &&
        runtime.Definition.IsEncrypted &&
        runtime.Definition.SelectableEncryption &&
        (p25KeyResolver?.CanResolve(
            runtime.Definition.EncryptionAlgorithm,
            runtime.Definition.EncryptionKeyId) ?? false);
    public string EncryptionStatusText => !runtime.Definition.IsEncrypted
        ? "Clear"
        : p25KeyResolver?.CanResolve(
            runtime.Definition.EncryptionAlgorithm,
            runtime.Definition.EncryptionKeyId) == true
            ? "Key available"
            : "Key unavailable";
    public string EncryptionButtonText => transmitEncrypted ? "Secure" : "Clear";
    public bool CanListen => runtime.Definition.Mode switch
    {
        "dmr" or "analog" => !runtime.Definition.IsEncrypted,
        "p25" => !runtime.Definition.IsEncrypted ||
                 (p25KeyResolver?.CanResolve(
                     runtime.Definition.EncryptionAlgorithm,
                     runtime.Definition.EncryptionKeyId) ?? false),
        _ => false
    };
    public bool CanTransmit =>
        !runtime.Definition.RxOnly &&
        runtime.Definition.Mode switch
        {
            "dmr" or "analog" => !runtime.Definition.IsEncrypted,
            "p25" => !runtime.Definition.IsEncrypted ||
                     (p25KeyResolver?.CanResolve(
                         runtime.Definition.EncryptionAlgorithm,
                         runtime.Definition.EncryptionKeyId) ?? false),
            _ => false
        };
    public string PttButtonText => transmitEnabled ? "Release" : "PTT";
    public string TransmitSelectionText => "TX";
    public string PageSelectionText => "PAGE";
    public string AlertSelectionText => "ALERT";
    public IBrush TransmitSelectionBrush => new SolidColorBrush(Color.Parse(
        transmitSelected
            ? darkMode ? "#694BB0" : "#D7C9F2"
            : darkMode ? "#242938" : "#E8EDF3"));
    public IBrush TransmitSelectionBorderBrush => new SolidColorBrush(Color.Parse(
        transmitSelected
            ? darkMode ? "#B69AF4" : "#7655B8"
            : darkMode ? "#3A4555" : "#8996A3"));
    public IBrush PageSelectionBrush => new SolidColorBrush(Color.Parse(
        pageSelected
            ? darkMode ? "#A15B2A" : "#F2D1B8"
            : darkMode ? "#242938" : "#E8EDF3"));
    public IBrush PageSelectionBorderBrush => new SolidColorBrush(Color.Parse(
        pageSelected
            ? darkMode ? "#F0A15C" : "#A95C26"
            : darkMode ? "#3A4555" : "#8996A3"));
    public IBrush AlertSelectionBrush => new SolidColorBrush(Color.Parse(
        alertSelected
            ? darkMode ? "#8A3D68" : "#F0C7DE"
            : darkMode ? "#242938" : "#E8EDF3"));
    public IBrush AlertSelectionBorderBrush => new SolidColorBrush(Color.Parse(
        alertSelected
            ? darkMode ? "#E58BBC" : "#A84479"
            : darkMode ? "#3A4555" : "#8996A3"));
    public IBrush RecordingSelectionBrush => new SolidColorBrush(Color.Parse(
        recordingEnabled
            ? darkMode ? "#8A3A3A" : "#F2CCCC"
            : darkMode ? "#242938" : "#E8EDF3"));
    public IBrush RecordingSelectionBorderBrush => new SolidColorBrush(Color.Parse(
        recordingEnabled
            ? darkMode ? "#E58A8A" : "#A84343"
            : darkMode ? "#3A4555" : "#8996A3"));
    public ICommand AudioCommand { get; private set; }
    public ICommand PttCommand { get; private set; }
    public ICommand EncryptionCommand { get; }
    public ICommand RecordingCommand { get; }

    public void ConfigureAudio(
        Func<ChannelViewModel, Task> start,
        Func<ChannelViewModel, Task> stop)
    {
        startAudio = start ?? throw new ArgumentNullException(nameof(start));
        stopAudio = stop ?? throw new ArgumentNullException(nameof(stop));
        AudioCommand = new AsyncRelayCommand(ToggleAudioAsync, () => CanListen && !audioBusy);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AudioCommand)));
    }

    public void ConfigureTransmit(
        Func<ChannelViewModel, Task> start,
        Func<ChannelViewModel, Task> stop)
    {
        startTransmit = start ?? throw new ArgumentNullException(nameof(start));
        stopTransmit = stop ?? throw new ArgumentNullException(nameof(stop));
        PttCommand = new AsyncRelayCommand(ToggleTransmitAsync, () => CanTransmit && !transmitBusy);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PttCommand)));
        (EncryptionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    public void RefreshEncryptionState()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanListen)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanTransmit)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanToggleEncryption)));
        (AudioCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (PttCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (EncryptionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RecordingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    public void RestoreTransmitEncryption(bool encrypted)
    {
        if (!runtime.Definition.IsEncrypted || !runtime.Definition.SelectableEncryption)
            return;

        transmitEncrypted = encrypted;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTransmitEncrypted)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncryptionButtonText)));
        (PttCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (EncryptionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    public void SetRecordingEnabled(bool enabled)
        => SetRecordingEnabledCore(enabled, raiseStateChanged: true);

    public void RestoreRecordingEnabled(bool enabled)
        => SetRecordingEnabledCore(enabled, raiseStateChanged: false);

    private void SetRecordingEnabledCore(bool enabled, bool raiseStateChanged)
    {
        if (recordingEnabled == enabled)
            return;

        recordingEnabled = enabled;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRecordingEnabled)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecordButtonText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecordingConfigurationButtonText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecordingSelectionBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecordingSelectionBorderBrush)));
        if (raiseStateChanged)
            RecordingStateChanged?.Invoke(this, enabled);
        (RecordingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    public void RestoreVolume(double value)
        => SetVolume(value, raiseChanged: false);

    public void RestoreOutputDeviceId(string? deviceId)
        => OutputDeviceIdText = deviceId?.Trim() ?? string.Empty;

    public void SetOutputDeviceOptions(IReadOnlyList<AudioDeviceOptionViewModel> options)
    {
        outputDeviceOptions = options ?? [];
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OutputDeviceOptions)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedOutputDevice)));
    }

    public void RefreshOutputDeviceSelection()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedOutputDevice)));

    private AudioDeviceOptionViewModel? ResolveOutputDevice()
    {
        return outputDeviceOptions.FirstOrDefault(device =>
                   !string.IsNullOrWhiteSpace(OutputDeviceIdText) &&
                   device.Id.Equals(OutputDeviceIdText, StringComparison.OrdinalIgnoreCase)) ??
               outputDeviceOptions.FirstOrDefault(device => device.IsDefault) ??
               outputDeviceOptions.FirstOrDefault();
    }

    private void SetVolume(double value, bool raiseChanged)
    {
        double normalized = double.IsFinite(value) ? Math.Clamp(value, 0, 4) : 1.0;
        if (Math.Abs(volume - normalized) < 0.0001)
            return;

        volume = normalized;
        if (raiseChanged)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Volume)));
            VolumeChanged?.Invoke(this, normalized);
        }
    }

    public void SetIgnoredSubscriberIds(IEnumerable<uint> subscriberIds)
    {
        ArgumentNullException.ThrowIfNull(subscriberIds);
        IgnoredSubscriberIdsText = string.Join(", ", subscriberIds.Where(id => id != 0).Distinct().OrderBy(id => id));
    }

    public void SetAudioEnabled(bool enabled)
    {
        bool suspensionChanged = audioSuspended;
        audioSuspended = false;
        if (audioEnabled == enabled && !suspensionChanged)
            return;
        audioEnabled = enabled;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAudioEnabled)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAudioSuspended)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AudioButtonText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StateText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardBackgroundBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardBorderBrush)));
        if (!enabled)
            SetAudioLevel(0);
    }

    public void SetAudioSuspended(bool suspended)
    {
        if (!audioEnabled || audioSuspended == suspended)
            return;
        audioSuspended = suspended;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAudioSuspended)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AudioButtonText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StateText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardBackgroundBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardBorderBrush)));
        if (suspended)
            SetAudioLevel(0);
    }

    public void SetAudioLevel(double value, ChannelAudioDirection? direction = null)
    {
        double normalized = double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;
        if ((direction == ChannelAudioDirection.Receive && runtime.State != ChannelRuntimeState.Receiving) ||
            (direction == ChannelAudioDirection.Transmit && runtime.State != ChannelRuntimeState.Transmitting))
        {
            normalized = 0;
        }
        if (Math.Abs(audioLevel - normalized) < 0.01)
            return;

        audioLevel = normalized;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AudioLevel)));
    }

    public void SetTransmitEnabled(bool enabled, uint streamId = 0)
    {
        if (enabled)
        {
            if (streamId == 0)
                throw new ArgumentOutOfRangeException(nameof(streamId));
            runtime.MarkTransmitting(streamId);
        }
        else
        {
            runtime.MarkIdle();
        }

        if (transmitEnabled == enabled)
        {
            (EncryptionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            return;
        }
        transmitEnabled = enabled;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTransmitting)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PttButtonText)));
        (EncryptionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    public void SetTransmitSelected(bool selected)
    {
        if (transmitSelected == selected)
            return;
        transmitSelected = selected;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTransmitSelected)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TransmitSelectionText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TransmitSelectionBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TransmitSelectionBorderBrush)));
    }

    public void SetPageSelected(bool selected)
    {
        if (pageSelected == selected)
            return;
        pageSelected = selected;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPageSelected)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PageSelectionText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PageSelectionBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PageSelectionBorderBrush)));
    }

    public void SetAlertSelected(bool selected)
    {
        if (alertSelected == selected)
            return;
        alertSelected = selected;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAlertSelected)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AlertSelectionText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AlertSelectionBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AlertSelectionBorderBrush)));
    }

    public void RestoreTransmitSelection(bool selected) => SetTransmitSelected(selected);

    public void SetWidgetPosition(double x, double y)
    {
        double nextX = double.IsFinite(x) ? Math.Clamp(x, 0, 10_000) : 0;
        double nextY = double.IsFinite(y) ? Math.Clamp(y, 0, 10_000) : 0;
        if (Math.Abs(widgetX - nextX) >= 0.01)
        {
            widgetX = nextX;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WidgetX)));
        }
        if (Math.Abs(widgetY - nextY) >= 0.01)
        {
            widgetY = nextY;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WidgetY)));
        }
    }

    public void SetDarkMode(bool enabled)
    {
        if (darkMode == enabled)
            return;
        darkMode = enabled;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardBackgroundBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardBorderBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardTextBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TransmitSelectionBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TransmitSelectionBorderBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PageSelectionBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PageSelectionBorderBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AlertSelectionBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AlertSelectionBorderBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecordingSelectionBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecordingSelectionBorderBrush)));
    }

    public bool TryApplyTraffic(string systemName, FneTrafficFrame traffic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemName);
        ArgumentNullException.ThrowIfNull(traffic);

        if (!runtime.Definition.SystemName.Equals(systemName, StringComparison.OrdinalIgnoreCase) ||
            !MatchesProtocol(traffic.Protocol) ||
            traffic.StreamId == 0)
        {
            return false;
        }

        if (runtime.State == ChannelRuntimeState.Transmitting)
            return false;

        if (IsTerminator(traffic))
        {
            if (runtime.StreamId != traffic.StreamId)
                return false;

            runtime.MarkIdle();
            return true;
        }

        if (IsDmrPrivacyHeader(traffic))
        {
            return runtime.State == ChannelRuntimeState.Receiving &&
                runtime.StreamId == traffic.StreamId &&
                runtime.SourceId == traffic.SourceId &&
                runtime.Definition.DestinationId == traffic.DestinationId &&
                runtime.Definition.Slot == traffic.Slot;
        }

        if (traffic.DestinationId != runtime.Definition.DestinationId)
            return false;

        if (!MatchesVoiceTraffic(traffic) || traffic.SourceId == 0)
            return false;

        runtime.MarkReceiving(traffic.SourceId, traffic.StreamId);
        return true;
    }

    public bool TryExpireReceiveState(DateTimeOffset now, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (runtime.State != ChannelRuntimeState.Receiving ||
            runtime.LastActivity is not DateTimeOffset lastActivity ||
            now - lastActivity <= timeout)
        {
            return false;
        }

        runtime.MarkIdle(now);
        return true;
    }

    private bool MatchesVoiceTraffic(FneTrafficFrame traffic)
    {
        return runtime.Definition.Mode switch
        {
            "dmr" => traffic.Slot == runtime.Definition.Slot &&
                     IsVoiceFrame(traffic.FrameType),
            "p25" => IsVoiceFrame(traffic.FrameType) &&
                     (traffic.Subtype.Equals("LDU1", StringComparison.OrdinalIgnoreCase) ||
                      traffic.Subtype.Equals("LDU2", StringComparison.OrdinalIgnoreCase)),
            "nxdn" => IsVoiceFrame(traffic.FrameType),
            "analog" => IsVoiceFrame(traffic.FrameType),
            _ => false
        };
    }

    private bool MatchesProtocol(FneTrafficProtocol protocol)
    {
        return runtime.Definition.Mode switch
        {
            "dmr" => protocol == FneTrafficProtocol.Dmr,
            "p25" => protocol == FneTrafficProtocol.P25,
            "nxdn" => protocol == FneTrafficProtocol.Nxdn,
            "analog" => protocol == FneTrafficProtocol.Analog,
            _ => false
        };
    }

    private static bool IsTerminator(FneTrafficFrame traffic)
    {
        if (traffic.FrameType.Equals("TERMINATOR", StringComparison.OrdinalIgnoreCase))
            return true;

        return traffic.Protocol switch
        {
            FneTrafficProtocol.Dmr => traffic.Subtype.Equals(
                "TERMINATOR_WITH_LC",
                StringComparison.OrdinalIgnoreCase),
            FneTrafficProtocol.P25 => traffic.Subtype.Equals("TDU", StringComparison.OrdinalIgnoreCase) ||
                                       traffic.Subtype.Equals("TDULC", StringComparison.OrdinalIgnoreCase),
            FneTrafficProtocol.Analog => traffic.Subtype.Equals("TERMINATOR", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool IsDmrPrivacyHeader(FneTrafficFrame traffic)
    {
        return traffic.Protocol == FneTrafficProtocol.Dmr &&
            traffic.FrameType.Equals("DATA_SYNC", StringComparison.OrdinalIgnoreCase) &&
            traffic.Subtype.Equals("VOICE_PI_HEADER", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVoiceFrame(string frameType)
    {
        return frameType.Equals("VOICE", StringComparison.OrdinalIgnoreCase) ||
            frameType.Equals("VOICE_SYNC", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ToggleAudioAsync()
    {
        if (startAudio is null || stopAudio is null)
            return;

        audioBusy = true;
        (AudioCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        try
        {
            if (audioEnabled)
                await stopAudio(this);
            else
                await startAudio(this);
        }
        finally
        {
            audioBusy = false;
            (AudioCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private async Task ToggleTransmitAsync()
    {
        if (startTransmit is null || stopTransmit is null)
            return;

        transmitBusy = true;
        (PttCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        try
        {
            if (transmitEnabled)
                await stopTransmit(this);
            else
                await startTransmit(this);
        }
        finally
        {
            transmitBusy = false;
            (PttCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private Task ToggleEncryptionAsync()
    {
        if (!CanToggleEncryption || transmitEnabled)
            return Task.CompletedTask;

        transmitEncrypted = !transmitEncrypted;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTransmitEncrypted)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncryptionButtonText)));
        TransmitEncryptionChanged?.Invoke(this, transmitEncrypted);
        (PttCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        return Task.CompletedTask;
    }

    private Task ToggleRecordingAsync()
    {
        if (!CanRecord && !recordingEnabled)
            return Task.CompletedTask;

        SetRecordingEnabled(!recordingEnabled);
        return Task.CompletedTask;
    }

    private void HandleRuntimePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (runtime.State == ChannelRuntimeState.Receiving && runtime.SourceId is uint sourceId)
        {
            string alias = AliasFileLoader.FindAlias(aliases, sourceId).Trim();
            lastCallerText = string.IsNullOrWhiteSpace(alias)
                ? sourceId.ToString(CultureInfo.InvariantCulture)
                : alias;
        }
        else if (runtime.State is not (ChannelRuntimeState.Receiving or ChannelRuntimeState.Transmitting))
        {
            SetAudioLevel(0);
        }
        PropertyChanged?.Invoke(this, args);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastCallerText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastCallerDisplayText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardBackgroundBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardBorderBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardTextBrush)));
    }

    private static IBrush CreateBrush(string? color, string fallback)
    {
        try
        {
            return new SolidColorBrush(Color.Parse(
                string.IsNullOrWhiteSpace(color) ? fallback : color.Trim()));
        }
        catch (FormatException)
        {
            return new SolidColorBrush(Color.Parse(fallback));
        }
    }
}

internal sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> execute;
    private readonly Func<bool> canExecute;
    private bool running;

    public AsyncRelayCommand(Func<Task> execute, Func<bool> canExecute)
    {
        this.execute = execute;
        this.canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !running && canExecute();

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
            return;
        running = true;
        RaiseCanExecuteChanged();
        try
        {
            await execute();
        }
        finally
        {
            running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

internal sealed class RelayCommand : ICommand
{
    private readonly Action execute;

    public RelayCommand(Action execute)
    {
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}
