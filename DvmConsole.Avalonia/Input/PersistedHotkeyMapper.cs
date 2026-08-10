// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using DvmConsole.Platform.Hotkeys;

namespace DvmConsole.Avalonia.Input
{
    /// <summary>
    /// Pure managed decoder for the persisted WPF hotkey integer that
    /// the WPF shell stored in <c>UserSettingsPttSection</c>. The WPF
    /// shell persisted the raw <c>System.Windows.Forms.Keys</c> value,
    /// whose integer shape is: the low 16 bits are the virtual-key code
    /// (<c>Keys.KeyCode</c> = 0xFFFF) and the high 16 bits are the
    /// modifier flags (<c>Keys.Modifiers</c> = 0xFFFF0000), with
    /// Shift = 0x10000, Control = 0x20000, and Alt = 0x40000. There is
    /// no Meta modifier in <c>System.Windows.Forms.Keys</c>, so no Meta
    /// mapping exists here; the LWin/RWin key codes are outside the
    /// supported operator matrix.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This type is the decode half of the persisted-hotkey seam. The
    /// composition boundary is deliberately deferred: the later
    /// PttSettingsPersistence adapter loads the raw integer from the
    /// settings section, this mapper decodes it into a portable
    /// <see cref="HotkeyGesture"/>, and MainWindow / the
    /// HotkeyRegistrationCoordinator register that gesture with the
    /// platform. Persistence, UI, and registration must not leak into
    /// this type.
    /// </para>
    /// <para>
    /// The type is pure managed: it has no reference to
    /// System.Windows.Forms, and no Avalonia/UI, native, network, or
    /// persistence dependencies, so it can be driven headlessly. The
    /// supported key-code matrix matches <see cref="KeyGestureMapper"/>
    /// / the operator HotkeyKey matrix: A-Z (0x41-0x5A), D0-D9
    /// (0x30-0x39), F1-F24 (0x70-0x87), Enter (0x0D), Escape (0x1B),
    /// Tab (0x09), Space (0x20), Backspace (0x08), Delete (0x2E),
    /// Insert (0x2D), Home (0x24), End (0x23), PageUp (0x21), PageDown
    /// (0x22), Left (0x25), Right (0x27), Up (0x26), Down (0x28).
    /// </para>
    /// </remarks>
    public static class PersistedHotkeyMapper
    {
        // The masks and modifier bits mirror System.Windows.Forms.Keys
        // (Keys.KeyCode = 0xFFFF, Keys.Modifiers = 0xFFFF0000,
        // Keys.Shift = 0x10000, Keys.Control = 0x20000,
        // Keys.Alt = 0x40000) without referencing that assembly.

        /// <summary>The low 16-bit key-code mask (Keys.KeyCode).</summary>
        private const int KeyCodeMask = 0xFFFF;

        /// <summary>The high 16-bit modifier mask (Keys.Modifiers).</summary>
        private const int ModifierMask = unchecked((int)0xFFFF0000);

        /// <summary>The Shift modifier bit (Keys.Shift).</summary>
        private const int ShiftBit = 0x10000;

        /// <summary>The Control modifier bit (Keys.Control).</summary>
        private const int ControlBit = 0x20000;

        /// <summary>The Alt modifier bit (Keys.Alt).</summary>
        private const int AltBit = 0x40000;

        /// <summary>Every supported modifier bit, OR-ed together.</summary>
        private const int SupportedModifiers = ShiftBit | ControlBit | AltBit;

        // The WPF virtual-key codes of the supported named keys; the
        // A-Z, D0-D9, and F1-F24 ranges are handled arithmetically.

        private const int WpfA = 0x41;
        private const int WpfZ = 0x5A;
        private const int WpfD0 = 0x30;
        private const int WpfD9 = 0x39;
        private const int WpfF1 = 0x70;
        private const int WpfF24 = 0x87;

