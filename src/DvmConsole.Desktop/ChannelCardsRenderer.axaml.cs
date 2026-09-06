// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DvmConsole.Desktop;

internal sealed partial class ChannelCardsRenderer : UserControl
{
    public ChannelCardsRenderer()
    {
        InitializeComponent();
    }

    public event EventHandler<PointerPressedEventArgs>? ChannelPointerPressed;
    public event EventHandler<PointerEventArgs>? ChannelPointerMoved;
    public event EventHandler<PointerReleasedEventArgs>? ChannelPointerReleased;
    public event EventHandler<PointerCaptureLostEventArgs>? ChannelPointerCaptureLost;
    public event EventHandler<RoutedEventArgs>? TransmitSelectionClick;
    public event EventHandler<RoutedEventArgs>? PageSelectionClick;
    public event EventHandler<RoutedEventArgs>? AlertSelectionClick;

    private void HandleChannelPointerPressed(object? sender, PointerPressedEventArgs e)
        => ChannelPointerPressed?.Invoke(sender, e);

    private void HandleChannelPointerMoved(object? sender, PointerEventArgs e)
        => ChannelPointerMoved?.Invoke(sender, e);

    private void HandleChannelPointerReleased(object? sender, PointerReleasedEventArgs e)
        => ChannelPointerReleased?.Invoke(sender, e);

    private void HandleChannelPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => ChannelPointerCaptureLost?.Invoke(sender, e);

    private void HandleTransmitSelectionClick(object? sender, RoutedEventArgs e)
        => TransmitSelectionClick?.Invoke(sender, e);

    private void HandlePageSelectionClick(object? sender, RoutedEventArgs e)
        => PageSelectionClick?.Invoke(sender, e);

    private void HandleAlertSelectionClick(object? sender, RoutedEventArgs e)
        => AlertSelectionClick?.Invoke(sender, e);

    private void HandleNavigationSelectionChanged(object? sender, SelectionChangedEventArgs e)
        => Dispatcher.UIThread.Post(RevealCurrentChannel, DispatcherPriority.Loaded);

    private void RevealCurrentChannel()
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        SystemViewModel? selectedSystem = viewModel.SelectedSystem;
        ZoneViewModel? selectedZone = selectedSystem?.SelectedZone;
        if (selectedZone is null)
            return;

        ChannelViewModel? channel = selectedZone.Channels.FirstOrDefault(candidate => candidate.IsTransmitting);
        if (channel is null && viewModel.SelectedChannel is { } selected &&
            selectedZone.Channels.Contains(selected))
        {
            channel = selected;
        }
        channel ??= selectedZone.Channels.Count > 0 ? selectedZone.Channels[0] : null;
        if (channel is null)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            ViewportAwareAbsolutePanel? panel = this.GetVisualDescendants()
                .OfType<ViewportAwareAbsolutePanel>()
                .FirstOrDefault();
            panel?.Reveal(channel);
        }, DispatcherPriority.Loaded);
    }
}
