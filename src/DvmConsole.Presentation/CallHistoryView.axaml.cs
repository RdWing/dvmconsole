// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DvmConsole.Presentation;

public sealed partial class CallHistoryView : UserControl
{
    public CallHistoryView()
    {
        InitializeComponent();
    }

    public event EventHandler? ExportRequested;
    public event EventHandler? ClearRequested;
    public event EventHandler? ClearFiltersRequested;
    public event EventHandler<CallHistoryItemEventArgs>? PlaybackToggleRequested;
    public event EventHandler<CallHistoryItemEventArgs>? OpenRequested;
    public event EventHandler<CallHistoryItemEventArgs>? DeleteRequested;

    public ListBox HistoryItems => HistoryList;

    private void HandleExportClick(object? sender, RoutedEventArgs e) => ExportRequested?.Invoke(this, EventArgs.Empty);
    private void HandleClearClick(object? sender, RoutedEventArgs e) => ClearRequested?.Invoke(this, EventArgs.Empty);
    private void HandleClearFiltersClick(object? sender, RoutedEventArgs e) => ClearFiltersRequested?.Invoke(this, EventArgs.Empty);
    private void HandlePlaybackToggleClick(object? sender, RoutedEventArgs e)
        => PublishItem(sender, PlaybackToggleRequested);
    private void HandleOpenClick(object? sender, RoutedEventArgs e) => PublishItem(sender, OpenRequested);
    private void HandleDeleteClick(object? sender, RoutedEventArgs e) => PublishItem(sender, DeleteRequested);

    private void PublishItem(
        object? sender,
        EventHandler<CallHistoryItemEventArgs>? handler)
    {
        if (sender is Button { Tag: ICallHistoryItemViewModel item })
            handler?.Invoke(this, new CallHistoryItemEventArgs(item));
    }

}
