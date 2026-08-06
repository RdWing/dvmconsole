// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System.ComponentModel;

namespace DvmConsole.Avalonia.ViewModels
{
    /// <summary>
    /// View-model for one operator-dashboard channel slot. The identity of
    /// a slot is immutable: number, label, and the unassigned defaults
    /// (NO TALKGROUP, IDLE) are fixed at construction. The live selection
    /// state (<see cref="IsSelected"/>, <see cref="IsPrimary"/>) and the
    /// PTT engagement state (<see cref="PttEngaged"/>) are writable and
    /// observable, driven by the dashboard's
    /// <c>SelectedChannelsManager</c> and PTT state slice.
    /// </summary>
    public sealed class ChannelSlotViewModel : INotifyPropertyChanged
    {
        private bool isSelected;
        private bool isPrimary;
        private bool pttEngaged;

        /// <summary>
        /// Creates a channel slot with the given 1-based number, display
        /// label and optional assigned codeplug channel name, in the
        /// unassigned default state (unselected, non-primary). The
        /// channel-name parameter is trailing and optional so existing
        /// two-argument constructions stay source-compatible.
        /// </summary>
        public ChannelSlotViewModel(int number, string label, string? channelName = null)
        {
            Number = number;
            Label = label;
            ChannelName = channelName;
        }

        /// <summary>The 1-based slot number (1..4 on the dashboard).</summary>
        public int Number { get; }

        /// <summary>The display label, e.g. <c>CHANNEL 01</c>.</summary>
        public string Label { get; }

        /// <summary>
        /// The codeplug channel name assigned to this slot, or null when
        /// no channel is assigned. Immutable at construction like
        /// <see cref="Number"/>, <see cref="Label"/>, <see cref="Talkgroup"/>
        /// and <see cref="Status"/>.
        /// </summary>
        public string? ChannelName { get; }

        /// <summary>
        /// The talkgroup assigned to this slot; <c>NO TALKGROUP</c> until
        /// configuration assigns one.
        /// </summary>
        public string Talkgroup { get; } = "NO TALKGROUP";

        /// <summary>
        /// The operational status of this slot; <c>IDLE</c> until assigned.
        /// </summary>
        public string Status { get; } = "IDLE";

        /// <summary>
        /// True when this slot is currently selected on the dashboard.
        /// Raises <see cref="PropertyChanged"/> only when the value
        /// changes.
        /// </summary>
        public bool IsSelected
        {
            get => isSelected;
            set
            {
                if (isSelected == value)
                {
                    return;
                }

                isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        /// <summary>
        /// True when this slot is the primary channel. Raises
        /// <see cref="PropertyChanged"/> only when the value changes.
        /// </summary>
        public bool IsPrimary
        {
            get => isPrimary;
            set
            {
                if (isPrimary == value)
                {
                    return;
                }

                isPrimary = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPrimary)));
            }
        }

        /// <summary>
        /// True when this slot is engaged for push-to-talk. Raises
        /// <see cref="PropertyChanged"/> only when the value changes.
        /// </summary>
        public bool PttEngaged
        {
            get => pttEngaged;
            set
            {
                if (pttEngaged == value)
                {
                    return;
                }

                pttEngaged = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PttEngaged)));
            }
        }

        /// <summary>
        /// Raised when <see cref="IsSelected"/>, <see cref="IsPrimary"/>
        /// or <see cref="PttEngaged"/> changes.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
