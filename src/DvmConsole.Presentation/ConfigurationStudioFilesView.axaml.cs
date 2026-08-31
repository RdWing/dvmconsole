using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DvmConsole.Presentation;

public sealed partial class ConfigurationStudioFilesView : UserControl
{
    public ConfigurationStudioFilesView()
    {
        InitializeComponent();
    }

    public event EventHandler? DeleteAliasRequested;
    public event EventHandler? ExportFullRequested;
    public event EventHandler? ExportSanitizedRequested;

    private ConfigurationStudioViewModel? ViewModel => DataContext as ConfigurationStudioViewModel;

    private void HandleDraftFieldEdit(object? sender, RoutedEventArgs e) => ViewModel?.CommitFieldEdit();
    private void HandleAliasFieldEdit(object? sender, RoutedEventArgs e) => ViewModel?.CommitAliasEdit();
    private void HandleAddAliasClick(object? sender, RoutedEventArgs e) => ViewModel?.AddAlias();
    private void HandleDeleteAliasClick(object? sender, RoutedEventArgs e)
        => DeleteAliasRequested?.Invoke(this, EventArgs.Empty);
    private void HandleExportFullClick(object? sender, RoutedEventArgs e)
        => ExportFullRequested?.Invoke(this, EventArgs.Empty);
    private void HandleExportSanitizedClick(object? sender, RoutedEventArgs e)
        => ExportSanitizedRequested?.Invoke(this, EventArgs.Empty);
}
