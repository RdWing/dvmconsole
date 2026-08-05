// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DvmConsole.Platform.Hotkeys
{
    /// <summary>
    /// Keyboard keys usable in global hotkey gestures.
    /// </summary>
    public enum HotkeyKey
    {
        /// <summary>No key.</summary>
        None = 0,
        A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
        D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
        F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
        F13, F14, F15, F16, F17, F18, F19, F20, F21, F22, F23, F24,
        Enter, Escape, Tab, Space, Backspace, Delete, Insert, Home, End,
        PageUp, PageDown, Left, Right, Up, Down,
    }

    /// <summary>
    /// Modifier flags for global hotkey gestures.
    /// </summary>
    [Flags]
    public enum HotkeyModifiers
    {
        /// <summary>No modifiers.</summary>
        None = 0,

        /// <summary>Alt / Option.</summary>
        Alt = 1,

        /// <summary>Control / Ctrl.</summary>
        Control = 2,

        /// <summary>Shift.</summary>
        Shift = 4,

        /// <summary>Meta / Command / Windows key.</summary>
        Meta = 8,
    }

    /// <summary>
    /// A global hotkey gesture: a key plus modifier flags. Value type.
    /// </summary>
    /// <param name="Key">The hotkey key.</param>
    /// <param name="Modifiers">Modifier flags.</param>
    public readonly record struct HotkeyGesture(HotkeyKey Key, HotkeyModifiers Modifiers);

    /// <summary>
    /// Per-gesture capability reported by the platform.
    /// </summary>
    public enum HotkeyCapability
    {
        /// <summary>The gesture is not supported on this platform.</summary>
        Unsupported,

        /// <summary>The gesture can be registered.</summary>
        Available,

        /// <summary>Registration requires user permission that is not yet granted.</summary>
        PermissionRequired,
    }

    /// <summary>
    /// Outcome of a hotkey registration attempt.
    /// </summary>
    public enum HotkeyRegistrationStatus
    {
        /// <summary>The gesture was registered successfully.</summary>
        Registered,

        /// <summary>The gesture was already registered.</summary>
        AlreadyRegistered,

        /// <summary>Registration was denied because permission is required.</summary>
        PermissionDenied,

        /// <summary>The gesture is not supported on this platform.</summary>
        Unsupported,
    }

    /// <summary>
    /// Result of a hotkey registration attempt: status plus the gesture involved.
    /// </summary>
    /// <param name="Status">Registration outcome.</param>
    /// <param name="Gesture">The gesture that was registered.</param>
    public readonly record struct HotkeyRegistrationResult(HotkeyRegistrationStatus Status, HotkeyGesture Gesture);

    /// <summary>
    /// Kind of hotkey event raised by the service.
    /// </summary>
    public enum HotkeyEventType
    {
        /// <summary>The hotkey was pressed.</summary>
        Pressed,

        /// <summary>The hotkey was released.</summary>
        Released,
    }

    /// <summary>
    /// Event arguments for a hotkey event: the gesture and the event type.
    /// </summary>
    public sealed class HotkeyEventArgs : EventArgs
    {
        /// <summary>
        /// Creates hotkey event arguments.
        /// </summary>
        /// <param name="gesture">The gesture that fired.</param>
        /// <param name="eventType">Whether it was a press or release.</param>
        public HotkeyEventArgs(HotkeyGesture gesture, HotkeyEventType eventType)
        {
            Gesture = gesture;
            EventType = eventType;
        }

        /// <summary>The gesture that fired.</summary>
        public HotkeyGesture Gesture { get; }

        /// <summary>Whether it was a press or release.</summary>
        public HotkeyEventType EventType { get; }
    }

    /// <summary>
    /// Global hotkey registration service. Queries per-gesture capability, registers
    /// and unregisters gestures, and raises <see cref="HotkeyPressed"/> when a
    /// registered gesture fires. Dispose is idempotent and detaches the event.
    /// </summary>
    public interface IGlobalHotkeyService : IDisposable
    {
        /// <summary>Raised when a registered gesture fires.</summary>
        event EventHandler<HotkeyEventArgs>? HotkeyPressed;

        /// <summary>Reports the capability of a gesture on this platform.</summary>
        HotkeyCapability GetCapability(HotkeyGesture gesture);

        /// <summary>
        /// Registers a gesture.
        /// </summary>
        /// <param name="gesture">The gesture to register.</param>
        /// <param name="cancellationToken">Cancels the registration attempt.</param>
        /// <returns>The registration outcome.</returns>
        Task<HotkeyRegistrationResult> RegisterAsync(HotkeyGesture gesture, CancellationToken cancellationToken);

        /// <summary>
        /// Unregisters a gesture. Idempotent; re-registration succeeds afterwards.
        /// </summary>
        /// <param name="gesture">The gesture to unregister.</param>
        /// <param name="cancellationToken">Cancels the unregistration attempt.</param>
        Task UnregisterAsync(HotkeyGesture gesture, CancellationToken cancellationToken);
    }
}
