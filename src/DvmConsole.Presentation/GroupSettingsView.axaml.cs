// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DvmConsole.Presentation;

public sealed partial class GroupSettingsView : UserControl
{
    public GroupSettingsView()
    {
        InitializeComponent();
        ResponsiveSettingsDensity.Attach(this);
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
