// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;

namespace DvmConsole.Platform.Hotkeys.Mac
{
    /// <summary>
    /// macOS <see cref="IKeyboardKeyStateReader"/> backed by
    /// CGEventSourceKeyState: the GetAsyncKeyState-contract replacement for
    /// the PTT hotkey key-up watchdog. The parameterless constructor probes
    /// the combined-session key state via P/Invoke and answers false off
    /// macOS without ever throwing; the delegate constructor forwards to an
    /// injected keycode probe for tests and alternate backends.
    /// </summary>
    public sealed class MacKeyStateReader : IKeyboardKeyStateReader
    {
        private readonly Func<ushort, bool> _keyStateProbe;

        /// <summary>
        /// Uses CGEventSourceKeyState(kCGEventSourceStateCombinedSessionState)
        /// as the key-state probe. Safe to construct and query on any host;
        /// off macOS every query answers false.
        /// </summary>
        public MacKeyStateReader()
            : this(CoreGraphicsNative.KeyStateIsDown)
        {
        }

        /// <summary>
        /// Uses the supplied keycode probe, so the physical key-state source
        /// can be controlled (e.g. in tests).
        /// </summary>
        /// <param name="keyStateProbe">Probe answering whether an ANSI kVK
        /// keycode is currently physically down.</param>
        /// <exception cref="ArgumentNullException">When
        /// <paramref name="keyStateProbe"/> is null.</exception>
        public MacKeyStateReader(Func<ushort, bool> keyStateProbe)
        {
            _keyStateProbe = keyStateProbe ?? throw new ArgumentNullException(nameof(keyStateProbe));
        }

        /// <summary>
        /// Reports whether the gesture's key is currently physically down.
        /// Returns false for <see cref="HotkeyKey.None"/> and any unmappable
        /// key without consulting the probe.
        /// </summary>
        public bool IsKeyDown(HotkeyGesture gesture)
        {
            if (!MacHotkeyKeyCodes.TryGetKeyCode(gesture.Key, out var keyCode))
            {
                return false;
            }

            return _keyStateProbe(keyCode);
        }
    }
}
