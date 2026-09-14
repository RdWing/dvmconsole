// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DvmConsole.Core.Configuration;
using System.ComponentModel;

namespace DvmConsole.Presentation;

public sealed partial class ConfigurationStudioZonesView : UserControl
{
    private bool compactTouchHeight;
    private bool inspectorExpanded;
    private bool compactInspectorNavigation;
    private ConfigurationStudioViewModel? subscribedViewModel;
    private int queuedSelectionCommitVersion;
    private int queuedChannelScrollVersion;
    private bool narrowLayout;
    private bool stackedInspectorLayout;
    private bool phoneLayout;
    private bool responsiveLayoutInitialized;
    private Control? draggedCard;
    private IConfigurationChannelPreviewViewModel? draggedPreview;
    private Point dragOrigin;
    private double dragX;
    private double dragY;

    public ConfigurationStudioZonesView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyResponsiveLayout(Bounds.Width);
        DataContextChanged += (_, _) => SubscribeToViewModel();
        Loaded += (_, _) =>
        {
            SubscribeToViewModel();
            ApplyResponsiveLayout(Bounds.Width);
            SynchronizeDmrSlotVisibility();
            QueueSelectedChannelScroll();
        };
        Unloaded += (_, _) =>
        {
            queuedSelectionCommitVersion++;
            queuedChannelScrollVersion++;
            if (draggedCard is not null)
                ClearPreviewDrag();
            UnsubscribeFromViewModel();
        };
    }

    public void SetLiveLayoutVisible(bool visible)
        => this.FindControl<Border>("liveZoneLayoutDrawer")!.IsVisible = visible;

    public event EventHandler<ConfigurationStudioEditCommandEventArgs>? EditCommandRequested;

    private ConfigurationStudioViewModel? ViewModel => DataContext as ConfigurationStudioViewModel;

    public IReadOnlyList<ChannelConfiguration> GetSelectedChannelRows()
    {
        ListBox? list = this.FindControl<ListBox>(narrowLayout ? "narrowChannelList" : "channelList");
        return list?.SelectedItems?
            .OfType<ConfigurationChannelRow>()
            .Select(row => row.Channel)
            .Distinct()
            .ToArray() ?? [];
    }

    private void HandleInspectorNavigation(object? sender, RoutedEventArgs e)
    {
        inspectorExpanded = !inspectorExpanded;
        ApplyInspectorNavigation();
        if (compactInspectorNavigation && inspectorExpanded)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsLoaded || !compactInspectorNavigation || !inspectorExpanded) return;
                // The inspector also contains zone settings. Opening channel
                // details should put the selected channel fields in view.
                UpdateLayout();
                InspectorScroll.Offset = new Vector(0, ChannelSettingsHeading.Bounds.Top);
            }, DispatcherPriority.Background);
        }
    }

    private void ApplyInspectorNavigation()
    {
        bool compactNavigation = compactInspectorNavigation;
        bool expanded = compactNavigation && inspectorExpanded;
        OpenInspector.IsVisible = compactNavigation;
        InspectorNavigation.IsVisible = expanded;
        InspectorNavigation.Content = "‹ Channels";
        Avalonia.Automation.AutomationProperties.SetName(InspectorNavigation, "Return to channel list");
        ChannelPane.IsVisible = !expanded;
        Grid.SetRowSpan(ChannelPane, compactNavigation ? 2 : 1);
        ChannelInspector.IsVisible = !compactNavigation || expanded;
        Grid.SetRow(ChannelInspector, expanded ? 0 : stackedInspectorLayout ? 1 : 0);
        Grid.SetRowSpan(ChannelInspector, expanded ? 2 : 1);
    }

    private void HandleChannelRowTapped(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ConfigurationChannelRow row } || ViewModel is not { } model)
            return;
        model.SelectedChannelRow = row;
        if (!inspectorExpanded && compactInspectorNavigation)
            HandleInspectorNavigation(sender, e);
        e.Handled = true;
    }

    private void HandleTouchActions(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } model) return;
        var items = new StackPanel { Spacing = 4, MinWidth = 240 };
        var flyout = new Flyout
        {
            Content = new ScrollViewer
            {
                Content = items,
                MaxHeight = Math.Max(160, Bounds.Height * 0.7)
            }
        };
        flyout.FlyoutPresenterClasses.Add("studio-actions");
        void Group(string title) => items.Children.Add(new TextBlock
        {
            Classes = { "studio-action-heading" },
            Text = title,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            Margin = new Thickness(8, 12, 8, 4)
        });
        void Action(string label, ConfigurationStudioEditCommand command, bool enabled = true)
        {
            var button = new Button
            {
                Content = label,
                MinHeight = 44,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                IsEnabled = model.CanEdit && enabled
            };
            button.Classes.Add("studio-action");
            if (command is ConfigurationStudioEditCommand.DeleteChannel or ConfigurationStudioEditCommand.DeleteZone)
                button.Classes.Add("destructive");
            button.Click += (_, _) =>
            {
                flyout.Hide();
                EditCommandRequested?.Invoke(this, new(command, GetSelectedChannelRows()));
            };
            items.Children.Add(button);
        }
        bool selected = GetSelectedChannelRows().Count > 0;
        Group("Channels");
        Action("Add Channel", ConfigurationStudioEditCommand.AddChannel);
        Action("Duplicate Channel", ConfigurationStudioEditCommand.DuplicateChannel, selected);
        Action("Move Up", ConfigurationStudioEditCommand.MoveChannelUp, selected);
        Action("Move Down", ConfigurationStudioEditCommand.MoveChannelDown, selected);
        Action("Set RX Only", ConfigurationStudioEditCommand.SetSelectedRowsRxOnly, selected);
        Action("Allow Transmit", ConfigurationStudioEditCommand.SetSelectedRowsTxCapable, selected);
        Action("Apply Card Size", ConfigurationStudioEditCommand.ApplySelectedCardSize, selected);
        Action("Delete Channel…", ConfigurationStudioEditCommand.DeleteChannel, selected);
        Group("Zones");
        Action("Add Zone", ConfigurationStudioEditCommand.AddZone);
        Action("Duplicate Zone", ConfigurationStudioEditCommand.DuplicateZone);
        Action("Delete Zone…", ConfigurationStudioEditCommand.DeleteZone);
        flyout.ShowAt(TouchZoneActions);
    }

    private void HandleEditMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string commandName } &&
            Enum.TryParse(commandName, ignoreCase: false, out ConfigurationStudioEditCommand command))
        {
            EditCommandRequested?.Invoke(
                this,
                new ConfigurationStudioEditCommandEventArgs(command, GetSelectedChannelRows()));
        }
    }

    private void HandleDraftFieldEdit(object? sender, RoutedEventArgs e)
    {
        if (!IsLoaded || ViewModel is not { } viewModel)
            return;
        if (e is SelectionChangedEventArgs)
            QueueSelectionCommit(viewModel.CommitFieldEdit);
        else
            viewModel.CommitFieldEdit();
    }

    private void HandleInlineChannelEditorFocus(object? sender, GotFocusEventArgs e)
    {
        if (sender is Control { DataContext: ConfigurationChannelRow row } && ViewModel is { } viewModel)
            viewModel.SelectedChannelRow = row;
    }

    private void HandleInlineChannelFieldEdit(object? sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not Control { DataContext: ConfigurationChannelRow row } ||
            ViewModel is not { } viewModel)
        {
            return;
        }

        if (e is SelectionChangedEventArgs && sender is ComboBox selectionEditor &&
            !IsUserSelectionChange(selectionEditor))
            return;

        viewModel.SelectedChannelRow = row;
        if (e is SelectionChangedEventArgs)
            QueueSelectionCommit(viewModel.CommitFieldEdit);
        else
            viewModel.CommitFieldEdit();
    }

    private void HandleInlineChannelModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || sender is not ComboBox { DataContext: ConfigurationChannelRow row } selectionEditor ||
            !IsUserSelectionChange(selectionEditor) ||
            ViewModel is not { } viewModel)
        {
            return;
        }

        viewModel.SelectedChannelRow = row;
        QueueSelectionCommit(() =>
        {
            viewModel.CommitChannelModeEdit();
            SynchronizeDmrSlotVisibility();
        });
    }

    private void HandleInlineChannelAlgorithmChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || sender is not ComboBox { DataContext: ConfigurationChannelRow row } selectionEditor ||
            !IsUserSelectionChange(selectionEditor) ||
            ViewModel is not { } viewModel)
        {
            return;
        }

        viewModel.SelectedChannelRow = row;
        QueueSelectionCommit(viewModel.CommitChannelAlgorithmEdit);
    }

    private static bool IsUserSelectionChange(ComboBox editor)
        => editor.IsKeyboardFocusWithin || editor.IsDropDownOpen;

    private void HandleChannelModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && sender is ComboBox selectionEditor &&
            IsUserSelectionChange(selectionEditor) && ViewModel is { } viewModel)
        {
            QueueSelectionCommit(() =>
            {
                viewModel.CommitChannelModeEdit();
                SynchronizeDmrSlotVisibility();
            });
        }
    }

    private void HandleChannelAlgorithmChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && sender is ComboBox selectionEditor &&
            IsUserSelectionChange(selectionEditor) && ViewModel is { } viewModel)
            QueueSelectionCommit(viewModel.CommitChannelAlgorithmEdit);
    }

    private void HandleChannelSlotChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && sender is ComboBox selectionEditor &&
            IsUserSelectionChange(selectionEditor) && ViewModel is { } viewModel)
        {
            QueueSelectionCommit(viewModel.CommitFieldEdit);
        }
    }

    private void HandleZoneSystemChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && sender is ComboBox { SelectedItem: SystemConfiguration } selectionEditor &&
            IsUserSelectionChange(selectionEditor) &&
            ViewModel is { } viewModel)
        {
            QueueSelectionCommit(viewModel.CommitZoneSystemEdit);
        }
    }

    private void QueueSelectionCommit(Action commit)
    {
        int version = ++queuedSelectionCommitVersion;
        Dispatcher.UIThread.Post(() =>
        {
            if (IsLoaded && version == queuedSelectionCommitVersion)
                commit();
        }, DispatcherPriority.Background);
    }

    private void HandleCardSizeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string cardSize } && ViewModel is { SelectedChannel: { } channel } viewModel)
        {
            channel.CardSize = cardSize;
            viewModel.CommitFieldEdit();
        }
    }

    private void HandleResourceColorClick(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string color } && ViewModel is { SelectedChannel: { } channel } viewModel)
        {
            channel.ResourceColor = color;
            viewModel.CommitFieldEdit();
        }
    }

    private void HandlePreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: IConfigurationChannelPreviewViewModel preview } control ||
            !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed || ViewModel is not { } viewModel)
        {
            return;
        }

        draggedCard = control;
        draggedPreview = preview;
        dragOrigin = e.GetPosition(this);
        dragX = preview.X;
        dragY = preview.Y;
        viewModel.BeginPreviewMove();
        viewModel.SelectedChannel = preview.Channel;
        e.Pointer.Capture(control);
        e.Handled = true;
    }

    private void HandlePreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (draggedCard is null || draggedPreview is null ||
            !ReferenceEquals(sender, draggedCard) || ViewModel is not { } viewModel)
        {
            return;
        }

        Point current = e.GetPosition(this);
        viewModel.MovePreviewChannel(
            draggedPreview,
            dragX + (current.X - dragOrigin.X),
            dragY + (current.Y - dragOrigin.Y));
        e.Handled = true;
    }

    private void HandlePreviewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (draggedCard is null || !ReferenceEquals(sender, draggedCard))
            return;
        e.Pointer.Capture(null);
        ClearPreviewDrag();
        e.Handled = true;
    }

    private void HandlePreviewPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (draggedCard is not null && ReferenceEquals(sender, draggedCard))
            ClearPreviewDrag();
    }

    private void ClearPreviewDrag()
    {
        ViewModel?.CommitPreviewMove();
        draggedCard = null;
        draggedPreview = null;
    }

    private void SubscribeToViewModel()
    {
        ConfigurationStudioViewModel? current = IsLoaded ? ViewModel : null;
        if (ReferenceEquals(current, subscribedViewModel))
            return;
        UnsubscribeFromViewModel();
        subscribedViewModel = current;
        if (subscribedViewModel is not null)
            subscribedViewModel.PropertyChanged += HandleViewModelPropertyChanged;
    }

    private void UnsubscribeFromViewModel()
    {
        if (subscribedViewModel is not null)
            subscribedViewModel.PropertyChanged -= HandleViewModelPropertyChanged;
        subscribedViewModel = null;
    }

    private void HandleViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConfigurationStudioViewModel.SelectedChannelRow))
            QueueSelectedChannelScroll();
        if (e.PropertyName is nameof(ConfigurationStudioViewModel.SelectedChannel) or
            nameof(ConfigurationStudioViewModel.IsSelectedChannelDmr))
        {
            SynchronizeDmrSlotVisibility();
        }
    }

    private void SynchronizeDmrSlotVisibility()
    {
        if (this.FindControl<StackPanel>("dmrSlotSettings") is { } slotSettings)
        {
            slotSettings.SetCurrentValue(
                StackPanel.IsVisibleProperty,
                ViewModel?.IsSelectedChannelDmr == true);
        }
    }

    private void QueueSelectedChannelScroll()
    {
        int version = ++queuedChannelScrollVersion;
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsLoaded || version != queuedChannelScrollVersion ||
                ViewModel is not { IsZones: true, SelectedChannelRow: { } row })
            {
                return;
            }

            this.FindControl<ListBox>(narrowLayout ? "narrowChannelList" : "channelList")
                ?.ScrollIntoView(row);
        }, DispatcherPriority.Background);
    }

    private void ApplyResponsiveLayout(double width)
    {
        bool touchHost = this.GetVisualAncestors().OfType<ConfigurationStudioView>().FirstOrDefault()?.UseTouchLayout == true;
        ConfigurationStudioResponsiveLayout responsive =
            ConfigurationStudioResponsiveLayoutPolicy.Evaluate(width, Bounds.Height, touchHost);
        bool useDetailPages = touchHost && responsive.StackInspector;
        bool useNarrow = responsive.UsePhoneChannelList || touchHost && width < 860;
        bool stackInspector = responsive.StackInspector;
        bool useSingleColumn = width is > 0 and < ConfigurationStudioResponsiveLayoutPolicy.PhoneChannelListWidth;
        bool usePhone = responsive.UseTouchTargets;
        bool enteringNarrow = useNarrow && (!responsiveLayoutInitialized || !narrowLayout);
        bool layoutChanged = !responsiveLayoutInitialized ||
            compactInspectorNavigation != useDetailPages ||
            compactTouchHeight != responsive.UseCompactHeight ||
            useNarrow != narrowLayout ||
            stackInspector != stackedInspectorLayout ||
            usePhone != phoneLayout;

        Grid? layout = this.FindControl<Grid>("ZoneLayout");
        Grid? channelPane = this.FindControl<Grid>("ChannelPane");
        Border? inspector = this.FindControl<Border>("ChannelInspector");
        Grid? header = this.FindControl<Grid>("ZoneHeaderGrid");
        Border? headerBorder = this.FindControl<Border>("ZoneHeader");
        TextBlock? heading = this.FindControl<TextBlock>("ZoneHeading");
        TextBox? search = this.FindControl<TextBox>("ChannelSearchBox");
        Grid? menu = this.FindControl<Grid>("ZoneActionHost");
        Grid? desktopTable = this.FindControl<Grid>("DesktopChannelTable");
        ScrollViewer? desktopTableScroller = this.FindControl<ScrollViewer>("DesktopChannelTableScroller");
        ListBox? narrowList = this.FindControl<ListBox>("narrowChannelList");
        Border? liveLayout = this.FindControl<Border>("liveZoneLayoutDrawer");
        if (layout is null || channelPane is null || inspector is null || header is null ||
            headerBorder is null || heading is null || search is null || menu is null ||
            desktopTable is null || desktopTableScroller is null || narrowList is null || liveLayout is null)
        {
            return;
        }

        liveLayout.MaxHeight = responsive.PreviewMaximumHeight;
        if (!layoutChanged)
            return;

        if ((enteringNarrow || responsive.UseCompactHeight && !compactTouchHeight) && ViewModel is { } viewModel)
            viewModel.IsZonePreviewExpanded = false;

        IReadOnlyList<ChannelConfiguration> selectedChannels = GetSelectedChannelRows();
        narrowLayout = useNarrow;
        compactInspectorNavigation = useDetailPages;
        stackedInspectorLayout = stackInspector;
        phoneLayout = usePhone;
        compactTouchHeight = responsive.UseCompactHeight;
        responsiveLayoutInitialized = true;
        Classes.Set("phone", usePhone);
        Classes.Set("touch-host", touchHost);
        ZoneEditMenu.IsVisible = !touchHost;
        TouchZoneActions.IsVisible = touchHost;
        ApplyPhoneTouchTargets(usePhone, inspector, search);

        layout.ColumnDefinitions = new ColumnDefinitions(stackInspector ? "*" : "*,316");
        layout.RowDefinitions = new RowDefinitions(stackInspector
            ? responsive.UseCompactHeight ? "65*,35*" : "55*,45*" : "*");
        Grid.SetColumn(inspector, stackInspector ? 0 : 1);
        Grid.SetRow(inspector, stackInspector ? 1 : 0);
        inspector.BorderThickness = stackInspector ? new Thickness(0, 1, 0, 0) : new Thickness(1, 0, 0, 0);
        inspector.MinHeight = 0;

        channelPane.RowDefinitions = new RowDefinitions(
            useSingleColumn ? "92,*,Auto" : "55,*,Auto");
        header.ColumnDefinitions = new ColumnDefinitions(
            useSingleColumn ? "*,58" : "*,216,58");
        // A lone Auto row retains only its measured height inside the fixed
        // 55-pixel header. Centered controls would then be arranged against a
        // near-zero cell and clip above the page. Stretch the desktop row.
        header.RowDefinitions = new RowDefinitions(useSingleColumn ? "Auto,Auto" : "*");
        headerBorder.Padding = useSingleColumn ? new Thickness(12, 8) : new Thickness(20, 0, 12, 0);
        Grid.SetColumn(heading, 0);
        Grid.SetRow(heading, 0);
        Grid.SetColumn(menu, useSingleColumn ? 1 : 2);
        Grid.SetRow(menu, 0);
        Grid.SetColumn(search, useSingleColumn ? 0 : 1);
        Grid.SetRow(search, useSingleColumn ? 1 : 0);
        Grid.SetColumnSpan(search, useSingleColumn ? 2 : 1);
        search.Margin = useSingleColumn ? new Thickness(0, 8, 0, 0) : default;

        desktopTable.IsVisible = !useNarrow;
        desktopTableScroller.IsVisible = !useNarrow;
        narrowList.IsVisible = useNarrow;
        RestoreSelectedRows(selectedChannels);
        ApplyInspectorNavigation();
        QueueSelectedChannelScroll();
    }

    private void ApplyPhoneTouchTargets(bool usePhone, Border inspector, TextBox search)
    {
        IEnumerable<Control> controls = inspector.GetVisualDescendants()
            .OfType<Control>()
            .Prepend(search);
        foreach (Control control in controls)
        {
            switch (control)
            {
                case TextBox textBox:
                    textBox.MinHeight = usePhone
                        ? 44
                        : string.Equals(textBox.Name, "ChannelKeyIdEditor", StringComparison.Ordinal)
                            ? 32
                            : 34;
                    break;
                case ComboBox comboBox:
                    comboBox.MinHeight = usePhone ? 44 : 34;
                    break;
                case NumericUpDown numeric:
                    numeric.MinHeight = usePhone ? 44 : 34;
                    break;
                case CheckBox checkBox:
                    checkBox.MinHeight = usePhone ? 44 : 0;
                    break;
                case ToggleButton toggle when toggle.Classes.Contains("color-swatch"):
                    toggle.Width = usePhone ? 44 : 27;
                    toggle.Height = usePhone ? 44 : 27;
                    toggle.CornerRadius = new CornerRadius(usePhone ? 22 : 14);
                    break;
                case ToggleButton toggle:
                    toggle.MinHeight = usePhone ? 44 : 0;
                    break;
            }
        }
    }

    private void RestoreSelectedRows(IReadOnlyList<ChannelConfiguration> selectedChannels)
    {
        if (selectedChannels.Count == 0 || ViewModel is not { } viewModel)
            return;
        ListBox? list = this.FindControl<ListBox>(narrowLayout ? "narrowChannelList" : "channelList");
        if (list?.SelectedItems is not { } selectedItems)
            return;

        selectedItems.Clear();
        foreach (ConfigurationChannelRow row in viewModel.VisibleChannelRows.Where(row =>
                     selectedChannels.Contains(row.Channel)))
        {
            selectedItems.Add(row);
        }
    }
}
