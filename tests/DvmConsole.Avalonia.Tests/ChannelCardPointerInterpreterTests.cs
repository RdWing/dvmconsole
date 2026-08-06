// SPDX-License-Identifier: AGPL-3.0-only
/**
* Dedicated contract gate for the pure managed pointer interpreter that maps
* Avalonia channel-card pointer events onto the dashboard selection contract:
* DvmConsole.Avalonia.Input.ChannelCardPointerInterpreter. These facts are
* written entirely against the agreed contract:
* TryGetChannelClick(object?, Avalonia.Input.KeyModifiers, out int, out bool)
* initializes both outputs to 0 / false, returns false for a null or
* non-ChannelSlotViewModel data context, returns true for a
* ChannelSlotViewModel passing its Number through unchanged, and reports
* setPrimary=true when the modifiers contain Control or Meta (with Shift
* still true) and false for None / Shift / Alt.
*
* The interpreter is a pure translation layer: no UI, window, display,
* pointer event, click count, e.Handled, FNE/audio/network, persistence, or
* secret is involved. The tests are fully headless and use only the real
* Avalonia 11.3.18 KeyModifiers enum.
*
* RED contract gate: the production interpreter does not exist yet, so this
* project fails to compile until the next slice implements it.
*/
#nullable enable
using Avalonia.Input;
using DvmConsole.Avalonia.Input;
using DvmConsole.Avalonia.ViewModels;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// Contract gate for <c>ChannelCardPointerInterpreter</c> against the
    /// pointer-to-selection translation contract.
    /// </summary>
    public sealed class ChannelCardPointerInterpreterTests
    {
        // ---- Data context rejection --------------------------------------------

        /// <summary>
        /// A null data context is rejected: the call returns false and both
        /// outputs are initialized to their zero values (slot number 0,
        /// setPrimary false) even when the caller pre-seeded them.
        /// </summary>
        [Fact]
        public void NullDataContext_ReturnsFalse_OutputsZeroed()
        {
            int slotNumber = 42;
            bool setPrimary = true;

            var accepted = ChannelCardPointerInterpreter.TryGetChannelClick(
                null,
                KeyModifiers.None,
                out slotNumber,
                out setPrimary);

            Assert.False(accepted);
            Assert.Equal(0, slotNumber);
            Assert.False(setPrimary);
        }

        /// <summary>
        /// A non-slot data context (a string) is rejected: the call returns
        /// false and both outputs stay at their zero values.
        /// </summary>
        [Theory]
        [InlineData("not a channel slot")]
        [InlineData("")]
        public void NonSlotDataContext_String_ReturnsFalse_OutputsZeroed(string dataContext)
        {
            int slotNumber = 42;
            bool setPrimary = true;

            var accepted = ChannelCardPointerInterpreter.TryGetChannelClick(
                dataContext,
                KeyModifiers.None,
                out slotNumber,
                out setPrimary);

            Assert.False(accepted);
            Assert.Equal(0, slotNumber);
            Assert.False(setPrimary);
        }

        /// <summary>
        /// A non-slot data context (a plain object) is rejected: the call
        /// returns false and both outputs stay at their zero values.
        /// </summary>
        [Fact]
        public void NonSlotDataContext_PlainObject_ReturnsFalse_OutputsZeroed()
        {
            int slotNumber = 42;
            bool setPrimary = true;

            var accepted = ChannelCardPointerInterpreter.TryGetChannelClick(
                new object(),
                KeyModifiers.None,
                out slotNumber,
                out setPrimary);

            Assert.False(accepted);
            Assert.Equal(0, slotNumber);
            Assert.False(setPrimary);
        }

        // ---- Slot number passthrough ---------------------------------------------

        /// <summary>
        /// A ChannelSlotViewModel data context is accepted and its Number is
        /// passed through unchanged, for every dashboard slot.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void SlotDataContext_NumberPassedThroughUnchanged(int number)
        {
            var slot = new ChannelSlotViewModel(number, $"CHANNEL {number:00}");

            var accepted = ChannelCardPointerInterpreter.TryGetChannelClick(
                slot,
                KeyModifiers.None,
                out var slotNumber,
                out var setPrimary);

            Assert.True(accepted);
            Assert.Equal(number, slotNumber);
        }

        // ---- Modifier mapping -------------------------------------------------------

        /// <summary>
        /// A plain click - no modifiers, or Shift / Alt alone - never sets
        /// the primary: setPrimary comes back false while the slot number
        /// still passes through.
        /// </summary>
        [Theory]
        [InlineData(KeyModifiers.None)]
        [InlineData(KeyModifiers.Shift)]
        [InlineData(KeyModifiers.Alt)]
        public void PlainModifiers_NotPrimary(KeyModifiers modifiers)
        {
            var slot = new ChannelSlotViewModel(2, "CHANNEL 02");

            var accepted = ChannelCardPointerInterpreter.TryGetChannelClick(
                slot,
                modifiers,
                out var slotNumber,
                out var setPrimary);

            Assert.True(accepted);
            Assert.Equal(slot.Number, slotNumber);
            Assert.False(setPrimary);
        }

        /// <summary>
        /// Control or Meta alone marks the click as a primary click:
        /// setPrimary comes back true with the slot number passed through.
        /// </summary>
        [Theory]
        [InlineData(KeyModifiers.Control)]
        [InlineData(KeyModifiers.Meta)]
        public void PrimaryModifiers_SetPrimary(KeyModifiers modifiers)
        {
            var slot = new ChannelSlotViewModel(3, "CHANNEL 03");

            var accepted = ChannelCardPointerInterpreter.TryGetChannelClick(
                slot,
                modifiers,
                out var slotNumber,
                out var setPrimary);

            Assert.True(accepted);
            Assert.Equal(slot.Number, slotNumber);
            Assert.True(setPrimary);
        }

        /// <summary>
        /// Control and Meta combined with Shift is still a primary click:
        /// Shift never suppresses the Control/Meta primary semantics.
        /// </summary>
        [Fact]
        public void ControlMetaShift_StillSetsPrimary()
        {
            var slot = new ChannelSlotViewModel(1, "CHANNEL 01");

            var accepted = ChannelCardPointerInterpreter.TryGetChannelClick(
                slot,
                KeyModifiers.Control | KeyModifiers.Meta | KeyModifiers.Shift,
                out var slotNumber,
                out var setPrimary);

            Assert.True(accepted);
            Assert.Equal(slot.Number, slotNumber);
            Assert.True(setPrimary);
        }
    }
}
