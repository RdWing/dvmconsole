// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System.ComponentModel;

namespace DvmConsole.Avalonia.ViewModels
{
    /// <summary>
    /// View-model for one operator-dashboard channel slot. The identity of
    /// a slot is immutable: number, label, and the unassigned defaults
    /// (NO TALKGROUP, IDLE) are fixed at construction. The live selection
    /// state (<see cref="IsSelected"/>, <see cref="IsPrimary"/>) is
    /// writable and observable, driven by the dashboard's
    /// <c>SelectedChannelsManager</c>.
    /// </summary>
    public sealed class ChannelSlotViewModel : INotifyPropertyChanged
    {
        private bool isSelected;
        private bool isPrimary;

        /// <summary>
        /// Creates a channel slot with the given 1-based number and display
        /// label, in the unassigned default state (unselected, non-primary).
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
        /// Raised when <see cref="IsSelected"/> or <see cref="IsPrimary"/>
        /// changes.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
