using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace DvmConsole.Presentation;

public sealed partial class ConfigurationStudioNavigationView : UserControl
{
    public ConfigurationStudioNavigationView()
    {
        InitializeComponent();
    }

    public event EventHandler<ConfigurationStudioSectionEventArgs>? SectionRequested;

    public void SetCompactLayout(bool compact, bool phone)
    {
        MaxHeight = compact ? 220 : double.PositiveInfinity;
        if (this.FindControl<Border>("NavigationBorder") is { } border)
        {
            border.BorderThickness = compact
                ? new Avalonia.Thickness(0, 0, 0, 1)
                : new Avalonia.Thickness(0, 0, 1, 0);
        }
        if (this.FindControl<TextBox>("NavigationSearch") is { } search)
            search.MinHeight = phone ? 44 : 34;
        foreach (Button button in this.GetVisualDescendants().OfType<Button>())
            button.MinHeight = phone ? 44 : 0;
    }

    private void HandleOverviewClick(object? sender, RoutedEventArgs e)
        => Publish(ConfigurationStudioSection.Overview);
    private void HandleSystemsClick(object? sender, RoutedEventArgs e)
        => Publish(ConfigurationStudioSection.Systems);
    private void HandleStreamsClick(object? sender, RoutedEventArgs e)
        => Publish(ConfigurationStudioSection.Streams);
    private void HandleGroupsClick(object? sender, RoutedEventArgs e)
        => Publish(ConfigurationStudioSection.Groups);
    private void HandleEncryptionKeysClick(object? sender, RoutedEventArgs e)
        => Publish(ConfigurationStudioSection.EncryptionKeys);
    private void HandleFilesClick(object? sender, RoutedEventArgs e)
        => Publish(ConfigurationStudioSection.Files);

    private void Publish(ConfigurationStudioSection section)
        => SectionRequested?.Invoke(this, new ConfigurationStudioSectionEventArgs(section));

    private void HandleHierarchyWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        ScrollViewer scroller = NavigationScroller;
        double maximumOffset = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
        double nextOffset = Math.Clamp(scroller.Offset.Y - (e.Delta.Y * 48), 0, maximumOffset);
        if (Math.Abs(nextOffset - scroller.Offset.Y) < 0.01)
            return;

        scroller.Offset = new Vector(scroller.Offset.X, nextOffset);
        e.Handled = true;
    }

}
