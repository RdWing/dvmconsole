// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using dvmconsole;
using DvmConsole.Avalonia.Input;
using DvmConsole.Avalonia.Persistence;
using DvmConsole.Avalonia.Services;
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

        private const string TarSavedFeedbackText = "TAR settings saved.";

        private const string TarSaveFailedFeedbackText = "TAR settings save failed.";

        private readonly SelectedChannelsManager<ChannelSlotViewModel> selectedChannelsManager;

        private readonly AudioSettingsPersistence? audioPersistence;

        private readonly TarSettingsPersistence? tarPersistence;

        /// <summary>
        /// The normalized resource key (<see cref="ResourceIdentity.Build"/>)
        /// of the codeplug channel currently assigned to each fixed slot,
        /// or null for unassigned slots. Kept in sync by
        /// <see cref="ReassignSlotsFromSelectedZone"/> and consumed by the
        /// TAR indicator projection.
        /// </summary>
        private readonly string?[] slotResourceKeys = new string?[ChannelCount];

        private string audioSaveFeedback = string.Empty;

        private string tarSaveFeedback = string.Empty;

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

        private string? audioStatusMessage;

        /// <summary>
        /// Shell-visible audio status line written by the window when the
        /// talkgroup audio router reports a capture end or a monitor
        /// stream end, marshalled onto the UI thread by the shell. Null
        /// when no status is outstanding. Change-only: a
        /// <see cref="PropertyChanged"/> notification is raised only when
        /// the value actually changes.
        /// </summary>
        public string? AudioStatusMessage
        {
            get => audioStatusMessage;
            set
            {
                if (audioStatusMessage == value)
                {
                    return;
                }

                audioStatusMessage = value;
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(AudioStatusMessage)));
            }
        }

        private string tarViewerStatusMessage = string.Empty;

        /// <summary>
        /// Shell-visible TAR Viewer composition status. The MainWindow writes
        /// an explanatory message when a required runtime dependency is absent;
        /// successful viewer creation clears it.
        /// </summary>
        public string TarViewerStatusMessage
        {
            get => tarViewerStatusMessage;
            set
            {
                if (tarViewerStatusMessage == value)
                {
                    return;
                }

                tarViewerStatusMessage = value;
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(TarViewerStatusMessage)));
            }
        }

        /// <summary>
        /// The PTT capability slice composed from the injected hotkey
        /// service, or null when no service was provided. Get-only and
        /// constructed exactly once; the slice is wired to the LIVE
        /// dashboard selection, resolving the primary and selected
        /// channels at press time, and performs no service query until
        /// its <c>SetHotkey</c> is called. When PTT settings persistence
        /// is also composed, the slice is seeded from the persisted
        /// section at construction (toggle mode, all-channels scope,
        /// and a mapped hotkey gesture). This composition is
        /// deliberately load-only: reverse hotkey encoding and two-way
        /// save wiring are deferred to a later seam. Owns no disposable
        /// resources.
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
        /// The call-history slice composed from the injected store, or
        /// null when no store was provided. Get-only and constructed
        /// exactly once; the slice performs no store-event subscription
        /// (the shell refreshes it via the store's
        /// <see cref="CallHistoryStore.Changed"/> event) and owns no
        /// disposable resources.
        /// </summary>
        public CallHistoryViewModel? CallHistory { get; }

        /// <summary>
        /// The TAR configuration slice composed from the injected codeplug
        /// and TAR settings persistence, or null when either was not
        /// provided. Get-only and constructed exactly once: the slice is
        /// projected from <c>codeplug.Zones</c> in codeplug order and seeded
        /// from the persisted section loaded at construction (a missing,
        /// malformed or unreadable load degrades to the section DTO
        /// defaults without throwing), with the persisted recording root
        /// supplied as both the configured and the fallback folder. Each
        /// channel's settings resolve with the WPF
        /// <c>SettingsManager.GetTarChannelConfig</c> lookup order
        /// (SettingsManager.cs:1780-1801): the resource key first, then
        /// the legacy talkgroup id, then the legacy channel name, otherwise
        /// the new default <see cref="TarChannelConfig"/>. The persisted
        /// map is already normalized (trimmed keys, blank keys skipped,
        /// case-insensitive) by the persistence adapter, and the resolver
        /// never mutates the loaded section's config instances. The slice
        /// performs no persistence I/O on its own; commits raise
        /// <c>SaveRequested</c> for the dashboard to persist. The slice
        /// also feeds the dashboard's slot indicators: each item's
        /// <see cref="TarConfigurationViewModel.TarChannelConfigItem.Enabled"/>
        /// is projected into the
        /// <see cref="ChannelSlotViewModel.TarRecordingEnabled"/> of the
        /// fixed slot whose assigned channel shares its normalized
        /// resource key, re-projected when the zone selection changes and
        /// refreshed immediately when an item's Enabled changes.
        /// </summary>
        public TarConfigurationViewModel? TarConfiguration { get; }

        /// <summary>
        /// Shell-visible persistence result for the TAR configuration
        /// slice, or empty when no save has been committed. The composed
        /// <see cref="TarConfiguration"/> raises <c>SaveRequested</c> when
        /// the dashboard commits; this property then reports the
        /// persistence boundary result exactly: <c>TAR settings saved.</c>
        /// on a successful write, or <c>TAR settings save failed.</c> when
        /// the persistence write throws (the exception is isolated to a
        /// debug diagnostic and never escapes the headless save event).
        /// Feedback ownership is split from the slice's own
        /// validation/payload status: <see cref="TarConfigurationViewModel.StatusText"/>
        /// and <see cref="TarConfigurationViewModel.ErrorText"/> describe
        /// the payload, while this property owns the persistence boundary
        /// result. Get-only and change-only: a
        /// <see cref="PropertyChanged"/> notification is raised only when
        /// the value actually changes, and sessions without a composed
        /// slice keep this permanently empty and never subscribe.
        /// </summary>
        public string TarSaveFeedback => tarSaveFeedback;

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
        /// after construction. The
        /// <see cref="ChannelSlotViewModel.TarRecordingEnabled"/>
        /// indicator of each assigned slot is projected from the composed
        /// <see cref="TarConfiguration"/> by normalized resource key, and
        /// stays false when no TAR configuration is composed or the slot
        /// is unassigned.
        /// </summary>
        public IReadOnlyList<ChannelSlotViewModel> Channels { get; }

        /// <summary>
        /// The codeplug zones retained by the dashboard in codeplug
        /// order, or empty when no codeplug was composed. Each entry
        /// wraps a codeplug zone wholesale, including zones whose
        /// channel list is null. Get-only; the collection is fixed at
        /// construction.
        /// </summary>
        public IReadOnlyList<ZoneViewModel> Zones { get; }

        private ZoneViewModel? selectedZone;

        /// <summary>
        /// The zone currently driving the four channel slots, or null
        /// when the codeplug has no zones. Defaults to the first zone
        /// when zones exist. Change-only: a
        /// <see cref="PropertyChanged"/> notification is raised only
        /// when the value actually changes, and a call that changes
        /// nothing raises nothing. Foreign instances (not a member of
        /// <see cref="Zones"/>) and null (while zones exist) are
        /// rejected as silent no-ops. On an accepted change the four
        /// slots are re-assigned from the new zone's channels, the
        /// slot-scoped selection is reset wholesale, and the TAR
        /// recording indicators are re-projected from the composed
        /// <see cref="TarConfiguration"/>. The
        /// <see cref="PropertyChanged"/> notification is raised only
        /// after the zone assignment, slot reassignment, and selection
        /// reset are complete, so observers see the fully applied
        /// zone/slot/selection state.
        /// </summary>
        public ZoneViewModel? SelectedZone
        {
            get => selectedZone;
            set
            {
                if (ReferenceEquals(selectedZone, value))
                {
                    return;
                }

                // The selection must be a member of Zones (reference
                // identity); foreign instances are rejected silently.
                if (value is not null && !Zones.Contains(value))
                {
                    return;
                }

                // While zones exist the dashboard always has a selected
                // zone; null is rejected silently.
                if (value is null && Zones.Count > 0)
                {
                    return;
                }

                selectedZone = value;

                // Selection is slot-scoped: the slots are about to be
                // re-pointed at a different zone's channels, so any
                // selection or primary on the old assignments is
                // meaningless. Reset before re-assigning; a silent
                // primary retarget would surprise the operator.
                ResetSelectionAndPrimary();
                ReassignSlotsFromSelectedZone();

                // Publish last: observers of this notification must
                // see the zone, slots, and selection fully applied.
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(SelectedZone)));
            }
        }

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
        /// an optional startup vocoder-readiness result, an optional
        /// codeplug for the zone/channel UI slice, an optional
        /// call-history store for the CALL HISTORY slice, optional TAR
        /// settings persistence, and optional PTT settings persistence.
        /// Null systems
        /// yield an empty manager,
        /// a null catalog yields a null <see cref="AudioSettings"/>, and
        /// a null hotkey service yields a null <see cref="Ptt"/>. When
        /// the catalog and persistence are both supplied, the audio
        /// section is loaded at construction and its keys are mapped to
        /// device ids that seed the audio-settings slice; a missing,
        /// malformed or unreadable load degrades to the default ids and
        /// default AGC state without throwing. A null persistence keeps
        /// the slice exactly request-only. A null readiness result
        /// leaves <see cref="VocoderStatus"/> null; otherwise it is
        /// composed exactly once from the result. When a codeplug is
        /// supplied, its zones are retained in codeplug order as
        /// <see cref="Zones"/> with the first zone selected by default
        /// (<see cref="SelectedZone"/>); the four slots are assigned
        /// from the selected zone's channels (first <c>ChannelCount</c>,
        /// with slots beyond the list staying unassigned — null
        /// <see cref="ChannelSlotViewModel.ChannelName"/>). A null
        /// codeplug leaves <see cref="Zones"/> empty, no zone selected,
        /// and every slot unassigned. A null store leaves
        /// <see cref="CallHistory"/> null, keeping the CALL HISTORY
        /// panel in its muted "not attached" state. When a codeplug and
        /// TAR settings persistence are both supplied, the TAR
        /// configuration slice is composed from the codeplug zones and
        /// the persisted section (<see cref="TarConfiguration"/>); a
        /// null persistence leaves it null with
        /// <see cref="TarSaveFeedback"/> permanently empty. When the
        /// hotkey service and PTT settings persistence are both
        /// supplied, the PTT slice is seeded at construction from the
        /// persisted section: <see cref="PttCapabilityViewModel.ToggleMode"/>
        /// from <c>TogglePTTMode</c>,
        /// <see cref="PttCapabilityViewModel.AllChannels"/> from
        /// <c>GlobalPTTKeysAllChannels</c>, and the hotkey from
        /// <c>GlobalPTTShortcut</c> decoded through the persisted-hotkey
        /// mapper; a missing, malformed or unreadable load, or an
        /// unsupported or zero persisted shortcut, degrades to the PTT
        /// defaults already held by the slice without throwing. A null
        /// persistence leaves the slice exactly request-only. This
        /// composition is deliberately load-only: reverse hotkey
        /// encoding and two-way save wiring are deferred to a later
        /// seam.
        /// </summary>
        public MainWindowViewModel(
            IReadOnlyList<Codeplug.System>? systems,
            IAudioDeviceCatalog? catalog,
            IGlobalHotkeyService? hotkeys,
            AudioSettingsPersistence? persistence,
            VocoderReadinessResult? vocoderStatus,
            Codeplug? codeplug = null,
            CallHistoryStore? callHistory = null,
            TarSettingsPersistence? tarPersistence = null,
            PttSettingsPersistence? pttPersistence = null)
        {
            VocoderStatus = vocoderStatus is null
                ? null
                : vocoderStatus.IsReady
                    ? $"{VocoderReadiness.LogicalLibraryName} ready"
                    : vocoderStatus.Diagnostic;

            audioPersistence = persistence;

            this.tarPersistence = tarPersistence;

            FneConnections = new FneConnectionManagerViewModel(systems);
            FneConnections.PropertyChanged += OnFneConnectionManagerChanged;

            // Retain the codeplug zones in codeplug order; each zone's
            // channel list passes through as-is, including null lists
            // (an empty codeplug yields no zones).
            Zones = codeplug?.Zones
                ?.Select(zone => new ZoneViewModel(zone.Name, zone.Channels))
                .ToList()
                ?? new List<ZoneViewModel>();

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

            // Default the zone selection to the first zone; the setter
            // assigns the slots from its channels (all unassigned when
            // no zones exist).
            SelectedZone = Zones.Count > 0 ? Zones[0] : null;

            Ptt = hotkeys is null
                ? null
                : new PttCapabilityViewModel(hotkeys, () => PrimaryChannel, () => SelectedChannels);

            // Load-only PTT settings composition: when the PTT slice and
            // PTT settings persistence are both composed, the persisted
            // section seeds the slice before the hotkey-capture slice is
            // built. The persisted raw WPF Keys integer is decoded through
            // PersistedHotkeyMapper; an unsupported or zero shortcut
            // leaves Ptt.Hotkey null while the mode and scope still load.
            // A missing, malformed or unreadable load degrades to the PTT
            // defaults already held by the slice without throwing.
            // Deliberately load-only: no HotkeyChangeRequested
            // subscription, reverse encoding, or save wiring exist yet.
            if (Ptt is not null && pttPersistence is not null)
            {
                try
                {
                    if (pttPersistence.TryLoad(out UserSettingsPttSection pttSection))
                    {
                        Ptt.ToggleMode = pttSection.TogglePTTMode;
                        Ptt.AllChannels = pttSection.GlobalPTTKeysAllChannels;
                        if (PersistedHotkeyMapper.TryMap(
                            pttSection.GlobalPTTShortcut,
                            out HotkeyGesture gesture))
                        {
                            Ptt.SetHotkey(gesture);
                        }
                    }
                }
                catch
                {
                    // Degrade to the PTT defaults already held by the
                    // slice; persistence must never break dashboard
                    // construction.
                }
            }

            HotkeyCapture = Ptt is null ? null : new HotkeyCaptureViewModel(Ptt);

            CallHistory = callHistory is null
                ? null
                : new CallHistoryViewModel(callHistory);

            // TAR configuration slice: composed only when a codeplug and
            // TAR settings persistence are both supplied. The persisted
            // section is loaded at construction; a missing, malformed or
            // unreadable load degrades to the section DTO defaults without
            // throwing. The persisted recording root seeds both the
            // configured and the fallback folder, and the persisted config
            // map backs a resolver with the WPF
            // SettingsManager.GetTarChannelConfig lookup order.
            UserSettingsTarSection tarSection = new UserSettingsTarSection();
            if (codeplug is not null && tarPersistence is not null)
            {
                try
                {
                    if (tarPersistence.TryLoad(out UserSettingsTarSection loaded))
                    {
                        tarSection = loaded;
                    }
                }
                catch
                {
                    // Degrade to the section DTO defaults; persistence must
                    // never break dashboard construction.
                }

                TarConfiguration = new TarConfigurationViewModel(
                    codeplug.Zones,
                    (resourceKey, channelName, talkgroupId) =>
                    {
                        // WPF SettingsManager.GetTarChannelConfig
                        // (SettingsManager.cs:1780-1801) lookup order:
                        // resource key, then legacy talkgroup id, then
                        // legacy channel name, else the new default. The
                        // adapter's map is already normalized (trimmed
                        // keys, blank keys skipped, case-insensitive), so
                        // the dictionary comparer provides the lookup
                        // semantics; the loaded config instances are
                        // returned untouched.
                        Dictionary<string, TarChannelConfig> loadedConfigs = tarSection.TarChannelConfigs;
                        if (!string.IsNullOrWhiteSpace(resourceKey)
                            && loadedConfigs.TryGetValue(resourceKey, out TarChannelConfig? resourceConfig))
                        {
                            return resourceConfig;
                        }

                        if (!string.IsNullOrWhiteSpace(talkgroupId)
                            && loadedConfigs.TryGetValue(talkgroupId, out TarChannelConfig? talkgroupConfig))
                        {
                            return talkgroupConfig;
                        }

                        if (!string.IsNullOrWhiteSpace(channelName)
                            && loadedConfigs.TryGetValue(channelName, out TarChannelConfig? channelConfig))
                        {
                            return channelConfig;
                        }

                        return new TarChannelConfig();
                    },
                    tarSection.TarRecordingsRootPath,
                    tarSection.TarRecordingsRootPath);
            }

            if (TarConfiguration is not null)
            {
                TarConfiguration.SaveRequested += OnTarSaveRequested;

                // Project the persisted Enabled state into the fixed slot
                // indicators and subscribe to every item so dialog edits
                // refresh the matching slot immediately. Items are fixed
                // at composition, so the subscription is exactly once per
                // item and never needs renewal.
                ProjectTarIndicators();
                foreach (TarConfigurationViewModel.TarZoneConfigGroup group in TarConfiguration.ZoneGroups)
                {
                    foreach (TarConfigurationViewModel.TarChannelConfigItem item in group.Channels)
                    {
                        item.PropertyChanged += OnTarChannelConfigItemChanged;
                    }
                }
            }

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

        private void OnTarSaveRequested(
            string recordingFolderPath,
            IReadOnlyDictionary<string, TarChannelConfig> configs)
        {
            // The persistence boundary result is owned by this property:
            // a successful write reports the fixed success text, a
            // throwing write is isolated to a debug diagnostic and
            // reports the fixed failure text. The exception must never
            // escape the headless save event.
            string feedback = TarSavedFeedbackText;
            if (tarPersistence is { } persistence)
            {
                try
                {
                    persistence.Save(recordingFolderPath, configs);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"TAR settings persistence failed: {ex}");
                    feedback = TarSaveFailedFeedbackText;
                }
            }

            // The acknowledgement text is fixed and change-only.
            if (tarSaveFeedback == feedback)
            {
                return;
            }

            tarSaveFeedback = feedback;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(TarSaveFeedback)));
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

        /// <summary>
        /// Resets the slot-scoped selection wholesale: the selection
        /// manager is cleared (unselecting the old slot instances and
        /// nulling the primary through its own event path), then every
        /// slot is forced back to the unselected, non-primary state so
        /// no card carries stale flags regardless of manager
        /// bookkeeping.
        /// </summary>
        private void ResetSelectionAndPrimary()
        {
            selectedChannelsManager.ClearPrimaryChannel();
            selectedChannelsManager.ClearSelections();

            foreach (var slot in Channels)
            {
                slot.IsSelected = false;
                slot.IsPrimary = false;
            }
        }

        /// <summary>
        /// Assigns slots 0..3 (1-based 1..4) from the selected zone's
        /// channels in order — the first <see cref="ChannelCount"/>
        /// channels. A zone with a null channel list, or fewer channels
        /// than slots, leaves the remainder unassigned (null
        /// <see cref="ChannelSlotViewModel.ChannelName"/>, <c>NO
        /// TALKGROUP</c>). Each slot's normalized resource key
        /// (<see cref="ResourceIdentity.Build"/>) is retained alongside
        /// the assignment, and the TAR recording indicators are
        /// re-projected from the composed <see cref="TarConfiguration"/>
        /// (a missing TAR configuration keeps every indicator false).
        /// </summary>
        private void ReassignSlotsFromSelectedZone()
        {
            var zoneChannels = selectedZone?.Channels;

            for (var i = 0; i < ChannelCount; i++)
            {
                var slot = Channels[i];

                if (zoneChannels is not null && i < zoneChannels.Count)
                {
                    var channel = zoneChannels[i];
                    slot.Reassign(channel.Name, channel.Tgid);
                    slotResourceKeys[i] = ResourceIdentity.Build(channel.System, channel.Tgid);
                }
                else
                {
                    slot.Reassign(null, null);
                    slotResourceKeys[i] = null;
                }
            }

            ProjectTarIndicators();
        }

        /// <summary>
        /// Projects the composed TAR configuration into the fixed slot
        /// indicators: each slot's
        /// <see cref="ChannelSlotViewModel.TarRecordingEnabled"/> becomes
        /// the Enabled state of the TAR item whose resource key matches
        /// the slot's assigned channel (normalized by
        /// <see cref="ResourceIdentity.Build"/>), or false when no TAR
        /// configuration is composed, the slot is unassigned, or no item
        /// matches. The slot setter is change-only, so re-projection never
        /// raises spurious notifications. This is headless indicator
        /// state only: nothing is persisted here and no UI, recorder or
        /// lifecycle code runs.
        /// </summary>
        private void ProjectTarIndicators()
        {
            if (TarConfiguration is null)
            {
                return;
            }

            for (var i = 0; i < ChannelCount; i++)
            {
                Channels[i].TarRecordingEnabled =
                    FindTarItem(slotResourceKeys[i])?.Enabled ?? false;
            }
        }

        /// <summary>
        /// Finds the TAR configuration item whose resource key matches the
        /// given key (case-insensitive, mirroring the persistence
        /// adapter's normalization), scanning every zone group in
        /// composition order, or null when no item matches. Items sharing
        /// a resource key are kept synchronized by the TAR slice itself,
        /// so the first match is authoritative.
        /// </summary>
        private TarConfigurationViewModel.TarChannelConfigItem? FindTarItem(string? resourceKey)
        {
            if (TarConfiguration is null || string.IsNullOrWhiteSpace(resourceKey))
            {
                return null;
            }

            foreach (TarConfigurationViewModel.TarZoneConfigGroup group in TarConfiguration.ZoneGroups)
            {
                foreach (TarConfigurationViewModel.TarChannelConfigItem item in group.Channels)
                {
                    if (string.Equals(item.ResourceKey, resourceKey, StringComparison.OrdinalIgnoreCase))
                    {
                        return item;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Refreshes the slot indicators when a TAR configuration item's
        /// <see cref="TarConfigurationViewModel.TarChannelConfigItem.Enabled"/>
        /// changes, so dialog edits surface on the dashboard immediately.
        /// Other item properties do not affect the indicators and are
        /// ignored. The projection itself is change-only, so a refresh
        /// that changes nothing raises nothing.
        /// </summary>
        private void OnTarChannelConfigItemChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(TarConfigurationViewModel.TarChannelConfigItem.Enabled))
            {
                return;
            }

            ProjectTarIndicators();
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
