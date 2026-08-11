// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Media;
using DvmConsole.Platform.Audio;

namespace DvmConsole.Avalonia.ViewModels
{
    /// <summary>
    /// Fixed presentation sizes supported by the WPF resource card.
    /// </summary>
    public enum ChannelCardSize
    {
        Small,
        Normal,
        Large,
    }

    /// <summary>
    /// View-model for one operator-dashboard channel resource. The identity
    /// of a slot is immutable: number and label are fixed at
    /// construction, as is the unassigned default (IDLE status). The
    /// channel assignment is mutable through the internal
    /// <see cref="Reassign"/> entry point, which the dashboard's
    /// zone-selection slice uses to point channel resources at a zone's
    /// channels. The live selection state
    /// (<see cref="IsSelected"/>, <see cref="IsPrimary"/>), the PTT
    /// engagement state (<see cref="PttEngaged"/>) and the TAR
    /// recording indicator state (<see cref="TarRecordingEnabled"/>)
    /// are writable and observable, driven by the dashboard's
    /// <c>SelectedChannelsManager</c>, PTT state slice and TAR
    /// indicator wiring.
    /// </summary>
    public sealed class ChannelSlotViewModel : INotifyPropertyChanged
    {
        public const string DefaultIdleColor = "#142126";
        public const double SmallCardWidth = 154d;
        public const double SmallCardHeight = 68d;
        public const double NormalCardWidth = 264d;
        public const double NormalCardHeight = 110d;
        public const double LargeCardWidth = 380d;
        public const double LargeCardHeight = 158d;

        private bool isSelected;
        private bool isPrimary;
        private double volume = 1.0;
        private IReadOnlyList<AudioDeviceOptionViewModel> monitorOutputDevices =
            Array.Empty<AudioDeviceOptionViewModel>();
        private AudioDeviceOptionViewModel? monitorOutputDevice;
        private bool pttEngaged;
        private bool tarRecordingEnabled;
        private string? channelName;
        private string talkgroup = "NO TALKGROUP";
        private string? resourceKey;
        private string channelMode = string.Empty;
        private string systemName = string.Empty;
        private bool isRxOnly;
        private ChannelCardSize cardSize = ChannelCardSize.Normal;
        private string idleColor = DefaultIdleColor;
        private IBrush idleBrush = new SolidColorBrush(Color.Parse(DefaultIdleColor));
        private bool isReceiving;
        private bool isReceivingEncrypted;
        private string lastSrcId = "Last ID: 0";
        private bool isTxEncrypted;
        private bool isEncryptionSelectable;
        private bool isPatchGroupMember;
        private bool isPatchGroupActive;
        private bool isMultiSelectMember;
        private bool pageState;
        private bool holdState;
        private bool fneConnectionWarningVisible;
        private string fneConnectionWarningToolTip = string.Empty;

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

        /// <summary>The 1-based resource number within the active zone.</summary>
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

        /// <summary>The normalized channel mode, or empty when unassigned.</summary>
        public string ChannelMode => channelMode;

        /// <summary>The codeplug system name, or empty when unassigned.</summary>
        public string SystemName => systemName;

        /// <summary>The normalized talkgroup ID, or <c>NO TALKGROUP</c> when blank.</summary>
        public string TalkgroupId => talkgroup;

        /// <summary>True when the resource is receive-only.</summary>
        public bool IsRxOnly => isRxOnly;

        /// <summary>True when this resource is eligible for PTT.</summary>
        public bool IsPttEnabled => !isRxOnly;

        /// <summary>The configured resource-card size.</summary>
        public ChannelCardSize CardSize => cardSize;

        /// <summary>The rendered width for <see cref="CardSize"/>.</summary>
        public double CardWidth => cardSize switch
        {
            ChannelCardSize.Small => SmallCardWidth,
            ChannelCardSize.Large => LargeCardWidth,
            _ => NormalCardWidth,
        };

        /// <summary>The rendered height for <see cref="CardSize"/>.</summary>
        public double CardHeight => cardSize switch
        {
            ChannelCardSize.Small => SmallCardHeight,
            ChannelCardSize.Large => LargeCardHeight,
            _ => NormalCardHeight,
        };

        /// <summary>The validated idle color used by the card background.</summary>
        public string IdleColor => idleColor;

        /// <summary>The validated idle brush used by the card background.</summary>
        public IBrush IdleBrush => idleBrush;