        /// <summary>
        /// Tries to decode a persisted WPF <c>System.Windows.Forms.Keys</c>
        /// integer into a portable <see cref="HotkeyGesture"/>. The out
        /// gesture is initialized to its zero value (None/None) on every
        /// call. Returns false for a zero key code, for any modifier bit
        /// beyond Shift/Control/Alt (there is no persisted Meta
        /// modifier), and for any key code outside the supported
        /// operator matrix.
        /// </summary>
        /// <param name="persistedKeys">
        /// The raw integer stored by the WPF shell: low 16 bits are the
        /// virtual-key code, high 16 bits are the Shift (0x10000),
        /// Control (0x20000), and Alt (0x40000) modifier flags.
        /// </param>
        /// <param name="gesture">The mapped gesture, or the zero gesture when rejected.</param>
        /// <returns>True when the value decodes to a supported gesture.</returns>
        public static bool TryMap(int persistedKeys, out HotkeyGesture gesture)
        {
            gesture = default;

            var keyCode = persistedKeys & KeyCodeMask;
            var modifiers = persistedKeys & ModifierMask;

            if (keyCode == 0)
            {
                return false;
            }

            if ((modifiers & ~SupportedModifiers) != 0)
            {
                return false;
            }

            if (!TryMapKey(keyCode, out var hotkeyKey))
            {
                return false;
            }

            var hotkeyModifiers = HotkeyModifiers.None;
            if ((modifiers & ShiftBit) != 0)
            {
                hotkeyModifiers |= HotkeyModifiers.Shift;
            }

            if ((modifiers & ControlBit) != 0)
            {
                hotkeyModifiers |= HotkeyModifiers.Control;
            }

            if ((modifiers & AltBit) != 0)
            {
                hotkeyModifiers |= HotkeyModifiers.Alt;
            }

            gesture = new HotkeyGesture(hotkeyKey, hotkeyModifiers);
            return true;
        }

        /// <summary>
        /// Maps a WPF virtual-key code to its <see cref="HotkeyKey"/>:
        /// the A-Z, D0-D9, and F1-F24 ranges are contiguous in both the
        /// WPF codes and the HotkeyKey enum (verified against the
        /// referenced assemblies), so they translate by offset; the
        /// named keys map one-to-one. Any other code — including the
        /// modifier-only keys, LWin/RWin, the plus operator key, and
        /// the keypad zero (0x60) — is unsupported.
        /// </summary>
        private static bool TryMapKey(int keyCode, out HotkeyKey hotkeyKey)
        {
            if (keyCode >= WpfA && keyCode <= WpfZ)
            {
                hotkeyKey = (HotkeyKey)((int)HotkeyKey.A + (keyCode - WpfA));
                return true;
            }

            if (keyCode >= WpfD0 && keyCode <= WpfD9)
            {
                hotkeyKey = (HotkeyKey)((int)HotkeyKey.D0 + (keyCode - WpfD0));
                return true;
            }

            if (keyCode >= WpfF1 && keyCode <= WpfF24)
            {
                hotkeyKey = (HotkeyKey)((int)HotkeyKey.F1 + (keyCode - WpfF1));
                return true;
            }

            switch (keyCode)
            {
                case 0x0D: hotkeyKey = HotkeyKey.Enter; return true;
                case 0x1B: hotkeyKey = HotkeyKey.Escape; return true;
                case 0x09: hotkeyKey = HotkeyKey.Tab; return true;
                case 0x20: hotkeyKey = HotkeyKey.Space; return true;
                case 0x08: hotkeyKey = HotkeyKey.Backspace; return true;
                case 0x2E: hotkeyKey = HotkeyKey.Delete; return true;
                case 0x2D: hotkeyKey = HotkeyKey.Insert; return true;
                case 0x24: hotkeyKey = HotkeyKey.Home; return true;
                case 0x23: hotkeyKey = HotkeyKey.End; return true;
                case 0x21: hotkeyKey = HotkeyKey.PageUp; return true;
                case 0x22: hotkeyKey = HotkeyKey.PageDown; return true;
                case 0x25: hotkeyKey = HotkeyKey.Left; return true;
                case 0x27: hotkeyKey = HotkeyKey.Right; return true;
                case 0x26: hotkeyKey = HotkeyKey.Up; return true;
                case 0x28: hotkeyKey = HotkeyKey.Down; return true;
                default: hotkeyKey = HotkeyKey.None; return false;
            }
        }
    }
}
