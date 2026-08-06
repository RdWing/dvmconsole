// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using Avalonia.Input;
using DvmConsole.Avalonia.ViewModels;

namespace DvmConsole.Avalonia.Input
{
    /// <summary>
    /// Pure managed translation layer that maps an Avalonia channel-card
    /// pointer press onto the dashboard selection contract. Given the card's
    /// data context and the pressed modifier keys, it reports whether the
    /// press is a channel-slot click and, when it is, which slot was clicked
    /// and whether the click is the primary-toggle (Ctrl/Meta) variant.
    /// This type is deliberately free of UI, window, pointer-event, audio,
    /// network, and persistence behavior so it can be driven headlessly.
    /// </summary>
    public static class ChannelCardPointerInterpreter
    {
        /// <summary>
        /// Tries to translate a channel-card pointer press into a dashboard
        /// selection click. Both outputs are initialized to their zero values
        /// (slot number 0, setPrimary false) on every call. Returns false
        /// unless <paramref name="dataContext"/> is a
        /// <see cref="ChannelSlotViewModel"/>, in which case the slot's
        /// <see cref="ChannelSlotViewModel.Number"/> is passed through
        /// unchanged and setPrimary reports whether the modifiers contain
        /// Control or Meta (Shift and Alt never suppress the primary
        /// semantics).
        /// </summary>
        /// <param name="dataContext">The clicked card's data context.</param>
        /// <param name="modifiers">The modifier keys held during the press.</param>
        /// <param name="slotNumber">The 1-based slot number, or 0 when rejected.</param>
        /// <param name="setPrimary">True for the primary-toggle (Ctrl/Meta) variant, or false when rejected.</param>
        /// <returns>True when the press is a channel-slot click.</returns>
        public static bool TryGetChannelClick(
            object? dataContext,
            KeyModifiers modifiers,
            out int slotNumber,
            out bool setPrimary)
        {
            slotNumber = 0;
            setPrimary = false;

            if (dataContext is not ChannelSlotViewModel slot)
            {
                return false;
            }

            slotNumber = slot.Number;
            setPrimary = (modifiers & KeyModifiers.Control) != 0
                || (modifiers & KeyModifiers.Meta) != 0;

            return true;
        }
    }
}
