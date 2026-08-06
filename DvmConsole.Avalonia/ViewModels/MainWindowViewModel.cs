// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using dvmconsole;
using DvmConsole.Platform.Audio;
using DvmConsole.Platform.Hotkeys;

namespace DvmConsole.Avalonia.ViewModels
{
    /// <summary>
    /// Pure managed view-model for the operator dashboard main window. The
    /// dashboard starts disconnected and awaiting FNE configuration, with
    /// exactly four fixed channel slots; connection state is replaced
    /// wholesale through <see cref="SetConnectionState"/>. Channel
    /// selection is tracked through the Core
    /// <see cref="SelectedChannelsManager{T}"/> with literal WPF
    /// <c>ProcessSelectionClick</c> semantics via
    /// <see cref="ProcessChannelClick"/>. This class is deliberately free
    /// of Avalonia, protocol, network, and native behavior; the optional
    /// audio-settings slice is composed only from an injected portable
    /// catalog so it can still be driven headlessly.
    /// </summary>
    public sealed class MainWindowViewModel : INotifyPropertyChanged
    {
        private const int ChannelCount = 4;

        private readonly SelectedChannelsManager<ChannelSlotViewModel> selectedChannelsManager;

        private IReadOnlyCollection<ChannelSlotViewModel> selectedChannels =
            Array.Empty<ChannelSlotViewModel>();

        /// <summary>The fixed product name shown by the dashboard.</summary>
        public string ProductName { get; } = "DVM Console";

        /// <summary>
        /// The FNE connection-manager slice. Constructed empty unless a
        /// codeplug system list is injected through the constructor; the
        /// dashboard header mirrors its connected aggregate when that state
        /// changes.
        /// </summary>
        public FneConnectionManagerViewModel FneConnections { get; }

        /// <summary>
        /// The audio-settings slice composed from the injected device
        /// catalog, or null when no catalog was provided. Get-only and
        /// constructed exactly once; the slice performs no catalog event
        /// subscription and owns no disposable resources.
        /// </summary>
        public AudioSettingsViewModel? AudioSettings { get; }

        /// <summary>
        /// The PTT capability slice composed from the injected hotkey
        /// service, or null when no service was provided. Get-only and
        /// constructed exactly once; the slice is wired to the LIVE
        /// dashboard selection, resolving the primary and selected
        /// channels at press time, and performs no service query until
        /// its <c>SetHotkey</c> is called. Owns no disposable resources.
        /// </summary>
        public PttCapabilityViewModel? Ptt { get; }

        /// <summary>
        /// The connection state label, e.g. <c>OFFLINE</c> or
        /// <c>LINKED</c>. Set verbatim by <see cref="SetConnectionState"/>.
        /// </summary>
        public string ConnectionLabel { get; private set; } = "OFFLINE";

        /// <summary>
        /// The connection detail line, e.g.
        /// <c>Awaiting FNE configuration</c> or the FNE endpoint. Set
        /// verbatim by <see cref="SetConnectionState"/>.
        /// </summary>
        public string ConnectionDetail { get; private set; } = "Awaiting FNE configuration";

        /// <summary>True when the console is connected to the FNE.</summary>
        public bool IsConnected { get; private set; }

        /// <summary>True when the operator may initiate a connection.</summary>
        public bool CanConnect { get; private set; } = true;

        /// <summary>
        /// The fixed channel slots of the dashboard, numbered 1..4.
        /// Exposed read-only; the backing collection is never mutated
        /// after construction.
        /// </summary>
        public IReadOnlyList<ChannelSlotViewModel> Channels { get; }

        /// <summary>
        /// A detached snapshot of the currently selected slots. Every
        /// access returns a fresh collection instance that is independent
        /// of the view-model: mutating it never affects the selection, and
        /// the snapshot is refreshed whenever the selection changes.
        /// </summary>
        public IReadOnlyCollection<ChannelSlotViewModel> SelectedChannels
        {
            get => new List<ChannelSlotViewModel>(selectedChannels);
            private set => selectedChannels = value;
        }

        /// <summary>
        /// The current primary channel, or null when no primary is set.
        /// </summary>
        public ChannelSlotViewModel? PrimaryChannel { get; private set; }

        /// <summary>
        /// Raised whenever a connection-state property changes. All four
        /// properties are reported on every <see cref="SetConnectionState"/>
        /// call, in the locked order: ConnectionLabel, ConnectionDetail,
        /// IsConnected, CanConnect. Also raised for SelectedChannels and
        /// PrimaryChannel when the selection changes.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Creates the offline dashboard with exactly four channel slots
        /// and an empty FNE connection manager.
        /// </summary>
        public MainWindowViewModel()
            : this(null)
        {
        }

        /// <summary>
        /// Creates the offline dashboard with exactly four channel slots
        /// and an FNE connection manager seeded from the given codeplug
        /// systems; a null list yields an empty manager. No audio catalog
        /// is composed.
        /// </summary>
        public MainWindowViewModel(IReadOnlyList<Codeplug.System>? systems)
            : this(systems, null)
        {
        }

        /// <summary>
        /// Creates the offline dashboard with exactly four channel slots,
        /// an FNE connection manager seeded from the given codeplug
        /// systems, and an audio-settings slice composed from the given
        /// device catalog; null systems yield an empty manager and a null
        /// catalog yields a null <see cref="AudioSettings"/>. No hotkey
        /// service is composed, so <see cref="Ptt"/> stays null.
        /// </summary>
        public MainWindowViewModel(
            IReadOnlyList<Codeplug.System>? systems,
            IAudioDeviceCatalog? catalog)
            : this(systems, catalog, null)
        {
        }

