// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using DvmConsole.Platform.Hotkeys;

namespace DvmConsole.Avalonia.Input
{
    /// <summary>
    /// Pure managed encoder that maps a portable <see cref="HotkeyGesture"/>
    /// onto the raw WPF hotkey integer that the WPF shell persisted in
    /// <c>UserSettingsPttSection</c>. The WPF shell stored the raw
    /// <c>System.Windows.Forms.Keys</c> value, whose integer shape is: the
    /// low 16 bits are the virtual-key code (<c>Keys.KeyCode</c> = 0xFFFF)
    /// and the high 16 bits are the modifier flags (<c>Keys.Modifiers</c>
    /// = 0xFFFF0000), with Shift = 0x10000, Control = 0x20000, and
    /// Alt = 0x40000. There is no Meta modifier in
    /// <c>System.Windows.Forms.Keys</c>, so <see cref="HotkeyModifiers.Meta"/>
    /// cannot be encoded and is rejected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This type is the encode half of the persisted-hotkey seam and the
    /// inverse of <see cref="PersistedHotkeyMapper"/>: the mapper decodes
    /// the persisted integer into a <see cref="HotkeyGesture"/>, and this
    /// encoder produces the integer that a later two-way settings
    /// composition can hand to PttSettingsPersistence for save. That
    /// two-way settings wiring is deliberately deferred — persistence,
    /// UI, and registration must not leak into this type.
    /// </para>
    /// <para>
    /// The type is pure managed: it has no reference to
    /// System.Windows.Forms, and no Avalonia/UI, native, network, or
    /// persistence dependencies, so it can be driven headlessly. The
    /// supported key matrix matches <see cref="PersistedHotkeyMapper"/>
    /// and <see cref="KeyGestureMapper"/> / the operator HotkeyKey matrix:
    /// A-Z (0x41-0x5A), D0-D9 (0x30-0x39), F1-F24 (0x70-0x87), Enter (0x0D),
    /// Escape (0x1B), Tab (0x09), Space (0x20), Backspace (0x08), Delete
    /// (0x2E), Insert (0x2D), Home (0x24), End (0x23), PageUp (0x21),
    /// PageDown (0x22), Left (0x25), Right (0x27), Up (0x26), Down (0x28).
    /// </para>
    /// </remarks>
    public static class PersistedHotkeyEncoder
    {
        // The modifier bits mirror System.Windows.Forms.Keys
        // (Keys.Shift = 0x10000, Keys.Control = 0x20000,
        // Keys.Alt = 0x40000) without referencing that assembly. The
        // portable HotkeyModifiers enum uses different flag values
        // (Alt = 1, Control = 2, Shift = 4, Meta = 8), so the encoder
        // translates between the two layouts.

        /// <summary>The Shift modifier bit (Keys.Shift).</summary>
        private const int ShiftBit = 0x10000;

        /// <summary>The Control modifier bit (Keys.Control).</summary>
        private const int ControlBit = 0x20000;

        /// <summary>The Alt modifier bit (Keys.Alt).</summary>
        private const int AltBit = 0x40000;

        /// <summary>
        /// Every portable modifier flag that has a persisted WPF
        /// equivalent: Alt, Control, and Shift.
        /// </summary>
        private const HotkeyModifiers SupportedModifiers =
            HotkeyModifiers.Alt | HotkeyModifiers.Control | HotkeyModifiers.Shift;

        // The WPF virtual-key codes of the supported named keys; the
        // A-Z, D0-D9, and F1-F24 ranges are handled arithmetically.

        private const int WpfA = 0x41;
        private const int WpfD0 = 0x30;
        private const int WpfF1 = 0x70;

