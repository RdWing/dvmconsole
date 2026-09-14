// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Foundation;
using UIKit;

namespace DvmConsole.iOS;

internal sealed partial class IosAccessibilityBridge
{
    private readonly Dictionary<AutomationPeer, AutomationPeer?> scrollParents = [];
    private PeerElement? focusedElement;

    [Export("accessibilityScroll:")]
    public bool Scroll(UIAccessibilityScrollDirection direction)
    {
        if (focusedElement is { } focused) return focused.Scroll(direction);
        using var refreshed = ReadElements();
        foreach (var owner in scrollParents.Keys)
            if (ScrollFrom(owner, direction)) return true;
        return false;
    }

    private bool ScrollFrom(AutomationPeer? owner, UIAccessibilityScrollDirection direction)
    {
        bool vertical = direction is UIAccessibilityScrollDirection.Up or UIAccessibilityScrollDirection.Down
            or UIAccessibilityScrollDirection.Next or UIAccessibilityScrollDirection.Previous;
        bool forward = direction is UIAccessibilityScrollDirection.Down or UIAccessibilityScrollDirection.Right
            or UIAccessibilityScrollDirection.Next;
        if (!vertical && direction is not (UIAccessibilityScrollDirection.Left or UIAccessibilityScrollDirection.Right)) return false;
        while (owner is not null)
        {
            if (!ContainsCurrentPeer(owner)) return false;
            if (owner.GetProvider<IScrollProvider>() is { } scroll)
            {
                bool available = vertical ? scroll.VerticallyScrollable : scroll.HorizontallyScrollable;
                double percent = vertical ? scroll.VerticalScrollPercent : scroll.HorizontalScrollPercent;
                if (available && (forward ? percent < 100 : percent > 0))
                {
                    var amount = forward ? ScrollAmount.LargeIncrement : ScrollAmount.LargeDecrement;
                    scroll.Scroll(vertical ? ScrollAmount.NoAmount : amount, vertical ? amount : ScrollAmount.NoAmount);
                    ScheduleLayoutRefresh();
                    if (UIAccessibility.IsVoiceOverRunning)
                        UIAccessibility.PostNotification(UIAccessibilityPostNotification.PageScrolled, null);
                    return true;
                }
            }
            owner = scrollParents.GetValueOrDefault(owner);
        }
        return false;
    }
}
