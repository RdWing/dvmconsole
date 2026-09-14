// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using DvmConsole.Application;

namespace DvmConsole.Mobile;

/// <summary>Places cards on the zone grid without changing radio configuration.</summary>
internal sealed class MobileCardReordering(
    MobileCardGrid panel,
    IReadOnlyDictionary<ChannelId, Control> cards, Action<IReadOnlyDictionary<string, MobileCardPosition>> save)
{
    public void Attach(Button handle, ChannelId id)
    {
        IPointer? pointer = null;
        bool dragging = false;
        bool suppressClick = false;
        Point origin = default;
        MobileCardPosition cardOrigin = default;
        var translation = new TranslateTransform();
        void Reset()
        {
            dragging = false;
            cards[id].Opacity = 1;
            cards[id].RenderTransform = null;
            cards[id].ZIndex = 0;
        }
        Gestures.SetIsHoldingEnabled(handle, true);
        handle.AddHandler(InputElement.PointerPressedEvent, (_, args) =>
        {
            pointer = args.Pointer;
            origin = args.GetPosition(panel);
            suppressClick = false;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        handle.AddHandler(Gestures.HoldingEvent, (_, args) =>
        {
            if (args.HoldingState != HoldingState.Started || pointer is null) return;
            cardOrigin = panel.Position(id);
            dragging = true;
            suppressClick = true;
            pointer.Capture(handle);
            cards[id].Opacity = .75;
            cards[id].RenderTransform = translation;
            cards[id].ZIndex = 1;
            translation.X = translation.Y = 0;
            args.Handled = true;
        });
        handle.AddHandler(InputElement.PointerMovedEvent, (_, args) =>
        {
            if (!dragging || args.Pointer != pointer) return;
            var position = args.GetPosition(panel);
            translation.X = position.X - origin.X;
            translation.Y = position.Y - origin.Y;
            args.PreventGestureRecognition();
            args.Handled = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        handle.AddHandler(InputElement.PointerReleasedEvent, (_, args) =>
        {
            if (!dragging || args.Pointer != pointer) return;
            var position = args.GetPosition(panel);
            panel.Move(id, MobileCardPosition.Snap(cardOrigin.X + position.X - origin.X, cardOrigin.Y + position.Y - origin.Y));
            try { save(panel.Snapshot()); }
            catch (Exception exception) { System.Diagnostics.Trace.TraceError("Saving card positions failed: {0}", exception); }
            Reset();
            args.PreventGestureRecognition();
            args.Pointer.Capture(null);
            args.Handled = true;
        }, RoutingStrategies.Tunnel);
        handle.PointerCaptureLost += (_, _) => Reset();
        handle.DetachedFromVisualTree += (_, _) => { Reset(); pointer?.Capture(null); };
        handle.AddHandler(Button.ClickEvent, (_, args) =>
        {
            if (!suppressClick) return;
            suppressClick = false;
            args.Handled = true;
        }, RoutingStrategies.Tunnel);
        Avalonia.Automation.AutomationProperties.SetHelpText(handle,
            "Hold the title, then drag to any grid position, including empty space.");
    }

}

public interface IMobileCardOrderPreferences
{
    IReadOnlyList<string> ReadCardOrder(string configurationId);
    void WriteCardOrder(string configurationId, IReadOnlyList<string> order);
}
