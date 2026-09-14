// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace DvmConsole.Mobile;

/// <summary>Adapts button gestures to host-supplied manual PTT commands.</summary>
internal sealed class MobilePttButtonInput : IDisposable
{
    private readonly Button button;
    private readonly DvmConsole.Presentation.PttAccessibilityBinding accessibility;
    private readonly Func<ValueTask> press;
    private readonly Func<ValueTask> release;
    private readonly Func<ValueTask> toggle;
    private readonly Func<ValueTask> unkey;
    private readonly Action<Exception> reportFailure;
    private readonly Func<bool> useToggle;
    private readonly Func<bool> isTransmitting;
    private IPointer? pointer;
    private bool keyboardHeld;
    private bool disposed;
    private bool momentary;

    public MobilePttButtonInput(Button button, Func<ValueTask> press, Func<ValueTask> release,
        Func<ValueTask> toggle, Func<ValueTask> unkey,
        Action<Exception> reportFailure, Func<bool> useToggle, Func<bool> isTransmitting)
    {
        this.button = button;
        this.press = press;
        this.release = release;
        this.toggle = toggle;
        this.unkey = unkey;
        this.reportFailure = reportFailure;
        this.useToggle = useToggle;
        this.isTransmitting = isTransmitting;
        accessibility = new(button, useToggle, isTransmitting, toggle, unkey, reportFailure);
        button.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        button.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
        button.PointerCaptureLost += OnCaptureLost;
        button.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        button.AddHandler(InputElement.KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
        button.LostFocus += OnLostFocus;
        button.DetachedFromVisualTree += OnDetached;
    }

    private async void OnPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (disposed || !button.IsEffectivelyEnabled ||
            !args.GetCurrentPoint(button).Properties.IsLeftButtonPressed) return;
        args.PreventGestureRecognition();
        args.Handled = true;
        if (pointer is not null || keyboardHeld) return;
        pointer = args.Pointer;
        pointer.Capture(button);
        await BeginGestureAsync();
    }

    private async void OnPointerReleased(object? sender, PointerReleasedEventArgs args)
    {
        if (!ReferenceEquals(pointer, args.Pointer)) return;
        args.Handled = true;
        await ReleaseAsync();
    }

    private async void OnCaptureLost(object? sender, PointerCaptureLostEventArgs args)
    {
        if (ReferenceEquals(pointer, args.Pointer)) await ReleaseAsync();
    }

    private async void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (disposed || !button.IsEffectivelyEnabled || args.Key != Key.Space ||
            args.KeyModifiers != KeyModifiers.None) return;
        args.Handled = true;
        if (keyboardHeld || pointer is not null) return;
        keyboardHeld = true;
        await BeginGestureAsync();
    }

    private async void OnKeyUp(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Space || !keyboardHeld) return;
        args.Handled = true;
        await ReleaseAsync();
    }

    private async void OnLostFocus(object? sender, RoutedEventArgs args)
    {
        if (keyboardHeld) await ReleaseAsync();
    }

    private async void OnDetached(object? sender, VisualTreeAttachmentEventArgs args)
        => await ReleaseAsync();

    private ValueTask BeginGestureAsync()
    {
        momentary = !useToggle();
        return ObserveAsync(momentary ? press() :
            isTransmitting() ? unkey() : toggle());
    }

    private ValueTask ReleaseAsync()
    {
        // Clear gesture ownership before relinquishing capture, which can raise
        // CaptureLost synchronously. The shared controller fences late starts.
        IPointer? captured = pointer;
        pointer = null;
        keyboardHeld = false;
        captured?.Capture(null);
        return momentary ? ObserveAsync(release()) : ValueTask.CompletedTask;
    }

    private async ValueTask ObserveAsync(ValueTask action)
    {
        try { await action; }
        catch (OperationCanceledException) { }
        catch (Exception exception) { reportFailure(exception); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        accessibility.Dispose();
        button.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        button.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleased);
        button.PointerCaptureLost -= OnCaptureLost;
        button.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        button.RemoveHandler(InputElement.KeyUpEvent, OnKeyUp);
        button.LostFocus -= OnLostFocus;
        button.DetachedFromVisualTree -= OnDetached;
        // The console owns asynchronous release before disposing its adapters.
        pointer?.Capture(null);
        pointer = null;
        keyboardHeld = false;
    }
}
