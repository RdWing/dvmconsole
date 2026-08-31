using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DvmConsole.Presentation;

public sealed partial class ConfigurationLibraryView : UserControl
{
    public ConfigurationLibraryView()
    {
        InitializeComponent();
    }

    public event EventHandler? RefreshRequested;
    public event EventHandler<ConfigurationLibraryItemEventArgs>? ActivateRequested;
    public event EventHandler<ConfigurationLibraryItemEventArgs>? TrashRequested;
    public event EventHandler<ConfigurationLibraryItemEventArgs>? RestoreRequested;

    private void HandleRefreshClick(object? sender, RoutedEventArgs e)
        => RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void HandleActivateClick(object? sender, RoutedEventArgs e)
        => RaiseItemEvent(sender, ActivateRequested);

    private void HandleTrashClick(object? sender, RoutedEventArgs e)
        => RaiseItemEvent(sender, TrashRequested);

    private void HandleRestoreClick(object? sender, RoutedEventArgs e)
        => RaiseItemEvent(sender, RestoreRequested);

    private void RaiseItemEvent(
        object? sender,
        EventHandler<ConfigurationLibraryItemEventArgs>? handler)
    {
        if (sender is Button { Tag: ConfigurationLibraryItemViewModel item })
            handler?.Invoke(this, new ConfigurationLibraryItemEventArgs(item));
    }

    private void InitializeComponent()
        => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
}
