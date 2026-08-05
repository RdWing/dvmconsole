// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;

namespace DvmConsole.Avalonia.ViewModels
{
    /// <summary>
    /// Immutable view-model for one operator-dashboard channel slot. Slots
    /// are created fixed at construction: number, label, and the
    /// unassigned defaults (NO TALKGROUP, IDLE, non-primary) until a
    /// future feature assigns talkgroups or selection.
    /// </summary>
    public sealed class ChannelSlotViewModel
    {
        /// <summary>
        /// Creates a channel slot with the given 1-based number and display
        /// label, in the unassigned default state.
        /// </summary>
        public ChannelSlotViewModel(int number, string label)
        {
            Number = number;
            Label = label;
        }

        /// <summary>The 1-based slot number (1..4 on the dashboard).</summary>
        public int Number { get; }

        /// <summary>The display label, e.g. <c>CHANNEL 01</c>.</summary>
        public string Label { get; }

        /// <summary>
        /// The talkgroup assigned to this slot; <c>NO TALKGROUP</c> until
        /// configuration assigns one.
        /// </summary>
        public string Talkgroup { get; } = "NO TALKGROUP";

        /// <summary>
        /// The operational status of this slot; <c>IDLE</c> until assigned.
        /// </summary>
        public string Status { get; } = "IDLE";

        /// <summary>True when this slot is the primary (selected) channel.</summary>
        public bool IsPrimary { get; }
    }
}
