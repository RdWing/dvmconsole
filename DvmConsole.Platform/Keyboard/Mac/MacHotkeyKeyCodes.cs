// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System.Collections.Generic;

namespace DvmConsole.Platform.Hotkeys.Mac
{
    /// <summary>
    /// Pure macOS virtual-key mapping surface for global hotkeys: BCL-only,
    /// no native calls. Maps every supported <see cref="HotkeyKey"/> to a
    /// distinct ANSI kVK code (and back), and maps the supported
    /// <see cref="HotkeyModifiers"/> to their CGEventFlags bits (and back).
    /// </summary>
    public static class MacHotkeyKeyCodes
    {
        /// <summary>
        /// The CGEventFlags bits that carry the supported modifier state:
        /// Shift (1 &lt;&lt; 17), Control (1 &lt;&lt; 18), Alt/Option
        /// (1 &lt;&lt; 19) and Meta/Command (1 &lt;&lt; 20). All other flag
        /// bits (caps lock, numeric keypad, device-independent bits, ...) are
        /// ignored when matching modifier state.
        /// </summary>
        public static readonly ulong SupportedModifierMask =
            (1ul << 17) | (1ul << 18) | (1ul << 19) | (1ul << 20);

        private static readonly Dictionary<HotkeyKey, ushort> KeyToCode = new()
        {
            // ANSI letters (kVK_ANSI_*).
            [HotkeyKey.A] = 0x00,
            [HotkeyKey.S] = 0x01,
            [HotkeyKey.D] = 0x02,
            [HotkeyKey.F] = 0x03,
            [HotkeyKey.H] = 0x04,
            [HotkeyKey.G] = 0x05,
            [HotkeyKey.Z] = 0x06,
            [HotkeyKey.X] = 0x07,
            [HotkeyKey.C] = 0x08,
            [HotkeyKey.V] = 0x09,
            [HotkeyKey.B] = 0x0B,
            [HotkeyKey.Q] = 0x0C,
            [HotkeyKey.W] = 0x0D,
            [HotkeyKey.E] = 0x0E,
            [HotkeyKey.R] = 0x0F,
            [HotkeyKey.Y] = 0x10,
            [HotkeyKey.T] = 0x11,
            [HotkeyKey.O] = 0x1F,
            [HotkeyKey.U] = 0x20,
            [HotkeyKey.I] = 0x22,
            [HotkeyKey.P] = 0x23,
            [HotkeyKey.L] = 0x25,
            [HotkeyKey.J] = 0x26,
            [HotkeyKey.K] = 0x28,
            [HotkeyKey.N] = 0x2D,
            [HotkeyKey.M] = 0x2E,

            // ANSI digits (kVK_ANSI_*).
            [HotkeyKey.D1] = 0x12,
            [HotkeyKey.D2] = 0x13,
            [HotkeyKey.D3] = 0x14,
            [HotkeyKey.D4] = 0x15,
            [HotkeyKey.D6] = 0x16,
            [HotkeyKey.D5] = 0x17,
            [HotkeyKey.D9] = 0x19,
            [HotkeyKey.D7] = 0x1A,
            [HotkeyKey.D8] = 0x1C,
            [HotkeyKey.D0] = 0x1D,

            // Named keys (kVK_Return, kVK_Tab, ...).
            [HotkeyKey.Enter] = 0x24,
            [HotkeyKey.Tab] = 0x30,
            [HotkeyKey.Space] = 0x31,
            [HotkeyKey.Backspace] = 0x33, // kVK_Delete (backspace)
            [HotkeyKey.Escape] = 0x35,

            // Function keys (kVK_F1..kVK_F24).
            [HotkeyKey.F1] = 0x7A,
            [HotkeyKey.F2] = 0x78,
            [HotkeyKey.F3] = 0x63,
            [HotkeyKey.F4] = 0x76,
            [HotkeyKey.F5] = 0x60,
            [HotkeyKey.F6] = 0x61,
            [HotkeyKey.F7] = 0x62,
            [HotkeyKey.F8] = 0x64,
            [HotkeyKey.F9] = 0x65,
            [HotkeyKey.F10] = 0x6D,
            [HotkeyKey.F11] = 0x67,
            [HotkeyKey.F12] = 0x6F,
            [HotkeyKey.F13] = 0x69,
            [HotkeyKey.F14] = 0x6B,
            [HotkeyKey.F15] = 0x71,
            [HotkeyKey.F16] = 0x6A,
            [HotkeyKey.F17] = 0x40,
            [HotkeyKey.F18] = 0x4F,
            [HotkeyKey.F19] = 0x50,
            [HotkeyKey.F20] = 0x5A,
            [HotkeyKey.F21] = 0x5D,
            [HotkeyKey.F22] = 0x5E,
            [HotkeyKey.F23] = 0x5F,
            [HotkeyKey.F24] = 0x68,

            // Cursor/navigation keys (kVK_Help, kVK_Home, kVK_PageUp,
            // kVK_ForwardDelete, kVK_End, kVK_PageDown, kVK_Left, kVK_Right,
            // kVK_Down, kVK_Up). Insert maps to the Help key, which is the
            // standard ANSI insert position.
            [HotkeyKey.Insert] = 0x72, // kVK_Help
            [HotkeyKey.Home] = 0x73,
            [HotkeyKey.PageUp] = 0x74,
            [HotkeyKey.Delete] = 0x75, // kVK_ForwardDelete
            [HotkeyKey.End] = 0x77,
            [HotkeyKey.PageDown] = 0x79,
            [HotkeyKey.Left] = 0x7B,
            [HotkeyKey.Right] = 0x7C,
            [HotkeyKey.Down] = 0x7D,
            [HotkeyKey.Up] = 0x7E,
        };