        /// <summary>True while a matching receive stream is active.</summary>
        public bool IsReceiving
        {
            get => isReceiving;
            set
            {
                if (isReceiving == value)
                {
                    return;
                }

                isReceiving = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReceiving)));
            }
        }

        /// <summary>True while the active receive stream is encrypted.</summary>
        public bool IsReceivingEncrypted
        {
            get => isReceivingEncrypted;
            set
            {
                if (isReceivingEncrypted == value)
                {
                    return;
                }

                isReceivingEncrypted = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReceivingEncrypted)));
            }
        }

        /// <summary>The WPF-parity last-source display, retained through idle.</summary>
        public string LastSrcId
        {
            get => lastSrcId;
            set
            {
                var normalized = value ?? string.Empty;
                if (string.Equals(lastSrcId, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                lastSrcId = normalized;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastSrcId)));
            }
        }

        /// <summary>True when the current transmit state is encrypted.</summary>
        public bool IsTxEncrypted
        {
            get => isTxEncrypted;
            set
            {
                if (isTxEncrypted == value)
                {
                    return;
                }

                isTxEncrypted = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTxEncrypted)));
            }
        }

        /// <summary>True when selectable encryption can be shown for this slot.</summary>
        public bool IsEncryptionSelectable
        {
            get => isEncryptionSelectable;
            set
            {
                if (isEncryptionSelectable == value)
                {
                    return;
                }

                isEncryptionSelectable = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEncryptionSelectable)));
            }
        }

        /// <summary>True when the resource belongs to one or more patch groups.</summary>
        public bool IsPatchGroupMember
        {
            get => isPatchGroupMember;
            set
            {
                if (isPatchGroupMember == value)
                {
                    return;
                }

                isPatchGroupMember = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPatchGroupMember)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GroupIndicatorToolTip)));
            }
        }

        /// <summary>True when one or more patch groups are currently enabled.</summary>
        public bool IsPatchGroupActive
        {
            get => isPatchGroupActive;
            set
            {
                if (isPatchGroupActive == value)
                {
                    return;
                }

                isPatchGroupActive = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPatchGroupActive)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GroupIndicatorToolTip)));
            }
        }

        /// <summary>True when the resource belongs to the current multi-select group.</summary>
        public bool IsMultiSelectMember
        {
            get => isMultiSelectMember;
            set
            {
                if (isMultiSelectMember == value)
                {
                    return;
                }

                isMultiSelectMember = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMultiSelectMember)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GroupIndicatorToolTip)));
            }
        }

        /// <summary>True when page-select is active for this resource.</summary>
        public bool PageState
        {
            get => pageState;
            set
            {
                if (pageState == value)
                {
                    return;
                }

                pageState = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PageState)));
            }
        }

        /// <summary>True when the resource is held for marker/hold behavior.</summary>
        public bool HoldState
        {
            get => holdState;
            set
            {
                if (holdState == value)
                {
                    return;
                }

                holdState = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HoldState)));
            }
        }

        /// <summary>
        /// WPF-priority tooltip for the resource group indicator: current
        /// multi-select membership wins over enabled and ordinary patch
        /// membership.
        /// </summary>
        public string GroupIndicatorToolTip =>
            isMultiSelectMember
                ? "Member of the current multi-select group"
                : isPatchGroupMember
                    ? isPatchGroupActive
                        ? "Member of one or more enabled patch groups"
                        : "Member of one or more patch groups"
                    : string.Empty;

        /// <summary>WPF-parity tooltip for the selectable encryption state.</summary>
        public string SelectableEncryptionToolTip =>
            isTxEncrypted
                ? "Selectable encryption: encrypted TX. Click to transmit clear."
                : "Selectable encryption: clear TX. Click to transmit encrypted.";

        /// <summary>True when the slot's FNE system is unavailable.</summary>
        public bool FneConnectionWarningVisible
        {
            get => fneConnectionWarningVisible;
            set
            {
                if (fneConnectionWarningVisible == value)
                {
                    return;
                }

                fneConnectionWarningVisible = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FneConnectionWarningVisible)));
            }
        }

        /// <summary>The FNE warning text shown for the slot.</summary>
        public string FneConnectionWarningToolTip
        {
            get => fneConnectionWarningToolTip;
            set
            {
                var normalized = value ?? string.Empty;
                if (string.Equals(fneConnectionWarningToolTip, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                fneConnectionWarningToolTip = normalized;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FneConnectionWarningToolTip)));
            }
        }

        /// <summary>
        /// The talkgroup assigned to this slot; <c>NO TALKGROUP</c> until
        /// configuration assigns one. Get-only: replaced wholesale
        /// through the internal <see cref="Reassign"/> entry point.
        /// </summary>
        public string Talkgroup => talkgroup;

        /// <summary>
        /// The normalized resource key (<see cref="dvmconsole.ResourceIdentity.Build"/>)
        /// of the codeplug channel assigned to this slot, or null when no
        /// channel is assigned. Get-only: the key travels with the
        /// assignment through the internal <see cref="Reassign"/> entry
        /// point and is never mutated in place.
        /// </summary>
        public string? ResourceKey => resourceKey;

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
        /// Local monitor volume for this resource in the WPF-compatible
        /// 0..4 range. Values are clamped and notify only when the effective
        /// value changes.
        /// </summary>
        public double Volume
        {
            get => volume;
            set
            {
                var clamped = double.IsNaN(value) || double.IsInfinity(value)
                    ? 1.0
                    : Math.Clamp(value, 0.0, 4.0);
                if (Math.Abs(volume - clamped) < double.Epsilon)
                {
                    return;
                }

                volume = clamped;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Volume)));
            }
        }

        /// <summary>
        /// Output devices offered for this resource's local monitor. The
        /// first row is the WPF-compatible inherit-master choice.
        /// </summary>
        public IReadOnlyList<AudioDeviceOptionViewModel> MonitorOutputDevices =>
            monitorOutputDevices;

        /// <summary>
        /// The selected per-resource monitor output, or the inherit-master
        /// row when no stable per-resource override is active.
        /// </summary>
        public AudioDeviceOptionViewModel? MonitorOutputDevice
        {
            get => monitorOutputDevice;
            set
            {
                if (ReferenceEquals(monitorOutputDevice, value))
                {
                    return;
                }

                monitorOutputDevice = value;
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(MonitorOutputDevice)));
            }
        }

        /// <summary>
        /// Replaces the output options and selects the requested stable id.
        /// This is internal projection state; user edits flow through the
        /// public <see cref="MonitorOutputDevice"/> setter.
        /// </summary>
        internal void SetMonitorOutputDevices(
            IReadOnlyList<AudioDeviceOptionViewModel> devices,
            AudioDeviceOptionViewModel? selected)
        {
            monitorOutputDevices = devices ?? Array.Empty<AudioDeviceOptionViewModel>();
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(MonitorOutputDevices)));

            MonitorOutputDevice = selected is not null
                && monitorOutputDevices.Contains(selected)
                ? selected
                : monitorOutputDevices.Count > 0
                    ? monitorOutputDevices[0]
                    : null;
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
        /// channel name, talkgroup and normalized resource key, raising
        /// change-only <see cref="PropertyChanged"/> notifications: a
        /// notification is raised for a member only when its value
        /// actually changes, and a call that changes nothing raises
        /// nothing at all. A null or whitespace-only talkgroup normalizes
        /// to the stable <c>NO TALKGROUP</c> text. The assignment is
        /// replaced wholesale; identity members (<see cref="Number"/>,
        /// <see cref="Label"/>) and <see cref="Status"/> are untouched.
        /// <see cref="ResourceKey"/> is get-only and non-notifying; it is
        /// set here alongside the assignment (null for unassigned).
        /// </summary>
        /// <param name="channelName">The new channel name, or null for unassigned.</param>
        /// <param name="talkgroup">The new talkgroup, or null for none.</param>
        /// <param name="resourceKey">
        /// The normalized resource key of the assigned channel
        /// (<see cref="dvmconsole.ResourceIdentity.Build"/>), or null for
        /// unassigned. Trailing and optional so existing two-argument
        /// calls stay source-compatible.
        /// </param>
        /// <param name="channelMode">
        /// The channel mode; recognized values are normalized to uppercase,
        /// unknown non-empty values fall back to P25, and null/blank means
        /// unassigned.
        /// </param>
        /// <param name="systemName">The codeplug system name, or null for unassigned.</param>
        /// <param name="isRxOnly">True when the resource must not be used for PTT.</param>
        /// <param name="cardSize">The codeplug card size; malformed values use Normal.</param>
        /// <param name="idleColor">
        /// The codeplug idle color; malformed or blank values use
        /// <see cref="DefaultIdleColor"/>.
        /// </param>
        internal void Reassign(
            string? channelName,
            string? talkgroup,
            string? resourceKey = null,
            string? channelMode = null,
            string? systemName = null,
            bool isRxOnly = false,
            string? cardSize = null,
            string? idleColor = null)
        {
            var normalizedTalkgroup = string.IsNullOrWhiteSpace(talkgroup)
                ? "NO TALKGROUP"
                : talkgroup;
            var normalizedMode = NormalizeChannelMode(channelMode);
            var normalizedSystemName = systemName?.Trim() ?? string.Empty;
            var normalizedCardSize = ParseCardSize(cardSize);
            var normalizedIdle = NormalizeIdleColor(idleColor);

            var channelNameChanged = !string.Equals(
                this.channelName, channelName, StringComparison.Ordinal);
            var talkgroupChanged = !string.Equals(
                this.talkgroup, normalizedTalkgroup, StringComparison.Ordinal);
            var resourceKeyChanged = !string.Equals(
                this.resourceKey, resourceKey, StringComparison.Ordinal);
            var channelModeChanged = !string.Equals(
                this.channelMode, normalizedMode, StringComparison.Ordinal);
            var systemNameChanged = !string.Equals(
                this.systemName, normalizedSystemName, StringComparison.Ordinal);
            var rxOnlyChanged = this.isRxOnly != isRxOnly;
            var cardSizeChanged = this.cardSize != normalizedCardSize;
            var idleColorChanged = !string.Equals(
                this.idleColor, normalizedIdle.Color, StringComparison.Ordinal);

            if (!channelNameChanged
                && !talkgroupChanged
                && !resourceKeyChanged
                && !channelModeChanged
                && !systemNameChanged
                && !rxOnlyChanged
                && !cardSizeChanged
                && !idleColorChanged)
            {
                return;
            }

            this.channelName = channelName;
            this.talkgroup = normalizedTalkgroup;
            this.resourceKey = resourceKey;
            this.channelMode = normalizedMode;
            this.systemName = normalizedSystemName;
            this.isRxOnly = isRxOnly;
            this.cardSize = normalizedCardSize;
            this.idleColor = normalizedIdle.Color;
            this.idleBrush = normalizedIdle.Brush;

            if (channelNameChanged)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChannelName)));
            }

            if (talkgroupChanged)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Talkgroup)));
            }

            if (channelModeChanged)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChannelMode)));
            }

            if (systemNameChanged)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SystemName)));
            }

            if (rxOnlyChanged)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRxOnly)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPttEnabled)));
            }

            if (cardSizeChanged)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardSize)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardWidth)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardHeight)));
            }

            if (idleColorChanged)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IdleColor)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IdleBrush)));
            }
        }

        private static string NormalizeChannelMode(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Trim().ToUpperInvariant();
            return normalized is "DMR" or "P25" or "NXDN"
                ? normalized
                : "P25";
        }

        private static ChannelCardSize ParseCardSize(string? value)
        {
            return value?.Trim().ToLowerInvariant() switch
            {
                "small" => ChannelCardSize.Small,
                "large" => ChannelCardSize.Large,
                _ => ChannelCardSize.Normal,
            };
        }

        private static (string Color, IBrush Brush) NormalizeIdleColor(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)
                && Color.TryParse(value.Trim(), out var parsed))
            {
                return (value.Trim(), new SolidColorBrush(parsed));
            }

            return (DefaultIdleColor, new SolidColorBrush(Color.Parse(DefaultIdleColor)));
        }

        /// <summary>
        /// Applies the WPF page-select guard, toggles the local state, and
        /// raises a request-only event for the shell/backend seam.
        /// </summary>
        public void RequestPageSelect()
        {
            if (isRxOnly || !isSelected)
            {
                return;
            }

            PageState = !PageState;
            PageSelectRequested?.Invoke(this);
        }

        /// <summary>
        /// Applies the WPF marker guard, toggles the local hold state, and
        /// raises a request-only event for the shell/backend seam.
        /// </summary>
        public void RequestMarker()
        {
            if (isRxOnly || !isSelected)
            {
                return;
            }

            HoldState = !HoldState;
            MarkerRequested?.Invoke(this);
        }

        /// <summary>
        /// Raises a request-only call-history event for selected resources.
        /// </summary>
        public void RequestChannelHistory()
        {
            if (!isSelected)
            {
                return;
            }

            ChannelHistoryRequested?.Invoke(this);
        }

        /// <summary>
        /// Applies the WPF selectable-encryption guard, toggles the local
        /// transmit state, and raises a request-only event. Backend security
        /// behavior remains outside this slice.
        /// </summary>
        public void RequestSelectableEncryption()
        {
            if (!isEncryptionSelectable || pttEngaged)
            {
                return;
            }

            IsTxEncrypted = !IsTxEncrypted;
            SelectableEncryptionRequested?.Invoke(this);
        }

        /// <summary>
        /// Raised when <see cref="ChannelName"/>, <see cref="Talkgroup"/>,
        /// <see cref="IsSelected"/>, <see cref="IsPrimary"/>,
        /// <see cref="PttEngaged"/> or <see cref="TarRecordingEnabled"/>
        /// changes.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Raised after a guarded page-select request is accepted.</summary>
        public event Action<ChannelSlotViewModel>? PageSelectRequested;

        /// <summary>Raised after a guarded marker request is accepted.</summary>
        public event Action<ChannelSlotViewModel>? MarkerRequested;

        /// <summary>Raised after a selected-resource history request is accepted.</summary>
        public event Action<ChannelSlotViewModel>? ChannelHistoryRequested;

        /// <summary>Raised after a guarded selectable-encryption request is accepted.</summary>
        public event Action<ChannelSlotViewModel>? SelectableEncryptionRequested;
    }
}
