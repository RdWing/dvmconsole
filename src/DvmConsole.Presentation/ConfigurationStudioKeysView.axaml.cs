using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DvmConsole.Core.Configuration;

namespace DvmConsole.Presentation;

public sealed partial class ConfigurationStudioKeysView : UserControl
{
    private int queuedCommitVersion;
    private bool handlingSelectionChange;

    public ConfigurationStudioKeysView()
    {
        InitializeComponent();
    }

    public event EventHandler? DeleteRequested;

    private ConfigurationStudioViewModel? ViewModel => DataContext as ConfigurationStudioViewModel;

    private void HandleAddKeyClick(object? sender, RoutedEventArgs e) => ViewModel?.AddKey();
    private void HandleDeleteKeyClick(object? sender, RoutedEventArgs e)
        => DeleteRequested?.Invoke(this, EventArgs.Empty);
    private void HandleKeyFieldEdit(object? sender, RoutedEventArgs e)
    {
        if (IsLoaded)
            ViewModel?.CommitKeyEdit();
    }

    private void HandleKeyProtocolChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || handlingSelectionChange ||
            sender is not ComboBox { SelectedItem: ConfigurationProtocolOption })
        {
            return;
        }
        QueueCommit(() => ViewModel?.CommitKeyProtocolEdit());
    }

    private void HandleKeyAlgorithmChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || handlingSelectionChange ||
            sender is not ComboBox { SelectedItem: EncryptionAlgorithmOption })
        {
            return;
        }
        QueueCommit(() => ViewModel?.CommitKeyEdit());
    }

    private void QueueCommit(Action commit)
    {
        int version = ++queuedCommitVersion;
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsLoaded || version != queuedCommitVersion)
                return;
            handlingSelectionChange = true;
            try
            {
                commit();
            }
            finally
            {
                handlingSelectionChange = false;
            }
        }, DispatcherPriority.Background);
    }
}
