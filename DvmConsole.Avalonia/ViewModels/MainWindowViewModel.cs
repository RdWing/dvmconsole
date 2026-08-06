// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using dvmconsole;
using DvmConsole.Avalonia.Persistence;
using DvmConsole.Platform.Audio;
using DvmConsole.Platform.Hotkeys;
using DvmConsole.Platform.Native;

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

        private const string AudioSavedFeedbackText = "Audio settings saved";

        private readonly SelectedChannelsManager<ChannelSlotViewModel> selectedChannelsManager;

        private readonly AudioSettingsPersistence? audioPersistence;

        private string audioSaveFeedback = string.Empty;

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
        /// Shell-visible acknowledgement for the audio-settings slice, or
        /// empty when no acknowledgement is outstanding. The composed
        /// <see cref="AudioSettings"/> raises <c>SaveRequested</c> when
        /// the dashboard commits; this property then becomes the exact
        /// text <c>Audio settings saved</c>. Any change-only selection or
        /// AGC change (SelectedInputId, SelectedOutputId, AgcEnabled)
        /// clears it back to empty. Get-only and change-only: a
        /// <see cref="PropertyChanged"/> notification is raised only when
        /// the value actually changes, and sessions without a catalog
        /// keep this permanently empty and never subscribe.
        /// </summary>
        public string AudioSaveFeedback => audioSaveFeedback;

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
        /// The hotkey-capture slice composed from the PTT capability
        /// slice, or null when no hotkey service was provided (and thus
        /// <see cref="Ptt"/> is null). Get-only and constructed exactly
        /// once from the composed <see cref="Ptt"/>; performs no service
        /// query at construction and owns no disposable resources.
        /// </summary>
        public HotkeyCaptureViewModel? HotkeyCapture { get; }

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
        /// null hotkey service yields a null <see cref="Ptt"/>. No
        /// audio persistence is composed, so the slice is request-only.
        /// </summary>
        public MainWindowViewModel(
            IReadOnlyList<Codeplug.System>? systems,
            IAudioDeviceCatalog? catalog,
            IGlobalHotkeyService? hotkeys)
            : this(systems, catalog, hotkeys, null)
        {
        }

        /// <summary>
        /// Startup-only vocoder readiness status, or null when no
        /// readiness result was composed. A ready result maps to the
        /// stable text <c>libvocoder ready</c>; a failure exposes the
        /// probe diagnostic verbatim. Get-only and never notified:
        /// composed exactly once at construction, this is startup
        /// status, not session state.
        /// </summary>
        public string? VocoderStatus { get; }

        /// <summary>
        /// Creates the offline dashboard with exactly four channel slots,
        /// an FNE connection manager seeded from the given codeplug
        /// systems, an audio-settings slice composed from the given
        /// device catalog, a PTT capability slice composed from the
        /// given hotkey service, and optional audio-settings persistence.
        /// Null systems yield an empty manager, a null catalog yields a
        /// null <see cref="AudioSettings"/>, and a null hotkey service
        /// yields a null <see cref="Ptt"/>. When the catalog and
        /// persistence are both supplied, the audio section is loaded at
        /// construction and its keys are mapped to device ids that seed
        /// the audio-settings slice; a missing, malformed or unreadable
        /// load degrades to the default ids and default AGC state without
        /// throwing. A null persistence keeps the slice exactly
        /// request-only. No vocoder readiness result is composed, so
        /// <see cref="VocoderStatus"/> stays null.
        /// </summary>
        public MainWindowViewModel(
            IReadOnlyList<Codeplug.System>? systems,
            IAudioDeviceCatalog? catalog,
            IGlobalHotkeyService? hotkeys,
            AudioSettingsPersistence? persistence)
            : this(systems, catalog, hotkeys, persistence, null)
        {
        }

        /// <summary>
        /// Creates the offline dashboard with exactly four channel slots,
        /// an FNE connection manager seeded from the given codeplug
        /// systems, an audio-settings slice composed from the given
        /// device catalog, a PTT capability slice composed from the
        /// given hotkey service, optional audio-settings persistence,
        /// and an optional startup vocoder-readiness result. Null
        /// systems yield an empty manager, a null catalog yields a
        /// null <see cref="AudioSettings"/>, and a null hotkey service
        /// yields a null <see cref="Ptt"/>. When the catalog and
        /// persistence are both supplied, the audio section is loaded at
        /// construction and its keys are mapped to device ids that seed
        /// the audio-settings slice; a missing, malformed or unreadable
        /// load degrades to the default ids and default AGC state without
        /// throwing. A null persistence keeps the slice exactly
        /// request-only. A null readiness result leaves
        /// <see cref="VocoderStatus"/> null; otherwise it is composed
        /// exactly once from the result.
        /// </summary>
        public MainWindowViewModel(
            IReadOnlyList<Codeplug.System>? systems,
            IAudioDeviceCatalog? catalog,
            IGlobalHotkeyService? hotkeys,
            AudioSettingsPersistence? persistence,
            VocoderReadinessResult? vocoderStatus)
        {
            VocoderStatus = vocoderStatus is null
                ? null
                : vocoderStatus.IsReady
                    ? $"{VocoderReadiness.LogicalLibraryName} ready"
                    : vocoderStatus.Diagnostic;

            audioPersistence = persistence;

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

            HotkeyCapture = Ptt is null ? null : new HotkeyCaptureViewModel(Ptt);

            var savedInputId = AudioDeviceId.Default;
            var savedOutputId = AudioDeviceId.Default;
            var savedAgcEnabled = false;

            if (catalog is not null && persistence is not null)
            {
                try
                {
                    if (persistence.TryLoad(out UserSettingsAudioSection section))
                    {
                        savedInputId = AudioSettingsPersistence.ToAudioDeviceId(section.AudioInputDeviceKey);
                        savedOutputId = AudioSettingsPersistence.ToAudioDeviceId(section.MasterOutputDeviceKey);
                        savedAgcEnabled = section.AudioInputAgcEnabled;
                    }
                }
                catch
                {
                    // Degrade to defaults; persistence must never break
                    // dashboard construction.
                }
            }

            AudioSettings = catalog is null
                ? null
                : new AudioSettingsViewModel(catalog, savedInputId, savedOutputId, savedAgcEnabled);

            if (AudioSettings is not null)
            {
                AudioSettings.SaveRequested += OnAudioSaveRequested;
                AudioSettings.PropertyChanged += OnAudioSettingsChanged;
            }
        }

        private void OnAudioSaveRequested(
            AudioDeviceId inputId,
            AudioDeviceId outputId,
            bool agcEnabled)
        {
            // Persist the payload when a store is composed. Failure is
            // isolated to a diagnostic: a malformed or I/O-unsafe save
            // must never escape or prevent the acknowledgement below.
            if (audioPersistence is not null)
            {
                try
                {
                    audioPersistence.Save(new UserSettingsAudioSection
                    {
                        AudioInputDeviceKey = AudioSettingsPersistence.ToSettingsKey(inputId),
                        MasterOutputDeviceKey = AudioSettingsPersistence.ToSettingsKey(outputId),
                        AudioInputAgcEnabled = agcEnabled
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Audio settings persistence failed: {ex}");
                }
            }

            // The acknowledgement text is fixed and change-only; the
            // payload values are intentionally ignored beyond persistence.
            if (audioSaveFeedback == AudioSavedFeedbackText)
            {
                return;
            }

            audioSaveFeedback = AudioSavedFeedbackText;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(AudioSaveFeedback)));
        }

        private void OnAudioSettingsChanged(
            object? sender,
            PropertyChangedEventArgs e)
        {
            if (e.PropertyName is not (
                nameof(AudioSettingsViewModel.SelectedInputId)
                or nameof(AudioSettingsViewModel.SelectedOutputId)
                or nameof(AudioSettingsViewModel.AgcEnabled)))
            {
                return;
            }

            if (audioSaveFeedback.Length == 0)
            {
                return;
            }

            audioSaveFeedback = string.Empty;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(AudioSaveFeedback)));
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
