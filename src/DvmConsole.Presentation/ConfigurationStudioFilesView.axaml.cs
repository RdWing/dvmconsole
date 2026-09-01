using Avalonia.Controls;
using Avalonia.Interactivity;
using DvmConsole.Core.Configuration;

namespace DvmConsole.Presentation;

public sealed partial class ConfigurationStudioFilesView : UserControl
{
    public ConfigurationStudioFilesView()
    {
        InitializeComponent();
    }

    public event EventHandler? DeleteAliasRequested;
    public event EventHandler? BrowseKeyFileRequested;
    public event EventHandler<ConfigurationStudioAliasFileEventArgs>? BrowseAliasFileRequested;
    public event EventHandler? ExportFullRequested;
    public event EventHandler? ExportSanitizedRequested;

    private ConfigurationStudioViewModel? ViewModel => DataContext as ConfigurationStudioViewModel;

    private void HandleDraftFieldEdit(object? sender, RoutedEventArgs e) => ViewModel?.CommitFieldEdit();
    private void HandleAliasFieldEdit(object? sender, RoutedEventArgs e) => ViewModel?.CommitAliasEdit();
    private void HandleAddAliasClick(object? sender, RoutedEventArgs e) => ViewModel?.AddAlias();
    private void HandleDeleteAliasClick(object? sender, RoutedEventArgs e)
        => DeleteAliasRequested?.Invoke(this, EventArgs.Empty);
    private void HandleBrowseKeyFileClick(object? sender, RoutedEventArgs e)
        => BrowseKeyFileRequested?.Invoke(this, EventArgs.Empty);
    private void HandleBrowseAliasFileClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SystemConfiguration system })
            BrowseAliasFileRequested?.Invoke(this, new ConfigurationStudioAliasFileEventArgs(system));
    }
    private void HandleExportFullClick(object? sender, RoutedEventArgs e)
        => ExportFullRequested?.Invoke(this, EventArgs.Empty);
    private void HandleExportSanitizedClick(object? sender, RoutedEventArgs e)
        => ExportSanitizedRequested?.Invoke(this, EventArgs.Empty);
}

public sealed class ConfigurationStudioAliasFileEventArgs(SystemConfiguration system) : EventArgs
{
    public SystemConfiguration System { get; } = system ?? throw new ArgumentNullException(nameof(system));
}
