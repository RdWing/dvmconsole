using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DvmConsole.Presentation;

public sealed partial class ConfigurationStudioGroupsView : UserControl
{
    private const double NarrowWidth = 760;

    public ConfigurationStudioGroupsView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyResponsiveLayout(Bounds.Width);
    }

    public event EventHandler? DeleteRequested;
    public event EventHandler<PatchGroupEventArgs>? ApplyGroupRequested;
    public event EventHandler? ApplyAllRequested;
    public event EventHandler? ApplyAllAndCloseRequested;
    public event EventHandler<PatchGroupEventArgs>? ToggleMultiSelectPttRequested;

    private ConfigurationStudioViewModel? ViewModel => DataContext as ConfigurationStudioViewModel;

    private void HandleAddGroupClick(object? sender, RoutedEventArgs e) => ViewModel?.AddGroup();
    private void HandleDeleteGroupClick(object? sender, RoutedEventArgs e)
        => DeleteRequested?.Invoke(this, EventArgs.Empty);

    private void HandleDraftFieldEdit(object? sender, RoutedEventArgs e)
    {
        if (IsLoaded)
            ViewModel?.CommitFieldEdit();
    }

    private void HandleApplyPatchGroupClick(object? sender, RoutedEventArgs e)
        => PublishGroup(sender, ApplyGroupRequested);

    private void HandleMultiSelectPttClick(object? sender, RoutedEventArgs e)
        => PublishGroup(sender, ToggleMultiSelectPttRequested);

    private void HandleApplyAllOperatorGroupsClick(object? sender, RoutedEventArgs e)
        => ApplyAllRequested?.Invoke(this, EventArgs.Empty);

    private void HandleApplyOperatorGroupsAndCloseClick(object? sender, RoutedEventArgs e)
        => ApplyAllAndCloseRequested?.Invoke(this, EventArgs.Empty);

    private void PublishGroup(object? sender, EventHandler<PatchGroupEventArgs>? handler)
    {
        if (sender is Button { Tag: PatchGroupEditorViewModel group })
            handler?.Invoke(this, new PatchGroupEventArgs(group));
    }

    private void ApplyResponsiveLayout(double width)
    {
        Grid? body = this.FindControl<Grid>("GroupsBody");
        Border? operatorPanel = this.FindControl<Border>("GroupOperatorPanel");
        if (body is null || operatorPanel is null)
            return;

        bool narrow = width > 0 && width < NarrowWidth;
        body.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "2*,3*");
        body.RowDefinitions = new RowDefinitions(narrow ? "Auto,Auto" : "Auto");
        Grid.SetColumn(operatorPanel, narrow ? 0 : 1);
        Grid.SetRow(operatorPanel, narrow ? 1 : 0);
        operatorPanel.Margin = narrow ? new Avalonia.Thickness(0, 10, 0, 0) : default;
    }
}
