// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using Avalonia.Input;
using DvmConsole.Platform.Hotkeys;

namespace DvmConsole.Avalonia.Input
{
    /// <summary>
    /// Pure managed translation layer that maps an Avalonia key event
    /// onto the platform hotkey-gesture contract. The supported matrix
    /// is locked to the operator hotkey surface: A-Z, D0-D9, F1-F24, and
    /// the fifteen named navigation/editing keys; the modifier-only
    /// keys, the plus operator key, and the keypad zero are rejected.
    /// Avalonia names the backspace key <see cref="Key.Back"/>, so that
    /// key maps to <see cref="HotkeyKey.Backspace"/> — the only name
    /// mismatch between the two enums. Avalonia modifiers Alt/Control/
    /// Shift/Meta map to the same-named <see cref="HotkeyModifiers"/>
    /// flags (the enums share the same flag bits), and any other
    /// modifier bit is rejected. This type is deliberately free of
    /// mutation, native, network, and persistence behavior so it can be
    /// driven headlessly.
    /// </summary>
    public static class KeyGestureMapper
    {
        /// <summary>
        /// Tries to translate an Avalonia key and modifier combination
        /// into a <see cref="HotkeyGesture"/>. The out gesture is
        /// initialized to its zero value (None/None) on every call.
        /// Returns false for <see cref="Key.None"/>, the modifier-only
        /// keys (LeftCtrl/RightCtrl/LeftShift/RightShift/LeftAlt/RightAlt/
        /// LWin/RWin), <see cref="Key.OemPlus"/>, <see cref="Key.NumPad0"/>,
        /// any other key outside the supported matrix, and any modifier
        /// combination carrying bits beyond Alt/Control/Shift/Meta.
        /// </summary>
        /// <param name="key">The pressed key.</param>
        /// <param name="modifiers">The modifier flags held during the press.</param>
        /// <param name="gesture">The mapped gesture, or the zero gesture when rejected.</param>
        /// <returns>True when the key and modifiers map to a supported gesture.</returns>
        public static bool TryMap(
            Key key,
            KeyModifiers modifiers,
            out HotkeyGesture gesture)
        {
            gesture = default;

            if (IsRejectedKey(key))
            {
                return false;
            }

            if (!TryMapKey(key, out var hotkeyKey))
            {
                return false;
            }

            // The Avalonia and platform modifier enums share the same
            // flag bits (Alt=1, Control=2, Shift=4, Meta=8); anything
            // beyond those four flags is not a supported modifier.
            var supported = KeyModifiers.Alt
                | KeyModifiers.Control
                | KeyModifiers.Shift
                | KeyModifiers.Meta;
            if ((modifiers & ~supported) != 0)
            {
                return false;
            }

            gesture = new HotkeyGesture(hotkeyKey, (HotkeyModifiers)modifiers);
            return true;
        }

        /// <summary>
        /// The explicitly rejected keys: none, the modifier-only keys,
        /// the plus operator key, and the keypad zero. These stay
        /// rejected regardless of the modifiers supplied.
        /// </summary>
        private static bool IsRejectedKey(Key key)
        {
            switch (key)
            {
                case Key.None:
                case Key.LeftCtrl:
                case Key.RightCtrl:
                case Key.LeftShift:
                case Key.RightShift:
                case Key.LeftAlt:
                case Key.RightAlt:
                case Key.LWin:
                case Key.RWin:
                case Key.OemPlus:
                case Key.NumPad0:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Maps a supported key to its <see cref="HotkeyKey"/>: the
        /// A-Z, D0-D9, and F1-F24 ranges are contiguous in both enums
        /// (verified against the referenced assemblies), so they
        /// translate by offset; the named keys map one-to-one, with
        /// <see cref="Key.Back"/> the sole name mismatch
        /// (<see cref="HotkeyKey.Backspace"/>). Any other key is
        /// unsupported.
        /// </summary>
        private static bool TryMapKey(Key key, out HotkeyKey hotkeyKey)
        {
            if (key >= Key.A && key <= Key.Z)
            {
                hotkeyKey = (HotkeyKey)((int)HotkeyKey.A + ((int)key - (int)Key.A));
                return true;
            }

            if (key >= Key.D0 && key <= Key.D9)
            {
                hotkeyKey = (HotkeyKey)((int)HotkeyKey.D0 + ((int)key - (int)Key.D0));
                return true;
            }

            if (key >= Key.F1 && key <= Key.F24)
            {
                hotkeyKey = (HotkeyKey)((int)HotkeyKey.F1 + ((int)key - (int)Key.F1));
                return true;
            }

            switch (key)
            {
                case Key.Enter: hotkeyKey = HotkeyKey.Enter; return true;
                case Key.Escape: hotkeyKey = HotkeyKey.Escape; return true;
                case Key.Tab: hotkeyKey = HotkeyKey.Tab; return true;
                case Key.Space: hotkeyKey = HotkeyKey.Space; return true;
                case Key.Back: hotkeyKey = HotkeyKey.Backspace; return true;
                case Key.Delete: hotkeyKey = HotkeyKey.Delete; return true;
                case Key.Insert: hotkeyKey = HotkeyKey.Insert; return true;
                case Key.Home: hotkeyKey = HotkeyKey.Home; return true;
                case Key.End: hotkeyKey = HotkeyKey.End; return true;
                case Key.PageUp: hotkeyKey = HotkeyKey.PageUp; return true;
                case Key.PageDown: hotkeyKey = HotkeyKey.PageDown; return true;
                case Key.Left: hotkeyKey = HotkeyKey.Left; return true;
                case Key.Right: hotkeyKey = HotkeyKey.Right; return true;
                case Key.Up: hotkeyKey = HotkeyKey.Up; return true;
                case Key.Down: hotkeyKey = HotkeyKey.Down; return true;
                default: hotkeyKey = HotkeyKey.None; return false;
            }
        }
    }
}