        /// <summary>
        /// Tries to encode a portable <see cref="HotkeyGesture"/> into a
        /// persisted WPF <c>System.Windows.Forms.Keys</c> integer: the low
        /// 16 bits are the virtual-key code and the high 16 bits carry the
        /// Shift (0x10000), Control (0x20000), and Alt (0x40000) modifier
        /// flags. The out integer is initialized to zero on every call.
        /// Returns false for <see cref="HotkeyKey.None"/>, for any key
        /// outside the supported operator matrix, and for any modifier bit
        /// beyond Alt/Control/Shift — in particular
        /// <see cref="HotkeyModifiers.Meta"/>, which has no persisted WPF
        /// equivalent.
        /// </summary>
        /// <param name="gesture">The gesture to encode.</param>
        /// <param name="persistedKeys">
        /// The WPF hotkey integer, or zero when the gesture is rejected.
        /// </param>
        /// <returns>True when the gesture encodes to a supported persisted value.</returns>
        public static bool TryMap(HotkeyGesture gesture, out int persistedKeys)
        {
            persistedKeys = 0;

            var modifiers = gesture.Modifiers;
            if ((modifiers & ~SupportedModifiers) != 0)
            {
                return false;
            }

            if (!TryMapKey(gesture.Key, out var keyCode))
            {
                return false;
            }

            var modifierBits = 0;
            if ((modifiers & HotkeyModifiers.Shift) != 0)
            {
                modifierBits |= ShiftBit;
            }

            if ((modifiers & HotkeyModifiers.Control) != 0)
            {
                modifierBits |= ControlBit;
            }

            if ((modifiers & HotkeyModifiers.Alt) != 0)
            {
                modifierBits |= AltBit;
            }

            persistedKeys = keyCode | modifierBits;
            return true;
        }

        /// <summary>
        /// Maps a <see cref="HotkeyKey"/> to its WPF virtual-key code:
        /// the A-Z, D0-D9, and F1-F24 ranges are contiguous in both the
        /// HotkeyKey enum and the WPF codes (verified against the
        /// referenced assemblies), so they translate by offset; the
        /// named keys map one-to-one. Any other key — including
        /// <see cref="HotkeyKey.None"/> — is unsupported.
        /// </summary>
        private static bool TryMapKey(HotkeyKey key, out int wpfKeyCode)
        {
            if (key >= HotkeyKey.A && key <= HotkeyKey.Z)
            {
                wpfKeyCode = WpfA + ((int)key - (int)HotkeyKey.A);
                return true;
            }

            if (key >= HotkeyKey.D0 && key <= HotkeyKey.D9)
            {
                wpfKeyCode = WpfD0 + ((int)key - (int)HotkeyKey.D0);
                return true;
            }

            if (key >= HotkeyKey.F1 && key <= HotkeyKey.F24)
            {
                wpfKeyCode = WpfF1 + ((int)key - (int)HotkeyKey.F1);
                return true;
            }

            switch (key)
            {
                case HotkeyKey.Enter: wpfKeyCode = 0x0D; return true;
                case HotkeyKey.Escape: wpfKeyCode = 0x1B; return true;
                case HotkeyKey.Tab: wpfKeyCode = 0x09; return true;
                case HotkeyKey.Space: wpfKeyCode = 0x20; return true;
                case HotkeyKey.Backspace: wpfKeyCode = 0x08; return true;
                case HotkeyKey.Delete: wpfKeyCode = 0x2E; return true;
                case HotkeyKey.Insert: wpfKeyCode = 0x2D; return true;
                case HotkeyKey.Home: wpfKeyCode = 0x24; return true;
                case HotkeyKey.End: wpfKeyCode = 0x23; return true;
                case HotkeyKey.PageUp: wpfKeyCode = 0x21; return true;
                case HotkeyKey.PageDown: wpfKeyCode = 0x22; return true;
                case HotkeyKey.Left: wpfKeyCode = 0x25; return true;
                case HotkeyKey.Right: wpfKeyCode = 0x27; return true;
                case HotkeyKey.Up: wpfKeyCode = 0x26; return true;
                case HotkeyKey.Down: wpfKeyCode = 0x28; return true;
                default: wpfKeyCode = 0; return false;
            }
        }
    }
}
