using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using DvmConsole.Audio;
using DvmConsole.Ptt;

namespace DvmConsole.Desktop;

internal sealed class WindowPttKeyRouter
{
    private readonly Func<MainWindowViewModel> getViewModel;
    private bool spaceInputSuppressed;

    public WindowPttKeyRouter(Func<MainWindowViewModel> getViewModel)
    {
        this.getViewModel = getViewModel ?? throw new ArgumentNullException(nameof(getViewModel));
    }

    public bool TryHandleKeyDown(Key key, out bool handled)
        => TryHandle(key, pressed: true, out handled);

    public bool TryHandleKeyUp(Key key, out bool handled)
        => TryHandle(key, pressed: false, out handled);

    public static bool TryMap(Key key, out KeyboardPttKey pttKey)
    {
        pttKey = key switch
        {
            Key.Space => KeyboardPttKey.Space,
            Key.F1 => KeyboardPttKey.F1,
            Key.F2 => KeyboardPttKey.F2,
            Key.F3 => KeyboardPttKey.F3,
            Key.F4 => KeyboardPttKey.F4,
            Key.F5 => KeyboardPttKey.F5,
            Key.F6 => KeyboardPttKey.F6,
            Key.F7 => KeyboardPttKey.F7,
            Key.F8 => KeyboardPttKey.F8,
            Key.F9 => KeyboardPttKey.F9,
            Key.F10 => KeyboardPttKey.F10,
            Key.F11 => KeyboardPttKey.F11,
            Key.F12 => KeyboardPttKey.F12,
            Key.F13 => KeyboardPttKey.F13,
            Key.F14 => KeyboardPttKey.F14,
            Key.F15 => KeyboardPttKey.F15,
            Key.F16 => KeyboardPttKey.F16,
            Key.F17 => KeyboardPttKey.F17,
            Key.F18 => KeyboardPttKey.F18,
            Key.F19 => KeyboardPttKey.F19,
            _ => default
        };
        return key is Key.Space or (>= Key.F1 and <= Key.F19);
    }

    public void UpdateInputFocus(object? focusedElement, bool isWindowActive = true)
    {
        bool suppressed = WindowPttInputGuard.ShouldSuppressSpacePtt(
            focusedElement,
            isWindowActive);
        if (spaceInputSuppressed == suppressed)
            return;
        spaceInputSuppressed = suppressed;
        getViewModel().SetSpacePttInputSuppressed(suppressed);
    }

    private bool TryHandle(Key key, bool pressed, out bool handled)
    {
        if (!TryMap(key, out KeyboardPttKey pttKey))
        {
            handled = false;
            return false;
        }

        if (pttKey == KeyboardPttKey.Space && spaceInputSuppressed)
        {
            handled = false;
            return true;
        }

        MainWindowViewModel viewModel = getViewModel();
        bool stateChanged = pressed
            ? viewModel.HandleKeyboardPttDown(pttKey)
            : viewModel.HandleKeyboardPttUp(pttKey);
        handled = stateChanged || viewModel.IsConfiguredPttKey(pttKey);
        return true;
    }
}

internal static class WindowPttInputGuard
{
    public static bool ShouldSuppressSpacePtt(object? focusedElement, bool isWindowActive)
        => isWindowActive && ShouldSuppressSpacePtt(focusedElement);

    public static bool ShouldSuppressSpacePtt(object? focusedElement)
    {
        if (IsInsideChannelPttSurface(focusedElement))
            return false;

        for (Visual? visual = focusedElement as Visual;
             visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is TextBox or
                Button or
                SelectingItemsControl or
                Slider or
                ScrollBar or
                NumericUpDown or
                DatePicker or
                TimePicker or
                MenuItem or
                TabItem)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInsideChannelPttSurface(object? focusedElement)
    {
        for (Visual? visual = focusedElement as Visual;
             visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is Control control &&
                (control.Classes.Contains("ptt") ||
                 control.Classes.Contains("channel-list") ||
                 control.Classes.Contains("channel-card")))
            {
                return true;
            }
        }

        return false;
    }
}
