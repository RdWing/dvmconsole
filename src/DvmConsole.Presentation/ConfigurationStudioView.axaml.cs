using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DvmConsole.Core.Configuration;

namespace DvmConsole.Presentation;

public sealed partial class ConfigurationStudioView : UserControl
{
    private const double NarrowWidth = 880;
    private const double PhoneWidth = 400;
    private bool responsiveLayoutInitialized;
    private bool narrowLayout;
    private bool phoneLayout;

    public ConfigurationStudioView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyResponsiveLayout(Bounds.Width);
        Loaded += (_, _) => ApplyResponsiveLayout(Bounds.Width);
    }

    public event EventHandler? DeleteSystemRequested;
    public event EventHandler<ConfigurationStudioEditCommandEventArgs>? EditCommandRequested;
    public event EventHandler? DeleteStreamRequested;
    public event EventHandler? DeleteGroupRequested;
    public event EventHandler<PatchGroupEventArgs>? ApplyGroupRequested;
    public event EventHandler? ApplyAllGroupsRequested;
    public event EventHandler? ApplyAllGroupsAndCloseRequested;
    public event EventHandler<PatchGroupEventArgs>? ToggleMultiSelectPttRequested;
    public event EventHandler? DeleteKeyRequested;
    public event EventHandler? DeleteAliasRequested;
    public event EventHandler? BrowseKeyFileRequested;
    public event EventHandler<ConfigurationStudioAliasFileEventArgs>? BrowseAliasFileRequested;
    public event EventHandler? ExportFullRequested;
    public event EventHandler? ExportSanitizedRequested;
    public event EventHandler? SaveCopyRequested;
    public event EventHandler? ReviewSaveRequested;

    public ConfigurationStudioZonesView ZonesView
        => this.FindControl<ConfigurationStudioZonesView>("zonesView")!;

    private ConfigurationStudioViewModel? ViewModel => DataContext as ConfigurationStudioViewModel;

    private void HandleSharedSectionNavigationRequested(
        object? sender,
        ConfigurationStudioSectionEventArgs e)
        => ViewModel?.SelectSection(e.Section);

    private void HandleToggleValidationDrawerClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.IsValidationDrawerOpen =
                !viewModel.IsValidationDrawerOpen && viewModel.HasValidationIssues;
        }
    }

    private void HandleValidationIssueClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ConfigurationValidationIssue issue })
            ViewModel?.NavigateToIssue(issue);
    }

    private void HandleUndoClick(object? sender, RoutedEventArgs e) => ViewModel?.Undo();
    private void HandleRedoClick(object? sender, RoutedEventArgs e) => ViewModel?.Redo();
    private void HandleSaveAsClick(object? sender, RoutedEventArgs e)
        => SaveCopyRequested?.Invoke(this, EventArgs.Empty);
    private void HandleReviewAndSaveClick(object? sender, RoutedEventArgs e)
        => ReviewSaveRequested?.Invoke(this, EventArgs.Empty);
    private void HandleExportFullClick(object? sender, RoutedEventArgs e)
        => ExportFullRequested?.Invoke(this, EventArgs.Empty);

    private void HandleSharedDeleteSystemRequested(object? sender, EventArgs e)
        => DeleteSystemRequested?.Invoke(this, EventArgs.Empty);
    private void HandleSharedStudioEditCommandRequested(
        object? sender,
        ConfigurationStudioEditCommandEventArgs e)
        => EditCommandRequested?.Invoke(this, e);
    private void HandleSharedDeleteStreamRequested(object? sender, EventArgs e)
        => DeleteStreamRequested?.Invoke(this, EventArgs.Empty);
    private void HandleSharedDeleteGroupRequested(object? sender, EventArgs e)
        => DeleteGroupRequested?.Invoke(this, EventArgs.Empty);
    private void HandleSharedApplyPatchGroupRequested(object? sender, PatchGroupEventArgs e)
        => ApplyGroupRequested?.Invoke(this, e);
    private void HandleSharedApplyAllOperatorGroupsRequested(object? sender, EventArgs e)
        => ApplyAllGroupsRequested?.Invoke(this, EventArgs.Empty);
    private void HandleSharedApplyOperatorGroupsAndCloseRequested(object? sender, EventArgs e)
        => ApplyAllGroupsAndCloseRequested?.Invoke(this, EventArgs.Empty);
    private void HandleSharedMultiSelectPttRequested(object? sender, PatchGroupEventArgs e)
        => ToggleMultiSelectPttRequested?.Invoke(this, e);
    private void HandleSharedDeleteKeyRequested(object? sender, EventArgs e)
        => DeleteKeyRequested?.Invoke(this, EventArgs.Empty);
    private void HandleSharedDeleteAliasRequested(object? sender, EventArgs e)
        => DeleteAliasRequested?.Invoke(this, EventArgs.Empty);
    private void HandleSharedBrowseKeyFileRequested(object? sender, EventArgs e)
        => BrowseKeyFileRequested?.Invoke(this, EventArgs.Empty);
    private void HandleSharedBrowseAliasFileRequested(
        object? sender,
        ConfigurationStudioAliasFileEventArgs e)
        => BrowseAliasFileRequested?.Invoke(this, e);
    private void HandleSharedExportFullRequested(object? sender, EventArgs e)
        => ExportFullRequested?.Invoke(this, EventArgs.Empty);
    private void HandleSharedExportSanitizedRequested(object? sender, EventArgs e)
        => ExportSanitizedRequested?.Invoke(this, EventArgs.Empty);

    private void ApplyResponsiveLayout(double width)
    {
        bool useNarrow = width > 0 && width < NarrowWidth;
        bool usePhone = width > 0 && width < PhoneWidth;
        if (responsiveLayoutInitialized && useNarrow == narrowLayout && usePhone == phoneLayout)
            return;

        Grid? shell = this.FindControl<Grid>("ShellLayout");
        Grid? workspace = this.FindControl<Grid>("Workspace");
        ConfigurationStudioNavigationView? navigation =
            this.FindControl<ConfigurationStudioNavigationView>("Navigation");
        Grid? pageHost = this.FindControl<Grid>("PageHost");
        Border? validationDrawer = this.FindControl<Border>("ValidationDrawer");
        Grid? footerGrid = this.FindControl<Grid>("FooterGrid");
        Button? validationToggle = this.FindControl<Button>("ValidationToggle");
        StackPanel? footerActions = this.FindControl<StackPanel>("FooterActions");
        if (shell is null || workspace is null || navigation is null || pageHost is null ||
            validationDrawer is null || footerGrid is null || validationToggle is null ||
            footerActions is null)
        {
            return;
        }

        responsiveLayoutInitialized = true;
        narrowLayout = useNarrow;
        phoneLayout = usePhone;
        shell.RowDefinitions = new RowDefinitions(useNarrow ? "45,*,Auto" : "45,*,74");
        workspace.ColumnDefinitions = new ColumnDefinitions(useNarrow ? "*" : "286,*");
        workspace.RowDefinitions = new RowDefinitions(useNarrow ? "220,*" : "*");
        Grid.SetColumn(pageHost, useNarrow ? 0 : 1);
        Grid.SetRow(pageHost, useNarrow ? 1 : 0);
        navigation.SetCompactLayout(useNarrow, usePhone);

        validationDrawer.Margin = useNarrow
            ? new Thickness(13, 0, 13, 0)
            : new Thickness(300, 0, 13, 0);
        footerGrid.ColumnDefinitions = new ColumnDefinitions(useNarrow ? "*" : "340,*");
        footerGrid.RowDefinitions = new RowDefinitions(useNarrow ? "Auto,Auto" : "*");
        Grid.SetColumn(footerActions, useNarrow ? 0 : 1);
        Grid.SetRow(footerActions, useNarrow ? 1 : 0);
        footerActions.Margin = useNarrow ? new Thickness(0, 8, 0, 0) : default;
        footerActions.HorizontalAlignment = useNarrow
            ? Avalonia.Layout.HorizontalAlignment.Left
            : Avalonia.Layout.HorizontalAlignment.Right;
        footerActions.Orientation = useNarrow
            ? Avalonia.Layout.Orientation.Vertical
            : Avalonia.Layout.Orientation.Horizontal;
        footerActions.Spacing = useNarrow ? 8 : 6;

        // Keep the footer actions measurable even when the desktop window is
        // first arranged at a constrained height. A local zero MinHeight can
        // collapse Fluent buttons inside the footer action panel.
        double minimumTouchHeight = usePhone ? 44 : 34;
        validationToggle.MinHeight = minimumTouchHeight;
        footerActions.MinHeight = minimumTouchHeight;
        foreach (Button button in footerActions.Children.OfType<Button>())
            button.MinHeight = minimumTouchHeight;
    }
}
