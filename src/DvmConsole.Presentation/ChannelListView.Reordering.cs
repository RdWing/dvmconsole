// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using DvmConsole.Application;

namespace DvmConsole.Presentation;

public sealed partial class ChannelListView
{
    public bool EnableRowReordering { get; set; }
    public event Action<IReadOnlyList<string>>? RowOrderChanged;
    private (IPointer Pointer, Control Row, Control Container, int PreviousZIndex, ChannelId Id, Point Origin)? rowDrag;

    private void InitializeRowReordering()
    {
        Gestures.SetIsHoldingEnabled(this, true);
        AddHandler(Gestures.HoldingEvent, (_, args) =>
        {
            if (!EnableRowReordering || args.HoldingState != HoldingState.Started || rowPress is not { } press ||
                press.Row.DataContext is not ChannelListItemViewModel item) return;
            // Z order is relative to siblings. Raise the item container, not
            // its nested border, so the dragged row draws over neighboring items.
            Control container = press.Row.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault() ?? press.Row;
            rowDrag = (press.Pointer, press.Row, container, container.ZIndex, item.Id, press.Position);
            rowPress = null;
            press.Pointer.Capture(press.Row);
            press.Row.Opacity = .75;
            container.ZIndex = 1;
            args.Handled = true;
        });
        AddHandler(InputElement.PointerMovedEvent, (_, args) =>
        {
            if (rowDrag is not { } drag || drag.Pointer != args.Pointer) return;
            Point position = args.GetPosition(this);
            drag.Container.RenderTransform = new TranslateTransform(position.X - drag.Origin.X, position.Y - drag.Origin.Y);
            args.PreventGestureRecognition();
            args.Handled = true;
        }, RoutingStrategies.Tunnel, true);
        AddHandler(InputElement.PointerReleasedEvent, (_, args) =>
        {
            if (rowDrag is not { } drag || drag.Pointer != args.Pointer) return;
            var target = this.GetVisualDescendants().OfType<Border>().FirstOrDefault(row =>
            {
                if (!row.Classes.Contains("channel-list-row") || ReferenceEquals(row, drag.Row) || !row.IsVisible) return false;
                Point? origin = row.TranslatePoint(default, this);
                return origin is { } point && new Rect(point, row.Bounds.Size).Contains(args.GetPosition(this));
            });
            CancelRowDrag();
            args.PreventGestureRecognition();
            args.Handled = true;
            if (target?.DataContext is ChannelListItemViewModel item && DataContext is ConsoleListViewModel model &&
                model.MoveWithinZone(drag.Id, item.Id))
            {
                try { RowOrderChanged?.Invoke(model.Items.Select(row => row.Id.ToString()).ToArray()); }
                catch (Exception exception) { System.Diagnostics.Trace.TraceError("Saving channel order failed: {0}", exception); }
            }
        }, RoutingStrategies.Tunnel, true);
        AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => CancelRowDrag(), RoutingStrategies.Bubble, true);
        DetachedFromVisualTree += (_, _) => CancelRowDrag();
    }

    private void CancelRowDrag()
    {
        if (rowDrag is not { } drag) return;
        rowDrag = null;
        rowPress = null;
        drag.Row.Opacity = 1;
        drag.Container.ZIndex = drag.PreviousZIndex;
        drag.Container.RenderTransform = null;
        if (ReferenceEquals(drag.Pointer.Captured, drag.Row)) drag.Pointer.Capture(null);
    }
}
