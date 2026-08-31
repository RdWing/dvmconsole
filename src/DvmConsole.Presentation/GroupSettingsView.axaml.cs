using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DvmConsole.Presentation;

public sealed partial class GroupSettingsView : UserControl
{
    public GroupSettingsView()
    {
        InitializeComponent();
    }

    public event EventHandler<PatchGroupEventArgs>? SaveGroupRequested;
    public event EventHandler<PatchGroupEventArgs>? ToggleMultiSelectPttRequested;

    private void HandleSaveGroupClick(object? sender, RoutedEventArgs e)
        => Publish(sender, SaveGroupRequested);

    private void HandleMultiSelectPttClick(object? sender, RoutedEventArgs e)
        => Publish(sender, ToggleMultiSelectPttRequested);

    private void Publish(object? sender, EventHandler<PatchGroupEventArgs>? handler)
    {
        if (sender is Button { Tag: PatchGroupEditorViewModel group })
            handler?.Invoke(this, new PatchGroupEventArgs(group));
    }
}
