// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DvmConsole.Mobile;

/// <summary>Keeps mobile pages and their focused editor above the software keyboard.</summary>
internal sealed class MobileInputPaneView : Border
{
    private TopLevel? topLevel;
    private IInputPane? inputPane;
    private Rect occludedRect;
    private int layoutGeneration;

    public MobileInputPaneView(Control content)
    {
        Child = content;
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        SizeChanged += (_, _) => UpdateInsets();
        GotFocus += (_, _) => RevealFocusedControl();
    }

    private void Attach()
    {
        Detach();
        topLevel = TopLevel.GetTopLevel(this);
        inputPane = topLevel?.InputPane;
        if (inputPane is null) return;
        inputPane.StateChanged += OnInputPaneChanged;
        occludedRect = inputPane.State == InputPaneState.Open ? inputPane.OccludedRect : default;
        UpdateInsets();
    }

    private void Detach()
    {
        if (inputPane is not null) inputPane.StateChanged -= OnInputPaneChanged;
        inputPane = null;
        topLevel = null;
        occludedRect = default;
        layoutGeneration++;
        Padding = default;
    }

    private void OnInputPaneChanged(object? sender, InputPaneStateEventArgs args)
    {
        occludedRect = args.NewState == InputPaneState.Open ? args.EndRect : default;
        UpdateInsets();
    }

    private void UpdateInsets()
    {
        // This view is inside the safe-area border. Compute only the additional
        // overlap, so the home-indicator inset is not charged twice.
        double bottom = 0;
        if (topLevel is not null && occludedRect.Height > 0 &&
            this.TranslatePoint(default, topLevel) is { } origin)
        {
            var overlap = new Rect(origin, Bounds.Size).Intersect(occludedRect);
            bottom = overlap.Height;
        }
        var padding = new Thickness(0, 0, 0, bottom);
        if (Padding == padding) return;
        Padding = padding;
        RevealFocusedControl();
    }

    private void RevealFocusedControl()
    {
        int generation = ++layoutGeneration;
        if (Padding.Bottom == 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != layoutGeneration || topLevel?.FocusManager?.GetFocusedElement() is not Control focused)
                return;
            if (focused.GetVisualAncestors().Contains(this)) focused.BringIntoView();
        }, DispatcherPriority.Loaded);
    }
}
