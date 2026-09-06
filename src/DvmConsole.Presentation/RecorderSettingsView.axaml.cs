// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DvmConsole.Presentation;

public sealed partial class RecorderSettingsView : UserControl
{
    public RecorderSettingsView()
    {
        InitializeComponent();
        ResponsiveSettingsDensity.Attach(this);
    }

    public event EventHandler? ChooseRecordingLocationRequested;
    public event EventHandler? ApplyRecordingLocationRequested;
    public event EventHandler? ApplyRecordingRetentionRequested;
    public event EventHandler<RecorderChannelEventArgs>? SaveIgnoredSubscribersRequested;

    private void HandleChooseRecordingLocationClick(object? sender, RoutedEventArgs e)
        => ChooseRecordingLocationRequested?.Invoke(this, EventArgs.Empty);

    private void HandleApplyRecordingLocationClick(object? sender, RoutedEventArgs e)
        => ApplyRecordingLocationRequested?.Invoke(this, EventArgs.Empty);

    private void HandleApplyRecordingRetentionClick(object? sender, RoutedEventArgs e)
        => ApplyRecordingRetentionRequested?.Invoke(this, EventArgs.Empty);

    private void HandleSaveIgnoredSubscribersClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: IRecorderChannelViewModel channel })
            SaveIgnoredSubscribersRequested?.Invoke(this, new RecorderChannelEventArgs(channel));
    }
}
