// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;

namespace DvmConsole.Platform.Hotkeys.Mac
{
    /// <summary>
    /// A raw macOS keyboard event observed by the event tap.
    /// </summary>
    /// <param name="KeyCode">The ANSI virtual key code (kVK) of the key.</param>
    /// <param name="Flags">The CGEventFlags of the event; modifier state lives
    /// in the supported-modifier bits of <see cref="MacHotkeyKeyCodes.SupportedModifierMask"/>.</param>
    /// <param name="IsAutorepeat">True for key-repeat events.</param>
    public readonly record struct MacKeyEventData(ushort KeyCode, ulong Flags, bool IsAutorepeat);

    /// <summary>
    /// Seam over a macOS CGEventTap-based keyboard event source. The
    /// lifecycle is create, enable, attach; the service owns the ordering.
    /// Implementations must be safe to construct on any host and never
    /// invoke native code off macOS.
    /// </summary>
    public interface IMacEventTap : IDisposable
    {
        /// <summary>Raised for every raw keyboard event the tap observes.</summary>
        event Action<MacKeyEventData>? KeyEvent;

        /// <summary>
        /// Creates the underlying tap. Returns false when the tap cannot be
        /// created (e.g. off macOS or without permission); the tap is then
        /// inert until a later Create attempt.
        /// </summary>
        bool Create();

        /// <summary>Starts delivery of events from the created tap.</summary>
        void Enable();

        /// <summary>Stops delivery of events from the created tap.</summary>
        void Disable();

        /// <summary>Attaches the tap source to the caller's run loop.</summary>
        void AttachRunLoop();

        /// <summary>Detaches the tap source from the caller's run loop.</summary>
        void DetachRunLoop();

        /// <summary>
        /// Raises <see cref="KeyEvent"/> directly with the supplied data,
        /// bypassing the OS event stream. Test seam and event-injection hook.
        /// </summary>
        void SimulateKeyEvent(MacKeyEventData data);
    }
}
