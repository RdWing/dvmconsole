// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DvmConsole.Application;
using System.Diagnostics;

namespace DvmConsole.Presentation;

public sealed partial class ChannelListView : UserControl
{
    public event Action<SystemId>? ConnectionToggleRequested;

    private void HandleConnectionClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if (sender is Button { DataContext: ConsoleListGroupViewModel { CanToggleConnection: true, SystemId: { } id } })
            ConnectionToggleRequested?.Invoke(id);
    }

    private bool adaptToNarrowWidth;
    public bool AdaptToNarrowWidth
    {
        get => adaptToNarrowWidth;
        set
        {
            adaptToNarrowWidth = value;
            UpdateCompactLayout();
        }
    }

    private void UpdateCompactLayout()
    {
        Classes.Set("compact", adaptToNarrowWidth && Bounds.Width < 600);
        UpdateColumns();
    }

    private Func<bool> useTogglePtt = static () => false;
    private (IPointer Pointer, Control Row, Point Position)? rowPress;
    private int anchorGeneration;
    private readonly PttHoldTracker<IPointer> heldPttPointers = new();
    private ChannelId? keyboardPttChannel;
    private bool keyboardPttMomentary;

    public ChannelListView()
    {
        InitializeComponent();
        InitializeRowReordering();
        SizeChanged += (_, _) => UpdateCompactLayout();
        AddHandler(InputElement.PointerPressedEvent, (_, _) => anchorGeneration++, RoutingStrategies.Tunnel, true);
        AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            if (rowPress is { } start && ReferenceEquals(start.Pointer, e.Pointer) &&
                (Math.Abs(e.GetPosition(this).X - start.Position.X) > 8 ||
                 Math.Abs(e.GetPosition(this).Y - start.Position.Y) > 8))
                rowPress = null;
        }, RoutingStrategies.Tunnel, true);
        AddHandler(InputElement.GotFocusEvent, (_, e) =>
        {
            // Keep keyboard navigation visible without touch focus scrolling a
            // newly expanded channel (which may be taller than the viewport).
            if (e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional && e.Source is Control control)
                control.BringIntoView();
        });
        AddHandler(InputElement.PointerPressedEvent, HandlePttPointerPressed, RoutingStrategies.Tunnel, true);
        AddHandler(InputElement.PointerReleasedEvent, HandlePttPointerReleased, RoutingStrategies.Tunnel, true);
        AddHandler(InputElement.PointerCaptureLostEvent, HandlePttPointerCaptureLost, RoutingStrategies.Bubble, true);
        AddHandler(InputElement.KeyDownEvent, (sender, args) =>
            HandlePttKeyDown(FindPttButton(args.Source), args), RoutingStrategies.Tunnel);
        AddHandler(InputElement.KeyUpEvent, (sender, args) =>
            HandlePttKeyUp(FindPttButton(args.Source), args), RoutingStrategies.Tunnel);
    }

    public void Attach(
        IConsoleApplicationSession session,
        ChannelPttController ptt,
        Func<bool>? useTogglePtt = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(ptt);
        if (DataContext is ConsoleListViewModel)
            throw new InvalidOperationException("The channel List is already attached to a console session.");
        this.useTogglePtt = useTogglePtt ?? (static () => false);
        DataContext = new ConsoleListViewModel(session, ptt);
        UpdateColumns();
    }

    public void RevealChannel(ChannelId id)
    {
        if (DataContext is ConsoleListViewModel model && model.RevealChannel(id) is { } item)
            ListFor(item).ScrollIntoView(item);
    }

    public async ValueTask DetachAsync()
    {
        if (DataContext is not ConsoleListViewModel viewModel)
            return;
        CancelRowDrag();
        rowPress = null;
        anchorGeneration++;
        heldPttPointers.Clear();
        keyboardPttChannel = null;
        StopColumns();
        DataContext = null;
        await viewModel.DisposeAsync();
    }

    private async void HandleReceiveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContextOf(sender) is { } item)
            await ObserveEventActionAsync(() => item.ToggleReceiveAsync());
        e.Handled = true;
    }

    private async void HandleTransmitSelectionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContextOf(sender) is { } item)
            await ObserveEventActionAsync(() => item.ToggleTransmitSelectionAsync());
        e.Handled = true;
    }

    private async void HandlePageSelectionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContextOf(sender) is { } item)
            await ObserveEventActionAsync(() => item.TogglePageSelectionAsync());
        e.Handled = true;
    }

    private async void HandleAlertSelectionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContextOf(sender) is { } item)
            await ObserveEventActionAsync(() => item.ToggleAlertSelectionAsync());
        e.Handled = true;
    }

    private async void HandleRecordingClick(object? sender, RoutedEventArgs e)
    {
        if (DataContextOf(sender) is { } item)
            await ObserveEventActionAsync(() => item.ToggleRecordingAsync());
        e.Handled = true;
    }

    private async void HandleEncryptionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContextOf(sender) is { } item)
            await ObserveEventActionAsync(() => item.ToggleTransmitEncryptionAsync());
        e.Handled = true;
    }

    private void HandleRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        rowPress = null;
        if (sender is Control row && !IsInteractiveSource(e.Source, row) &&
            e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            rowPress = (e.Pointer, row, e.GetPosition(this));
        // Let the ScrollViewer receive the press so a touch drag can scroll.
    }

    private void HandleDisclosureClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control || DataContextOf(sender) is not { } item)
            return;
        Control? row = control.GetVisualAncestors().OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("channel-list-row"));
        e.Handled = true;
        ChangeWithScrollAnchor(row ?? control, item.ToggleExpansion);
    }

    private void HandleGroupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ConsoleListGroupViewModel group } control ||
            DataContext is not ConsoleListViewModel model)
            return;
        e.Handled = true;
        ChangeWithScrollAnchor(control, () => model.ToggleGroup(group));
    }

    private void HandleRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var press = rowPress;
        rowPress = null;
        if (sender is not Control { DataContext: ChannelListItemViewModel item } row ||
            IsInteractiveSource(e.Source, row) || press is not { } start ||
            !ReferenceEquals(start.Pointer, e.Pointer) || !ReferenceEquals(start.Row, row) ||
            (Math.Abs(e.GetPosition(this).X - start.Position.X) > 8 ||
             Math.Abs(e.GetPosition(this).Y - start.Position.Y) > 8))
            return;

        e.Handled = true;
        ChangeWithScrollAnchor(row, item.ToggleExpansion);
    }

    private async void HandleRowDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control { DataContext: ChannelListItemViewModel item } &&
            DataContext is ConsoleListViewModel viewModel)
        {
            await ObserveEventActionAsync(() => viewModel.ReleasePttAsync(item.Id));
        }
    }

    private void HandlePttAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Button button) return;
        button.GetValue(PttAccessibilityBinding.BindingProperty)?.Dispose();
        _ = new PttAccessibilityBinding(button, () => useTogglePtt(),
            () => button.DataContext is ChannelListItemViewModel { IsTransmitting: true },
            () => ActivateAccessiblePttAsync(button, release: false),
            () => ActivateAccessiblePttAsync(button, release: true),
            exception => Trace.TraceError("Accessible PTT failed: {0}", exception));
    }

    private void HandlePttDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Button button) button.GetValue(PttAccessibilityBinding.BindingProperty)?.Dispose();
    }

    private ValueTask ActivateAccessiblePttAsync(Button button, bool release)
    {
        if (button.DataContext is not ChannelListItemViewModel item || DataContext is not ConsoleListViewModel model)
            return ValueTask.CompletedTask;
        return release ? model.UnkeyPttAsync(item.Id) : model.TogglePttAsync(item.Id);
    }

    private async void HandlePttPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Button? button = FindPttButton(e.Source);
        if (button?.DataContext is not ChannelListItemViewModel item ||
            DataContext is not ConsoleListViewModel viewModel ||
            !e.GetCurrentPoint(button).Properties.IsLeftButtonPressed)
        {
            return;
        }
        // PTT owns this touch even if the finger moves. A handled routed event
        // alone does not stop the enclosing ScrollViewer's gesture recognizer.
        e.PreventGestureRecognition();
        e.Handled = true;
        if (useTogglePtt())
        {
            if (item.IsTransmitting)
                await ObserveEventActionAsync(() => viewModel.UnkeyPttAsync(item.Id));
            else
                await ObserveEventActionAsync(() => viewModel.TogglePttAsync(item.Id));
        }
        else
        {
            heldPttPointers.Track(e.Pointer, item.Id);
            e.Pointer.Capture(button);
            await ObserveEventActionAsync(() => viewModel.PressPttAsync(item.Id));
        }
    }

    private async void HandlePttPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        Button? button = e.Pointer.Captured as Button ?? FindPttButton(e.Source);
        ChannelId? heldChannel = heldPttPointers.Take(e.Pointer);
        ChannelId? channelId = heldChannel ??
            (button?.DataContext is ChannelListItemViewModel item ? item.Id : null);
        if ((heldChannel is null && useTogglePtt()) ||
            channelId is null ||
            DataContext is not ConsoleListViewModel viewModel)
        {
            return;
        }
        e.Handled = true;
        e.Pointer.Capture(null);
        await ObserveEventActionAsync(() => viewModel.ReleasePttAsync(channelId.Value));
    }

    private async void HandlePttPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        ChannelId? heldChannel = heldPttPointers.Take(e.Pointer);
        Button? button = FindPttButton(e.Source);
        ChannelId? channelId = heldChannel ??
            (button?.DataContext is ChannelListItemViewModel item ? item.Id : null);
        if ((heldChannel is not null || !useTogglePtt()) &&
            channelId is not null &&
            DataContext is ConsoleListViewModel viewModel)
        {
            await ObserveEventActionAsync(() => viewModel.ReleasePttAsync(channelId.Value));
        }
    }

    private async void HandlePttKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || e.KeyModifiers != KeyModifiers.None ||
            sender is not Button { DataContext: ChannelListItemViewModel item } ||
            DataContext is not ConsoleListViewModel viewModel)
        {
            return;
        }
        e.Handled = true;
        if (keyboardPttChannel is not null)
            return;
        keyboardPttChannel = item.Id;
        keyboardPttMomentary = !useTogglePtt();
        if (useTogglePtt())
        {
            if (item.IsTransmitting)
                await ObserveEventActionAsync(() => viewModel.UnkeyPttAsync(item.Id));
            else
                await ObserveEventActionAsync(() => viewModel.TogglePttAsync(item.Id));
        }
        else
            await ObserveEventActionAsync(() => viewModel.PressPttAsync(item.Id));
    }

    private async void HandlePttKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || keyboardPttChannel is null)
            return;
        e.Handled = true;
        await ReleaseKeyboardPttAsync();
    }

    private async void HandlePttLostFocus(object? sender, RoutedEventArgs e)
        => await ReleaseKeyboardPttAsync();

    private async ValueTask ReleaseKeyboardPttAsync()
    {
        ChannelId? channel = keyboardPttChannel;
        keyboardPttChannel = null;
        if (keyboardPttMomentary && channel is ChannelId id && DataContext is ConsoleListViewModel viewModel)
            await ObserveEventActionAsync(() => viewModel.ReleasePttAsync(id));
    }

    private async void HandleVolumeChanged(object? sender, EventArgs e)
    {
        if (sender is not Slider slider ||
            slider.DataContext is not ChannelListItemViewModel item)
        {
            return;
        }
        await ObserveEventActionAsync(() => item.SetVolumeSliderValueAsync(slider.Value));
    }

    private static async ValueTask ObserveEventActionAsync(Func<ValueTask> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            // A session replacement or shutdown can cancel an in-flight UI action.
        }
        catch (Exception exception)
        {
            // Event handlers cannot return their failure to Avalonia. The
            // controller has already retained truthful operator state; keep
            // the dispatcher alive and make the failure observable to hosts.
            Trace.TraceError("Channel List action failed: {0}", exception);
        }
    }

    private static ChannelListItemViewModel? DataContextOf(object? sender)
        => (sender as Control)?.DataContext as ChannelListItemViewModel;

    private static Button? FindPttButton(object? source)
    {
        for (Visual? visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Button button && button.Classes.Contains("ptt"))
                return button;
        }
        return null;
    }

    private void ChangeWithScrollAnchor(Control row, Action change)
    {
        ScrollViewer? scroller = row.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        Point? before = scroller is null ? null : row.TranslatePoint(default, scroller);
        object? item = row.DataContext;
        change();
        // Complete extent estimation and anchor correction in this input turn,
        // before the compositor can display the intermediate virtualized layout.
        int generation = ++anchorGeneration;
        // Correcting the offset can realize a different set of row heights and
        // change the virtualizer's extent estimate again. Settle those changes
        // before returning from the input event.
        for (int pass = 0; pass < 4; pass++)
        {
            if (!RestoreAnchor()) break;
            UpdateLayout();
        }

        bool RestoreAnchor()
        {
            if (generation != anchorGeneration || DataContext is not ConsoleListViewModel) return false;
            UpdateLayout();
            if (scroller is null || before is null || item is null)
                return false;
            Control? FindAnchor() => this.GetVisualDescendants().OfType<Control>().FirstOrDefault(control =>
                ReferenceEquals(control.DataContext, item) && (control.Classes.Contains("channel-list-row") ||
                    control.Classes.Contains("group-disclosure")));
            Control? anchor = FindAnchor();
            if (anchor is null)
            {
                // A changed estimated row height can recycle a distant virtualized row.
                // Realize the same item before restoring its viewport-relative position.
                ListFor(item).ScrollIntoView(item);
                UpdateLayout();
                anchor = FindAnchor();
            }
            Point? after = anchor?.TranslatePoint(default, scroller);
            if (after is null) return false;
            double maximum = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
            double corrected = Math.Clamp(scroller.Offset.Y + after.Value.Y - before.Value.Y, 0, maximum);
            if (Math.Abs(corrected - scroller.Offset.Y) < 0.01) return false;
            scroller.Offset = new Vector(scroller.Offset.X, corrected);
            return true;
        }
    }

    private static bool IsInteractiveSource(object? source, Control row)
    {
        for (Visual? visual = source as Visual; visual is not null && !ReferenceEquals(visual, row); visual = visual.GetVisualParent())
        {
            if (visual is Button or Slider or TextBox or ComboBox or ToggleSwitch)
                return true;
        }
        return false;
    }

    private void InitializeComponent()
        => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
}
