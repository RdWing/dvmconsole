// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using Avalonia.Input;

namespace DvmConsole.Avalonia.Input
{
    /// <summary>
    /// Pure managed translation layer that maps an Avalonia pointer update
    /// onto the PTT button down/up contract. Given the pointer update kind,
    /// it reports whether the update is a PTT button action and, when it is,
    /// whether the action is a press (isDown true) or a release (isDown
    /// false). Only the left button participates: LeftButtonPressed accepts
    /// with isDown true, LeftButtonReleased accepts with isDown false, and
    /// every other kind is rejected with isDown zeroed. Modifiers are not an
    /// input and this type is deliberately free of UI, window, pointer-event,
    /// audio, network, and persistence behavior so it can be driven
    /// headlessly.
    /// </summary>
    public static class PttButtonPointerInterpreter
    {
        /// <summary>
        /// Tries to translate a pointer update kind into a PTT button
        /// action. <paramref name="isDown"/> is initialized to false on
        /// every call. Returns true with isDown true only for
        /// <see cref="PointerUpdateKind.LeftButtonPressed"/>, true with
        /// isDown false only for
        /// <see cref="PointerUpdateKind.LeftButtonReleased"/>, and false
        /// with isDown false for every other kind.
        /// </summary>
        /// <param name="kind">The pointer update kind to translate.</param>
        /// <param name="isDown">True when the action is a PTT press, false for a release or rejection.</param>
        /// <returns>True when the update is a PTT button action.</returns>
        public static bool TryGetPttPointerAction(PointerUpdateKind kind, out bool isDown)
        {
            isDown = false;

            switch (kind)
            {
                case PointerUpdateKind.LeftButtonPressed:
                    isDown = true;
                    return true;
                case PointerUpdateKind.LeftButtonReleased:
                    return true;
                default:
                    return false;
            }
        }
    }
}