        /// <summary>
        /// Creates the offline dashboard with exactly four channel slots,
        /// an FNE connection manager seeded from the given codeplug
        /// systems, an audio-settings slice composed from the given
        /// device catalog, and a PTT capability slice composed from the
        /// given hotkey service; null systems yield an empty manager, a
        /// null catalog yields a null <see cref="AudioSettings"/>, and a
        /// null hotkey service yields a null <see cref="Ptt"/>.
        /// </summary>
        public MainWindowViewModel(
            IReadOnlyList<Codeplug.System>? systems,
            IAudioDeviceCatalog? catalog,
            IGlobalHotkeyService? hotkeys)
        {
            FneConnections = new FneConnectionManagerViewModel(systems);
            FneConnections.PropertyChanged += OnFneConnectionManagerChanged;

            var channels = new ChannelSlotViewModel[ChannelCount];
            for (var i = 0; i < ChannelCount; i++)
            {
                var number = i + 1;
                channels[i] = new ChannelSlotViewModel(number, $"CHANNEL {number:00}");
            }

            Channels = channels;

            selectedChannelsManager = new SelectedChannelsManager<ChannelSlotViewModel>(
                selectionVisualChanged: (slot, isSelected) => slot.IsSelected = isSelected,
                primaryVisualChanged: (slot, isPrimary) => slot.IsPrimary = isPrimary);

            selectedChannelsManager.SelectedChannelsChanged += OnSelectedChannelsChanged;
            selectedChannelsManager.PrimaryChannelChanged += OnPrimaryChannelChanged;

            SelectedChannels = selectedChannelsManager.GetSelectedChannels();
            PrimaryChannel = null;

            Ptt = hotkeys is null
                ? null
                : new PttCapabilityViewModel(hotkeys, () => PrimaryChannel, () => SelectedChannels);

            AudioSettings = catalog is null ? null : new AudioSettingsViewModel(catalog);
        }

        private void OnFneConnectionManagerChanged(
            object? sender,
            PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(FneConnectionManagerViewModel.ConnectedSystemSummary))
            {
                return;
            }

            if (FneConnections.AnyConnected)
            {
                SetConnectionState(
                    "LINKED",
                    FneConnections.ConnectedSystemSummary ?? "FNE connected",
                    isConnected: true);
            }
            else
            {
                SetConnectionState(
                    "OFFLINE",
                    "Awaiting FNE configuration",
                    isConnected: false);
            }
        }

        /// <summary>
        /// Replaces the connection state wholesale. Nonblank label and
        /// detail strings are preserved verbatim, including surrounding
        /// whitespace; null or whitespace-only values are programming
        /// errors and are rejected with <see cref="ArgumentException"/>.
        /// Notifications are raised on every call, even when values are
        /// unchanged.
        /// </summary>
        public void SetConnectionState(string label, string detail, bool isConnected)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                throw new ArgumentException("Connection label must be nonblank.", nameof(label));
            }

            if (string.IsNullOrWhiteSpace(detail))
            {
                throw new ArgumentException("Connection detail must be nonblank.", nameof(detail));
            }

            ConnectionLabel = label;
            ConnectionDetail = detail;
            IsConnected = isConnected;
            CanConnect = !isConnected;

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionDetail)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsConnected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanConnect)));
        }

        /// <summary>
        /// Applies a channel-slot click with the literal WPF
        /// <c>ProcessSelectionClick</c> branch order through the Core
        /// <see cref="SelectedChannelsManager{T}"/>: a primary click
        /// (setPrimary true) on an already-selected slot sets or moves the
        /// primary, or clears it when the slot is already primary; any
        /// other click toggles membership (select unselected, deselect
        /// selected). A primary click on an unselected slot selects it
        /// only. Deselecting the primary slot also clears the primary.
        /// </summary>
        /// <param name="slotNumber">The 1-based slot number to click.</param>
        /// <param name="setPrimary">True for the primary-toggle (Ctrl-click) variant.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="slotNumber"/> is outside the valid 1..4 range.
        /// </exception>
        public void ProcessChannelClick(int slotNumber, bool setPrimary)
        {
            if (slotNumber < 1 || slotNumber > ChannelCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(slotNumber),
                    slotNumber,
                    $"Slot number must be between 1 and {ChannelCount}.");
            }

            var slot = Channels[slotNumber - 1];

            if (slot.IsSelected && setPrimary)
            {
                // WPF Ctrl-click branch: toggle PRIMARY state instead of deselecting.
                if (selectedChannelsManager.PrimaryChannel == slot)
                {
                    selectedChannelsManager.ClearPrimaryChannel();
                }
                else
                {
                    selectedChannelsManager.SetPrimaryChannel(slot);
                }

                return;
            }

            if (slot.IsSelected)
            {
                selectedChannelsManager.RemoveSelectedChannel(slot);
            }
            else
            {
                selectedChannelsManager.AddSelectedChannel(slot);
            }
        }

        private void OnSelectedChannelsChanged()
        {
            SelectedChannels = selectedChannelsManager.GetSelectedChannels();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedChannels)));
        }

        private void OnPrimaryChannelChanged()
        {
            if (PrimaryChannel is { } previous)
            {
                previous.IsPrimary = false;
            }

            var current = selectedChannelsManager.PrimaryChannel;
            if (current is not null)
            {
                current.IsPrimary = true;
            }

            PrimaryChannel = current;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PrimaryChannel)));
        }
    }
}
