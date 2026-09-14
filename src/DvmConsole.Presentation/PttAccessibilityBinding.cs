// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace DvmConsole.Presentation;

/// <summary>Accessible activation uses explicit toggle intent; it never invents a held press.</summary>
public sealed class PttAccessibilityBinding : AvaloniaObject, IDisposable
{
    public static readonly AttachedProperty<PttAccessibilityBinding?> BindingProperty =
        AvaloniaProperty.RegisterAttached<PttAccessibilityBinding, Button, PttAccessibilityBinding?>("Binding");
    private readonly Button button;
    private readonly Func<bool> useToggle;
    private readonly Func<bool> active;
    private readonly Func<ValueTask> toggle;
    private readonly Func<ValueTask> release;
    private readonly Action<Exception> reportFailure;
    private bool disposed;

    public PttAccessibilityBinding(Button button, Func<bool> useToggle, Func<bool> active,
        Func<ValueTask> toggle, Func<ValueTask> release, Action<Exception> reportFailure)
    {
        this.button = button;
        this.useToggle = useToggle;
        this.active = active;
        this.toggle = toggle;
        this.release = release;
        this.reportFailure = reportFailure;
        button.SetValue(BindingProperty, this);
    }

    public string Hint => active() ? "Double-tap to stop transmitting."
        : useToggle() ? "Double-tap to start or stop transmitting."
        : "Hold to talk. Enable Tap to toggle PTT in Settings for accessibility activation.";

    public bool TryActivate()
    {
        if (disposed || !button.IsEffectivelyEnabled || button.GetVisualRoot() is null) return false;
        bool stop = active();
        if (!stop && !useToggle()) return false;
        ObserveActivation(stop);
        return true;
    }

    private async void ObserveActivation(bool stop)
    {
        try { await (stop ? release() : toggle()); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { reportFailure(exception); }
    }

    public void Dispose()
    {
        disposed = true;
        if (ReferenceEquals(button.GetValue(BindingProperty), this)) button.ClearValue(BindingProperty);
    }
}
