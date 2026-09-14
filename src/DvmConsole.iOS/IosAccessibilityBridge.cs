// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Threading;
using CoreGraphics;
using DvmConsole.Presentation;
using Foundation;
using UIKit;

namespace DvmConsole.iOS;

/// <summary>Exposes existing automation peers through UIKit without taking over touch input.</summary>
[Register("NeoAccessibilityContainer")]
internal sealed partial class IosAccessibilityBridge : UIView
{
    private readonly Control root;
    private readonly Dictionary<AutomationPeer, PeerElement> elements = [];

    public IosAccessibilityBridge(Control root, UIView nativeRoot) : base(nativeRoot.Bounds)
    {
        this.root = root;
        IsAccessibilityElement = false;
        UserInteractionEnabled = false;
        BackgroundColor = UIColor.Clear;
        AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight;
        nativeRoot.AddSubview(this);
        root.LayoutUpdated += HandleLayoutUpdated;
        voiceOverObserver = UIApplication.Notifications.ObserveVoiceOverStatusDidChange((_, _) => ScheduleLayoutRefresh());
    }

    // UIKit requests the tree on demand. No polling or per-frame tree rebuilding
    // occurs while accessibility is unused. Reuse native identities across reads.
    [Export("accessibilityElements")]
    public NSArray ReadElements()
    {
        var visible = new List<NSObject>();
        var retained = new HashSet<AutomationPeer>();
        var visited = new HashSet<AutomationPeer>();
        scrollParents.Clear();
        var pending = new Stack<(AutomationPeer Peer, AutomationPeer? ScrollOwner, string? SelectionLabel, bool ChoiceDisplay)>();
        if (ControlAutomationPeer.CreatePeerForElement(root) is { } initial) pending.Push((initial, null, null, false));
        while (pending.TryPop(out var entry))
        {
            var peer = entry.Peer;
            // NativeControlHost supplies an interop placeholder whose geometry
            // API is intentionally unimplemented. UIKit owns that native subtree.
            if (!visited.Add(peer) || peer.IsOffscreen()) continue;
            var scrollOwner = entry.ScrollOwner;
            if (peer.GetProvider<IScrollProvider>() is not null)
            {
                scrollParents[peer] = scrollOwner;
                scrollOwner = peer;
            }
            if (!TryReadBounds(peer, out var bounds)) continue;
            if (bounds.Width <= 0 || bounds.Height <= 0) continue;
            string name = peer.GetName();
            bool actionable = peer.GetProvider<IInvokeProvider>() is not null ||
                peer.GetProvider<IToggleProvider>() is not null ||
                peer.GetProvider<IRangeValueProvider>() is not null ||
                peer.GetProvider<ISelectionItemProvider>() is not null ||
                peer.GetProvider<IExpandCollapseProvider>() is not null ||
                peer.GetProvider<IValueProvider>() is { IsReadOnly: false };
            bool repeatsSelectionLabel = !actionable &&
                (entry.ChoiceDisplay || string.Equals(name, entry.SelectionLabel, StringComparison.Ordinal));
            if (actionable || (!string.IsNullOrWhiteSpace(name) && !repeatsSelectionLabel))
            {
                retained.Add(peer);
                if (!elements.TryGetValue(peer, out var element))
                    elements.Add(peer, element = new PeerElement(this, peer));
                element.ScrollOwner = scrollOwner;
                element.Refresh(name, bounds);
                visible.Add(element);
                // A selectable row can contain independently operable controls.
                // Buttons still expose one action rather than their decorative children.
                if (actionable && peer.GetProvider<ISelectionItemProvider>() is null &&
                    peer.GetProvider<IExpandCollapseProvider>()?.ExpandCollapseState != ExpandCollapseState.Expanded) continue;
            }
            // A plain label inside a selectable row repeats the row's announcement.
            // Keep independently actionable children, even when their labels match.
            string? selectionLabel = peer.GetProvider<ISelectionItemProvider>() is not null
                ? name : entry.SelectionLabel;
            // An expanded ComboBox contains its closed selected-value presenter too.
            // Its value is already announced by the ComboBox; expose option rows instead.
            bool choiceDisplay = peer is ComboBoxAutomationPeer ||
                (entry.ChoiceDisplay && peer.GetProvider<ISelectionItemProvider>() is null);
            var children = peer.GetChildren();
            for (int i = children.Count - 1; i >= 0; i--)
                pending.Push((children[i], scrollOwner, selectionLabel, choiceDisplay));
        }
        foreach (var peer in elements.Keys.Where(peer => !retained.Contains(peer)).ToArray())
        {
            elements[peer].Retire();
            elements.Remove(peer);
        }
        var current = visible.Cast<PeerElement>().ToArray();
        if (!current.SequenceEqual(visibleElements)) structureRevision++;
        visibleElements = current;
        return NSArray.FromNSObjects(visible.ToArray());
    }