        private static readonly Dictionary<ushort, HotkeyKey> CodeToKey = BuildReverseMap(KeyToCode);

        /// <summary>
        /// Maps a <see cref="HotkeyKey"/> to its ANSI kVK code. Returns false
        /// (and leaves <paramref name="keyCode"/> untouched) for
        /// <see cref="HotkeyKey.None"/> and any other unmappable key.
        /// </summary>
        public static bool TryGetKeyCode(HotkeyKey key, out ushort keyCode)
            => KeyToCode.TryGetValue(key, out keyCode);

        /// <summary>
        /// Maps an ANSI kVK code back to its <see cref="HotkeyKey"/>. Returns
        /// false for codes that no supported key maps to.
        /// </summary>
        public static bool TryGetHotkeyKey(ushort keyCode, out HotkeyKey key)
            => CodeToKey.TryGetValue(keyCode, out key);

        /// <summary>
        /// Maps the supported <see cref="HotkeyModifiers"/> to their
        /// CGEventFlags bits: Alt to 1 &lt;&lt; 19, Control to 1 &lt;&lt; 18,
        /// Shift to 1 &lt;&lt; 17, Meta to 1 &lt;&lt; 20. Unsupported flag
        /// values in <paramref name="modifiers"/> are ignored.
        /// </summary>
        public static ulong GetEventFlags(HotkeyModifiers modifiers)
        {
            var flags = 0ul;
            if ((modifiers & HotkeyModifiers.Alt) != 0)
            {
                flags |= 1ul << 19;
            }

            if ((modifiers & HotkeyModifiers.Control) != 0)
            {
                flags |= 1ul << 18;
            }

            if ((modifiers & HotkeyModifiers.Shift) != 0)
            {
                flags |= 1ul << 17;
            }

            if ((modifiers & HotkeyModifiers.Meta) != 0)
            {
                flags |= 1ul << 20;
            }

            return flags;
        }

        /// <summary>
        /// Extracts the supported modifier state from CGEventFlags, ignoring
        /// every bit outside <see cref="SupportedModifierMask"/> (caps lock,
        /// numeric keypad, ...). Returns <see cref="HotkeyModifiers.None"/>
        /// when no supported modifier bit is set.
        /// </summary>
        public static HotkeyModifiers ToModifiers(ulong flags)
        {
            var modifiers = HotkeyModifiers.None;
            if ((flags & (1ul << 19)) != 0)
            {
                modifiers |= HotkeyModifiers.Alt;
            }

            if ((flags & (1ul << 18)) != 0)
            {
                modifiers |= HotkeyModifiers.Control;
            }

            if ((flags & (1ul << 17)) != 0)
            {
                modifiers |= HotkeyModifiers.Shift;
            }

            if ((flags & (1ul << 20)) != 0)
            {
                modifiers |= HotkeyModifiers.Meta;
            }

            return modifiers;
        }

        private static Dictionary<ushort, HotkeyKey> BuildReverseMap(Dictionary<HotkeyKey, ushort> forward)
        {
            var reverse = new Dictionary<ushort, HotkeyKey>(forward.Count);
            foreach (var pair in forward)
            {
                reverse[pair.Value] = pair.Key;
            }

            return reverse;
        }
    }
}
