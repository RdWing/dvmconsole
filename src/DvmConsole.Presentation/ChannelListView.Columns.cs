// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;

namespace DvmConsole.Presentation;

public sealed partial class ChannelListView
{
    public bool AllowMultipleColumns { get; set; }
    private ListBox? rightColumn;
    private ConsoleListViewModel? columnModel;
    private readonly ObservableCollection<IConsoleListRow> leftRows = [];
    private readonly ObservableCollection<IConsoleListRow> rightRows = [];
    private readonly HashSet<IConsoleListRow> rightGroups = [];

    private ListBox ListFor(object item)
        => rightColumn is { IsVisible: true } && item is IConsoleListRow row && rightRows.Contains(row)
            ? rightColumn : this.FindControl<ListBox>("channelRows")!;

    private void UpdateColumns()
    {
        if (this.FindControl<Grid>("ListColumns") is not { } grid ||
            DataContext is not ConsoleListViewModel model) return;
        bool wide = AllowMultipleColumns && Bounds.Width >= 1000;
        if (wide == (columnModel is not null)) return;
        var left = this.FindControl<ListBox>("channelRows")!;
        if (!wide)
        {
            StopColumns();
            left.ItemsSource = model.VisibleRows;
            rightColumn!.IsVisible = false;
            grid.ColumnDefinitions = new ColumnDefinitions("*");
            grid.ColumnSpacing = 0;
            return;
        }
        if (rightColumn is null)
        {
            rightColumn = new ListBox
            {
                AutoScrollToSelectedItem = false,
                Background = Avalonia.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ItemsSource = rightRows,
                ItemsPanel = left.ItemsPanel
            };
            rightColumn.Classes.Add("channel-list");
            foreach (var template in left.DataTemplates) rightColumn.DataTemplates.Add(template);
            ScrollViewer.SetHorizontalScrollBarVisibility(rightColumn, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(rightColumn, ScrollBarVisibility.Hidden);
            ScrollViewer.SetBringIntoViewOnFocusChange(rightColumn, false);
            Grid.SetColumn(rightColumn, 1);
            grid.Children.Add(rightColumn);
        }
        columnModel = model;
        int groupIndex = 0;
        foreach (var group in model.VisibleRows.OfType<ConsoleListGroupViewModel>().Where(group => group.IsSystem))
            if (groupIndex++ % 2 == 1) rightGroups.Add(group);
        RefreshColumns();
        left.ItemsSource = leftRows;
        rightColumn.IsVisible = true;
        grid.ColumnDefinitions = new ColumnDefinitions("*,*");
        grid.ColumnSpacing = 16;
        model.VisibleRows.CollectionChanged += RowsChanged;
    }

    private void RowsChanged(object? sender, NotifyCollectionChangedEventArgs args) => RefreshColumns();

    private void RefreshColumns()
    {
        if (columnModel is null) return;
        var left = new List<IConsoleListRow>();
        var right = new List<IConsoleListRow>();
        bool useRight = false;
        foreach (var row in columnModel.VisibleRows)
        {
            if (row is ConsoleListGroupViewModel { IsSystem: true }) useRight = rightGroups.Contains(row);
            (useRight ? right : left).Add(row);
        }
        Reconcile(leftRows, left);
        Reconcile(rightRows, right);
    }

    // Preserve containers and scroll anchors: never reset the collection on expansion.
    private static void Reconcile(ObservableCollection<IConsoleListRow> current, List<IConsoleListRow> desired)
    {
        var retained = desired.ToHashSet();
        for (int i = current.Count - 1; i >= 0; i--)
            if (!retained.Contains(current[i])) current.RemoveAt(i);
        for (int i = 0; i < desired.Count; i++)
            if (i >= current.Count || !ReferenceEquals(current[i], desired[i]))
            {
                int existing = current.IndexOf(desired[i]);
                if (existing >= 0) current.Move(existing, i);
                else current.Insert(i, desired[i]);
            }
    }

    private void StopColumns()
    {
        if (columnModel is not null) columnModel.VisibleRows.CollectionChanged -= RowsChanged;
        columnModel = null;
        rightGroups.Clear();
        leftRows.Clear();
        rightRows.Clear();
    }
}
