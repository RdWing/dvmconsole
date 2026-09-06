// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace DvmConsole.Desktop;

/// <summary>
/// Owns pointer selection and drag state for the classic zone-card surface.
/// PTT button input remains with the window-level PTT router.
/// </summary>
internal sealed class ChannelCardInteractionController
{
    private readonly Control coordinateRoot;
    private readonly Func<MainWindowViewModel> getViewModel;
    private Control? draggedCard;
    private object? draggedWidget;
    private Point pointerOrigin;
    private double widgetXOrigin;
    private double widgetYOrigin;
    private bool moved;
    private bool toggleAfterClick;

    public ChannelCardInteractionController(
        Control coordinateRoot,
        Func<MainWindowViewModel> getViewModel)
    {
        this.coordinateRoot = coordinateRoot ?? throw new ArgumentNullException(nameof(coordinateRoot));
        this.getViewModel = getViewModel ?? throw new ArgumentNullException(nameof(getViewModel));
    }

    public async void HandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || ChannelCardInput.IsInteractiveSource(e.Source, control))
            return;
        if (control.DataContext is not (ChannelViewModel or WebStreamViewModel))
            return;
        object widget = control.DataContext;

        MainWindowViewModel viewModel = getViewModel();
        PointerPointProperties properties = e.GetCurrentPoint(control).Properties;
        if ((properties.IsLeftButtonPressed || properties.IsRightButtonPressed) && !viewModel.LockWidgets)
        {
            draggedCard = control;
            draggedWidget = widget;
            pointerOrigin = e.GetPosition(coordinateRoot);
            widgetXOrigin = GetWidgetX(widget);
            widgetYOrigin = GetWidgetY(widget);
            moved = false;
            toggleAfterClick = properties.IsLeftButtonPressed;
            ViewportAwareAbsolutePanel.SetIsInteractionPinned(control, true);
            e.Pointer.Capture(control);
            e.Handled = true;
            control.Focus();
            return;
        }

        if (!properties.IsLeftButtonPressed)
            return;
        await ToggleAsync(viewModel, widget);
        control.Focus();
    }

    public void HandlePointerMoved(object? sender, PointerEventArgs e)
    {
        if (draggedCard is null || draggedWidget is null || !ReferenceEquals(sender, draggedCard))
            return;

        Point current = e.GetPosition(coordinateRoot);
        double deltaX = current.X - pointerOrigin.X;
        double deltaY = current.Y - pointerOrigin.Y;
        if (!moved && Math.Abs(deltaX) < 4 && Math.Abs(deltaY) < 4)
            return;

        moved = true;
        const double gridSize = 10;
        double x = Math.Max(0, Math.Round((widgetXOrigin + deltaX) / gridSize) * gridSize);
        double y = Math.Max(0, Math.Round((widgetYOrigin + deltaY) / gridSize) * gridSize);
        MoveWidget(getViewModel(), draggedWidget, x, y, persist: false);
        e.Handled = true;
    }

    public async void HandlePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (draggedCard is null || draggedWidget is null || !ReferenceEquals(sender, draggedCard))
            return;

        object widget = draggedWidget;
        bool cardMoved = moved;
        bool shouldToggle = toggleAfterClick;
        if (cardMoved)
            MoveWidget(
                getViewModel(),
                widget,
                GetWidgetX(widget),
                GetWidgetY(widget),
                persist: true);
        else if (shouldToggle)
            await ToggleAsync(getViewModel(), widget);
        e.Pointer.Capture(null);
        Clear();
        e.Handled = true;
    }

    public void HandlePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (draggedCard is null || !ReferenceEquals(sender, draggedCard))
            return;
        if (moved && draggedWidget is not null)
            FinalizeWidgetPosition(draggedWidget);
        Clear();
    }

    private void Clear()
    {
        if (draggedCard is not null)
            ViewportAwareAbsolutePanel.SetIsInteractionPinned(draggedCard, false);
        draggedCard = null;
        draggedWidget = null;
        moved = false;
        toggleAfterClick = false;
    }

    private static double GetWidgetX(object widget)
        => widget switch
        {
            ChannelViewModel channel => channel.WidgetX,
            WebStreamViewModel stream => stream.WidgetX,
            _ => 0
        };

    private static double GetWidgetY(object widget)
        => widget switch
        {
            ChannelViewModel channel => channel.WidgetY,
            WebStreamViewModel stream => stream.WidgetY,
            _ => 0
        };

    private static void MoveWidget(
        MainWindowViewModel viewModel,
        object widget,
        double x,
        double y,
        bool persist)
    {
        switch (widget)
        {
            case ChannelViewModel channel:
                viewModel.MoveChannelWidget(channel, x, y, persist);
                break;
            case WebStreamViewModel stream:
                viewModel.MoveWebStreamWidget(stream, x, y, persist);
                break;
        }
    }

    private static Task ToggleAsync(MainWindowViewModel viewModel, object widget)
        => widget switch
        {
            ChannelViewModel channel => viewModel.ToggleChannelReceiveAsync(channel),
            WebStreamViewModel stream => stream.ToggleAsync(),
            _ => Task.CompletedTask
        };

    private static void FinalizeWidgetPosition(object widget)
    {
        switch (widget)
        {
            case ChannelViewModel channel:
                channel.SetWidgetPosition(channel.WidgetX, channel.WidgetY, isFinal: true);
                break;
            case WebStreamViewModel stream:
                stream.SetWidgetPosition(stream.WidgetX, stream.WidgetY, isFinal: true);
                break;
        }
    }
}
