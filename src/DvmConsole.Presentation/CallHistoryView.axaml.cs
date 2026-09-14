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

    /// <summary>Bounds expanded filters and wraps the header for touch hosts.</summary>
    public void UseTouchLayout()
    {
        Classes.Add("touch-history");
        RecordingOpenActionText = "Export";
        ExportButton.MinHeight = ClearButton.MinHeight = 44;
        HistoryLayout.Margin = new Avalonia.Thickness(0);
        HistoryLayout.RowSpacing = 6;
        HistoryFilters.Padding = new Avalonia.Thickness(0);
        SizeChanged += (_, _) => ApplyTouchLayout();
        ApplyTouchLayout();
    }

    private void ApplyTouchLayout()
    {
        bool narrow = Bounds.Width < 600;
        HistoryHeader.RowDefinitions = new RowDefinitions(narrow ? "Auto,Auto,Auto" : "Auto,Auto");
        Grid.SetRow(HistoryHeaderActions, narrow ? 1 : 0);
        Grid.SetColumn(HistoryHeaderActions, narrow ? 0 : 1);
        Grid.SetColumnSpan(HistoryHeaderActions, narrow ? 2 : 1);
        Grid.SetRow(HistorySearch, narrow ? 2 : 1);
        HistoryFilterScroller.MaxHeight = Math.Max(32, Math.Min(Bounds.Height * 0.3, Bounds.Height - 210));
    }

    public string RecordingOpenActionText { get; private set; } = "Open";

    public event EventHandler? ExportRequested;
    public event EventHandler? ClearRequested;
    public event EventHandler? ClearFiltersRequested;
    public event EventHandler<CallHistoryItemEventArgs>? PlaybackToggleRequested;
    public event EventHandler<CallHistoryItemEventArgs>? OpenRequested;
    public event EventHandler<CallHistoryItemEventArgs>? DeleteRequested;

    public ListBox HistoryItems => HistoryList;
    public Button ExportButton => ExportHistoryButton;
    public Button ClearButton => ClearHistoryButton;

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
