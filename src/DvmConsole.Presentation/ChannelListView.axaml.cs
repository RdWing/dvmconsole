using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using DvmConsole.Application;

namespace DvmConsole.Presentation;

public sealed partial class ChannelListView : UserControl
{
    private Func<bool> useTogglePtt = static () => false;

    public ChannelListView()
    {
        InitializeComponent();
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
    }

    public async ValueTask DetachAsync()
    {
        if (DataContext is not ConsoleListViewModel viewModel)
            return;
        DataContext = null;
        await viewModel.DisposeAsync();
    }

    private async void HandleReceiveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContextOf(sender) is { } item)
            await item.ToggleReceiveAsync();
        e.Handled = true;
    }

    private async void HandleTransmitSelectionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContextOf(sender) is { } item)
            await item.ToggleTransmitSelectionAsync();
        e.Handled = true;
    }

    private async void HandlePageSelectionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContextOf(sender) is { } item)
            await item.TogglePageSelectionAsync();
        e.Handled = true;
    }

    private async void HandleAlertSelectionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContextOf(sender) is { } item)
            await item.ToggleAlertSelectionAsync();
        e.Handled = true;
    }

    private async void HandleEncryptionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContextOf(sender) is { } item)
            await item.ToggleTransmitEncryptionAsync();
        e.Handled = true;
    }

    private void HandleRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Control row || IsInteractiveSource(e.Source, row))
            return;
        if (row.DataContext is ChannelListItemViewModel item)
            item.ToggleExpansion();
    }

    private async void HandleRowDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control { DataContext: ChannelListItemViewModel item } &&
            DataContext is ConsoleListViewModel viewModel)
        {
            await viewModel.ReleasePttAsync(item.Id);
        }
    }

    private async void HandlePttPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Button button ||
            button.DataContext is not ChannelListItemViewModel item ||
            DataContext is not ConsoleListViewModel viewModel ||
            !e.GetCurrentPoint(button).Properties.IsLeftButtonPressed)
        {
            return;
        }
        e.Handled = true;
        if (useTogglePtt())
            await viewModel.TogglePttAsync(item.Id);
        else
        {
            e.Pointer.Capture(button);
            await viewModel.PressPttAsync(item.Id);
        }
    }

    private async void HandlePttPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (useTogglePtt() || sender is not Button { DataContext: ChannelListItemViewModel item } ||
            DataContext is not ConsoleListViewModel viewModel)
        {
            return;
        }
        e.Handled = true;
        e.Pointer.Capture(null);
        await viewModel.ReleasePttAsync(item.Id);
    }

    private async void HandlePttPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!useTogglePtt() && sender is Button { DataContext: ChannelListItemViewModel item } &&
            DataContext is ConsoleListViewModel viewModel)
        {
            await viewModel.ReleasePttAsync(item.Id);
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
        if (useTogglePtt())
            await viewModel.TogglePttAsync(item.Id);
        else
            await viewModel.PressPttAsync(item.Id);
    }

    private async void HandlePttKeyUp(object? sender, KeyEventArgs e)
    {
        if (useTogglePtt() || e.Key != Key.Space ||
            sender is not Button { DataContext: ChannelListItemViewModel item } ||
            DataContext is not ConsoleListViewModel viewModel)
        {
            return;
        }
        e.Handled = true;
        await viewModel.ReleasePttAsync(item.Id);
    }

    private async void HandleVolumeChanged(object? sender, EventArgs e)
    {
        if (sender is not Slider slider ||
            slider.DataContext is not ChannelListItemViewModel item)
        {
            return;
        }
        await item.SetVolumeSliderValueAsync(slider.Value);
    }

    private static ChannelListItemViewModel? DataContextOf(object? sender)
        => (sender as Control)?.DataContext as ChannelListItemViewModel;

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
