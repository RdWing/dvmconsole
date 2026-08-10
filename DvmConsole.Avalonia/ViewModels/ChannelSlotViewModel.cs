// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.ComponentModel;

namespace DvmConsole.Avalonia.ViewModels
{
    /// <summary>
    /// View-model for one operator-dashboard channel slot. The identity
    /// of a slot is immutable: number and label are fixed at
    /// construction, as is the unassigned default (IDLE status). The
    /// channel assignment is mutable through the internal
    /// <see cref="Reassign"/> entry point, which the dashboard's
    /// zone-selection slice uses to re-point the four fixed slots at a
    /// zone's channels. The live selection state
    /// (<see cref="IsSelected"/>, <see cref="IsPrimary"/>), the PTT
    /// engagement state (<see cref="PttEngaged"/>) and the TAR
    /// recording indicator state (<see cref="TarRecordingEnabled"/>)
    /// are writable and observable, driven by the dashboard's
    /// <c>SelectedChannelsManager</c>, PTT state slice and TAR
    /// indicator wiring.
    /// </summary>
    public sealed class ChannelSlotViewModel : INotifyPropertyChanged
    {
        private bool isSelected;
        private bool isPrimary;
        private bool pttEngaged;
        private bool tarRecordingEnabled;
        private string? channelName;
        private string talkgroup = "NO TALKGROUP";

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
            this.channelName = channelName;
        }

        /// <summary>The 1-based slot number (1..4 on the dashboard).</summary>
        public int Number { get; }

        /// <summary>The display label, e.g. <c>CHANNEL 01</c>.</summary>
        public string Label { get; }

        /// <summary>
        /// The codeplug channel name assigned to this slot, or null when
        /// no channel is assigned. Get-only: the assignment is replaced
        /// wholesale through the internal <see cref="Reassign"/> entry
        /// point, never mutated in place. The identity members
        /// (<see cref="Number"/>, <see cref="Label"/>) are immutable.
        /// </summary>
        public string? ChannelName => channelName;

        /// <summary>
        /// The talkgroup assigned to this slot; <c>NO TALKGROUP</c> until
        /// configuration assigns one. Get-only: replaced wholesale
        /// through the internal <see cref="Reassign"/> entry point.
        /// </summary>
        public string Talkgroup => talkgroup;

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
        /// True when TAR recording is enabled for this channel slot,
        /// defaulting to false. Headless indicator state only: shell
        /// wiring to the recorder lifecycle is a later slice. Raises
        /// <see cref="PropertyChanged"/> only when the value changes.
        /// Tooltip text is WPF-parity with the original
        /// <c>ChannelBox.SetTarRecordingIndicator</c>
        /// (dvmconsole/Controls/ChannelBox.xaml.cs:1510-1516).
        /// </summary>
        public bool TarRecordingEnabled
        {
            get => tarRecordingEnabled;
            set
            {
                if (tarRecordingEnabled == value)
                {
                    return;
                }

                tarRecordingEnabled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TarRecordingEnabled)));
            }
        }

        /// <summary>
        /// The tooltip text for this slot's TAR recording indicator,
        /// derived from <see cref="TarRecordingEnabled"/> with the
        /// WPF-original wording
        /// (dvmconsole/Controls/ChannelBox.xaml.cs:1510-1516).
        /// </summary>
        public string TarRecordingIndicatorToolTip =>
            tarRecordingEnabled
                ? "TAR recording enabled for this channel"
                : "TAR recording disabled for this channel";

        /// <summary>
        /// Replaces the channel assignment of this slot with the given
        /// channel name and talkgroup, raising change-only
        /// <see cref="PropertyChanged"/> notifications: a notification
        /// is raised for a member only when its value actually changes,
        /// and a call that changes nothing raises nothing at all. A null
        /// or whitespace-only talkgroup normalizes to the stable
        /// <c>NO TALKGROUP</c> text. The assignment is replaced
        /// wholesale; identity members (<see cref="Number"/>,
        /// <see cref="Label"/>) and <see cref="Status"/> are untouched.
        /// </summary>
        /// <param name="channelName">The new channel name, or null for unassigned.</param>
        /// <param name="talkgroup">The new talkgroup, or null for none.</param>
        internal void Reassign(string? channelName, string? talkgroup)
        {
            var normalizedTalkgroup = string.IsNullOrWhiteSpace(talkgroup)
                ? "NO TALKGROUP"
                : talkgroup;

            var channelNameChanged = !string.Equals(
                this.channelName, channelName, StringComparison.Ordinal);
            var talkgroupChanged = !string.Equals(
                this.talkgroup, normalizedTalkgroup, StringComparison.Ordinal);

            if (!channelNameChanged && !talkgroupChanged)
            {
                return;
            }

            this.channelName = channelName;
            this.talkgroup = normalizedTalkgroup;

            if (channelNameChanged)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChannelName)));
            }

            if (talkgroupChanged)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Talkgroup)));
            }
        }

        /// <summary>
        /// Raised when <see cref="ChannelName"/>, <see cref="Talkgroup"/>,
        /// <see cref="IsSelected"/>, <see cref="IsPrimary"/>,
        /// <see cref="PttEngaged"/> or <see cref="TarRecordingEnabled"/>
        /// changes.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
