// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable

namespace DvmConsole.Platform.Hotkeys
{
    /// <summary>
    /// Dependency-free physical key-state probe used by the PTT hotkey
    /// key-up watchdog. Implementations report whether a hotkey
    /// gesture's key is currently physically down; the interface
    /// carries no platform implementation or lifetime surface.
    /// </summary>
    public interface IKeyboardKeyStateReader
    {
        /// <summary>
        /// Reports whether the gesture's key is currently physically
        /// down.
        /// </summary>
        /// <param name="gesture">The gesture whose key to probe.</param>
        /// <returns>True when the gesture's key is physically down.</returns>
        bool IsKeyDown(HotkeyGesture gesture);
    }
}