    private static bool TryReadBounds(AutomationPeer peer, out Avalonia.Rect bounds)
    {
        try { bounds = peer.GetBoundingRectangle(); return true; }
        catch (NotImplementedException)
        {
            // Avalonia's internal interop peer delegates geometry to the native host.
            bounds = default;
            return false;
        }
    }

    private bool ContainsCurrentPeer(AutomationPeer target)
    {
        var pending = new Stack<AutomationPeer>();
        if (ControlAutomationPeer.CreatePeerForElement(root) is { } initial) pending.Push(initial);
        while (pending.TryPop(out var candidate))
        {
            if (candidate.IsOffscreen() || !TryReadBounds(candidate, out _)) continue;
            if (ReferenceEquals(candidate, target)) return true;
            foreach (var child in candidate.GetChildren()) pending.Push(child);
        }
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            disposed = true;
            root.LayoutUpdated -= HandleLayoutUpdated;
            voiceOverObserver.Dispose();
            foreach (var element in elements.Values) element.Retire();
            elements.Clear();
        }
        base.Dispose(disposing);
    }

    [Register("NeoAccessibilityElement")]
    internal sealed class PeerElement : UIAccessibilityElement
    {
        private readonly IosAccessibilityBridge container;
        private readonly AutomationPeer peer;
        private bool retired;
        private bool refreshPending;

        public PeerElement(IosAccessibilityBridge container, AutomationPeer peer) : base(container)
        {
            this.container = container;
            this.peer = peer;
            peer.PropertyChanged += HandlePeerPropertyChanged;
        }

        private void HandlePeerPropertyChanged(object? sender, EventArgs args)
        {
            if (retired || refreshPending) return;
            refreshPending = true;
            Dispatcher.UIThread.Post(() =>
            {
                refreshPending = false;
                if (!retired) RefreshCurrent();
            }, DispatcherPriority.Background);
        }

        private void RefreshCurrent()
        {
            if (TryReadBounds(peer, out var bounds)) Refresh(peer.GetName(), bounds);
        }
        internal AutomationPeer? ScrollOwner { get; set; }

        [Export("accessibilityElementDidBecomeFocused")]
        public void BecameFocused()
        {
            if (!IsCurrent) return;
            container.focusedElement = this;
            RefreshCurrent();
        }

        [Export("accessibilityElementDidLoseFocus")]
        public void LostFocus()
        {
            if (ReferenceEquals(container.focusedElement, this)) container.focusedElement = null;
        }

        [Export("accessibilityScroll:")]
        public bool Scroll(UIAccessibilityScrollDirection direction)
            => IsCurrent && container.ScrollFrom(ScrollOwner, direction);

        public void Retire()
        {
            if (retired) return;
            retired = true;
            peer.PropertyChanged -= HandlePeerPropertyChanged;
            LostFocus();
        }

        public void Refresh(string name, Avalonia.Rect bounds)
        {
            AccessibilityLabel = name;
            AccessibilityHint = PttBinding?.Hint ?? peer.GetHelpText();
            AccessibilityFrameInContainerSpace = new CGRect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            var range = peer.GetProvider<IRangeValueProvider>();
            var toggle = peer.GetProvider<IToggleProvider>();
            var selection = peer.GetProvider<ISelectionItemProvider>();
            var expandable = peer.GetProvider<IExpandCollapseProvider>();
            bool editable = peer.GetProvider<IValueProvider>() is { IsReadOnly: false };
            if (editable && string.IsNullOrWhiteSpace(AccessibilityHint)) AccessibilityHint = "Double-tap to edit.";
            AccessibilityTraits = (ulong)(!peer.IsEnabled() ? UIAccessibilityTrait.NotEnabled : UIAccessibilityTrait.None);
            if (range is { IsReadOnly: false }) AccessibilityTraits |= (ulong)UIAccessibilityTrait.Adjustable;
            else if (peer.GetProvider<IInvokeProvider>() is not null || toggle is not null || selection is not null || expandable is not null || editable)
                AccessibilityTraits |= (ulong)UIAccessibilityTrait.Button;
            else AccessibilityTraits |= (ulong)UIAccessibilityTrait.StaticText;
            if (selection?.IsSelected == true) AccessibilityTraits |= (ulong)UIAccessibilityTrait.Selected;
            AccessibilityValue = range is not null ? range.Value.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture)
                : toggle?.ToggleState.ToString() ?? ReadTextValue();
        }

        private string ReadTextValue()
        {
            // Avalonia's text provider returns raw text even for password fields.
            // Never copy that value into the native accessibility tree.
            if (peer is TextBoxAutomationPeer { Owner.PasswordChar: not '\0' }) return "Secure text";
            return peer.GetProvider<IValueProvider>()?.Value ?? string.Empty;
        }

        private PttAccessibilityBinding? PttBinding => peer is ControlAutomationPeer { Owner: Button button }
            ? button.GetValue(PttAccessibilityBinding.BindingProperty) : null;

        private bool IsCurrent => !retired && !peer.IsOffscreen() && container.ContainsCurrentPeer(peer);

        private bool CanAct => peer.IsEnabled() && IsCurrent;

        [Export("accessibilityActivate")]
        public bool Activate()
        {
            if (!CanAct) return false;
            if (PttBinding is { } ptt)
            {
                bool activated = ptt.TryActivate();
                RefreshCurrent();
                return activated;
            }
            if (peer.GetProvider<IExpandCollapseProvider>() is { } expandable)
            {
                if (expandable.ExpandCollapseState == ExpandCollapseState.Expanded) expandable.Collapse();
                else expandable.Expand();
                container.ScheduleLayoutRefresh();
            }
            else if (peer.GetProvider<IToggleProvider>() is { } toggle) toggle.Toggle();
            else if (peer.GetProvider<IInvokeProvider>() is { } invoke)
            {
                // Match pointer activation: pending field edits commit when focus
                // leaves their editor before the requested command runs.
                if (peer.IsKeyboardFocusable()) peer.SetFocus();
                invoke.Invoke();
            }
            else if (peer.GetProvider<ISelectionItemProvider>() is { } selection)
            {
                var selectionContainer = selection.SelectionContainer;
                selection.Select();
                // Native activation completes a menu choice, including choosing its current value.
                // Ordinary list selection must leave its surrounding controls in place.
                if (selectionContainer is ComboBoxAutomationPeer &&
                    selectionContainer is IExpandCollapseProvider { ExpandCollapseState: ExpandCollapseState.Expanded } popup)
                    popup.Collapse();
                container.ScheduleLayoutRefresh();
            }
            else if (peer.GetProvider<IValueProvider>() is { IsReadOnly: false }) peer.SetFocus();
            else return false;
            RefreshCurrent();
            return true;
        }

        [Export("accessibilityIncrement")]
        public void Increment() => Adjust(1);

        [Export("accessibilityDecrement")]
        public void Decrement() => Adjust(-1);

        private void Adjust(int direction)
        {
            if (!CanAct || peer.GetProvider<IRangeValueProvider>() is not { IsReadOnly: false } range) return;
            double step = range.SmallChange > 0 ? range.SmallChange : (range.Maximum - range.Minimum) / 20;
            range.SetValue(Math.Clamp(range.Value + direction * step, range.Minimum, range.Maximum));
            Refresh(peer.GetName(), peer.GetBoundingRectangle());
        }
    }
}
