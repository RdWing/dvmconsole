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
    /// dashboard starts disconnected and awaiting FNE configuration;
    /// connection state is replaced wholesale through
    /// <see cref="SetConnectionState"/>. The channel resources exposed by
    /// <see cref="Channels"/> are the complete channel collection of the
    /// selected codeplug zone in codeplug order; sessions without a
    /// codeplug keep the four fixed compatibility slots. Channel
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
        /// <summary>
        /// The fixed slot count of the no-codeplug compatibility
        /// dashboard. Codeplug sessions project the selected zone's
        /// complete channel collection instead.
        /// </summary>
        private const int CompatibilitySlotCount = 4;

        private const string AudioSavedFeedbackText = "Audio settings saved";
        private const int LegacyDefaultOutputDevice = -1;

        private const string TarSavedFeedbackText = "TAR settings saved.";

        private const string TarSaveFailedFeedbackText = "TAR settings save failed.";

        private const string PreferencesSavedFeedbackText = "Preferences settings saved";

        private const string PreferencesSaveFailedFeedbackText = "Preferences settings save failed.";

        private const string PttHotkeyPermissionFeedbackText =
            "Global hotkey permission required.";

        private const string PttHotkeyUnavailableFeedbackText =
            "Global hotkey unavailable on this host.";

        private readonly SelectedChannelsManager<ChannelSlotViewModel> selectedChannelsManager;

        private readonly AudioSettingsPersistence? audioPersistence;

        private readonly TarSettingsPersistence? tarPersistence;

        private readonly PttSettingsPersistence? pttPersistence;

        private PreferencesSettingsPersistence? preferencesPersistence;

        private RestoreSettingsPersistence? restorePersistence;

        private readonly Dictionary<string, bool> selectableEncryptionStates =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly List<string> pendingRestoreSelectedResourceKeys = new();

        private string? pendingRestorePrimaryResourceKey;

        private bool restoreSelectionApplied;

        private bool restoringSelectionState;

        private readonly Dictionary<string, int> channelOutputDevices =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, string> channelOutputDeviceKeys =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, double> channelVolumes =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly IReadOnlyList<Codeplug.Group> codeplugGroups;

        private readonly Dictionary<string, bool> groupPatchMemberships =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, bool> groupMultiSelectMemberships =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, bool> groupActivePatchMemberships =
            new(StringComparer.OrdinalIgnoreCase);

        private bool projectingAudioState;

        private string audioSaveFeedback = string.Empty;

        private string tarSaveFeedback = string.Empty;

        private string pttHotkeyFeedback = string.Empty;

        private string preferencesSaveFeedback = string.Empty;

        private bool showSystemStatus = true;

        private bool showChannels = true;

        private bool showAlertTones = true;

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
        /// Shell-visible global-hotkey registration feedback. Permission and
        /// unsupported outcomes expose fixed neutral text; a successful or
        /// already-registered outcome clears stale feedback. Change-only.
        /// </summary>
        public string PttHotkeyFeedback => pttHotkeyFeedback;

        /// <summary>
        /// Shell-visible acknowledgement for the operator-preferences
        /// persistence boundary. Success and failure use fixed text and the
        /// property raises change-only notifications.
        /// </summary>
        public string PreferencesSaveFeedback => preferencesSaveFeedback;

        /// <summary>Whether the system-status shell panel is visible.</summary>
        public bool ShowSystemStatus
        {
            get => showSystemStatus;
            private set => SetWidgetVisibilityValue(ref showSystemStatus, value, nameof(ShowSystemStatus));
        }

        /// <summary>Whether the channel-grid shell panel is visible.</summary>
        public bool ShowChannels
        {
            get => showChannels;
            private set => SetWidgetVisibilityValue(ref showChannels, value, nameof(ShowChannels));
        }

        /// <summary>Whether the alert-tone toolbar widgets are visible.</summary>
        public bool ShowAlertTones
        {
            get => showAlertTones;
            private set => SetWidgetVisibilityValue(ref showAlertTones, value, nameof(ShowAlertTones));
        }

        /// <summary>
        /// Applies the three WPF-compatible widget visibility flags. The
        /// view-model owns only observable state; persistence remains in the
        /// shell's layout section adapter.
        /// </summary>
        public void SetWidgetVisibility(
            bool showSystemStatus,
            bool showChannels,
            bool showAlertTones)
        {
            ShowSystemStatus = showSystemStatus;
            ShowChannels = showChannels;
            ShowAlertTones = showAlertTones;
        }

        private void SetWidgetVisibilityValue(ref bool field, bool value, string propertyName)
        {
            if (field == value)
                return;

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private string? audioStatusMessage;

        private string codeplugStatusMessage = string.Empty;

        /// <summary>
        /// Shell-visible codeplug load/reload feedback. Failed parses leave the
        /// current runtime untouched and publish their diagnostic here.
        /// </summary>
        public string CodeplugStatusMessage
        {
            get => codeplugStatusMessage;
            set
            {
                if (codeplugStatusMessage == value)
                {
                    return;
                }

                codeplugStatusMessage = value;
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(CodeplugStatusMessage)));
            }
        }

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
        /// Operator-preferences state seeded from the optional persistence
        /// adapter. The slice raises save requests only for effective
        /// post-hydration changes; runtime application belongs to later
        /// preference gates.
        /// </summary>
        public OperatorPreferencesViewModel? Preferences { get; private set; }

        /// <summary>
        /// Configured web-stream shell items, or null until the window
        /// composes the optional stream-source and persistence boundary.
        /// </summary>
        public WebStreamShellViewModel? WebStreams { get; private set; }

        /// <summary>
        /// Attaches the shell-owned web-stream collection after the window
        /// has composed its shared factories and settings adapters.
        /// </summary>
        public void AttachWebStreams(WebStreamShellViewModel webStreams)
        {
            ArgumentNullException.ThrowIfNull(webStreams);
            if (WebStreams is not null)
                return;

            WebStreams = webStreams;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WebStreams)));
        }

        /// <summary>
        /// Attaches the shared operator-preferences persistence adapter after
        /// shell construction. This preserves the existing MainWindow
        /// constructor's TAR/viewer parameter order while keeping hydration
        /// ahead of all change/save subscriptions.
        /// </summary>
        public void AttachPreferencesPersistence(PreferencesSettingsPersistence persistence)
        {
            ArgumentNullException.ThrowIfNull(persistence);
            if (Preferences is not null)
            {
                return;
            }

            preferencesPersistence = persistence;
            Preferences = new OperatorPreferencesViewModel(persistence);
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(Preferences)));
            Preferences.PropertyChanged += OnPreferencesChanged;
            Preferences.SaveRequested += OnPreferencesSaveRequested;
            ApplyRestoreSelectionIfReady();
        }

        /// <summary>
        /// Applies a group section already owned by the shell. Saving remains
        /// outside the dashboard view-model.
        /// </summary>
        public void ApplyGroupsSection(
            UserSettingsGroupSection? section,
            string? membershipContextKey,
            bool retainPatchStateOnStartup)
        {
            groupPatchMemberships.Clear();
            groupMultiSelectMemberships.Clear();
            groupActivePatchMemberships.Clear();

            string context = membershipContextKey ?? string.Empty;
            Dictionary<string, List<PatchTalkgroupMember>> memberships =
                FindGroupContext(section?.PatchGroupMemberships, context);
            Dictionary<string, bool> enabledStates =
                FindGroupContext(section?.PatchGroupEnabledStates, context);
            HashSet<string> patchGroups = new(
                codeplugGroups.Where(group => group.IsPatchGroup()).Select(group => group.Name),
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> multiSelectGroups = new(
                codeplugGroups.Where(group => group.IsMultiselectGroup()).Select(group => group.Name),
                StringComparer.OrdinalIgnoreCase);

            foreach (var pair in memberships)
            {
                bool isPatch = patchGroups.Contains(pair.Key);
                bool isMultiSelect = multiSelectGroups.Contains(pair.Key);
                bool isActive = isPatch
                    && retainPatchStateOnStartup
                    && enabledStates.TryGetValue(pair.Key, out bool enabled)
                    && enabled;

                foreach (PatchTalkgroupMember? member in pair.Value ?? new List<PatchTalkgroupMember>())
                {
                    if (member is null
                        || string.IsNullOrWhiteSpace(member.SystemName)
                        || string.IsNullOrWhiteSpace(member.Tgid))
                    {
                        continue;
                    }

                    string key = ResourceIdentity.Build(member.SystemName, member.Tgid);
                    if (isPatch)
                    {
                        groupPatchMemberships[key] = true;
                        if (isActive)
                        {
                            groupActivePatchMemberships[key] = true;
                        }
                    }

                    if (isMultiSelect)
                    {
                        groupMultiSelectMemberships[key] = true;
                    }
                }
            }

            ProjectGroupIndicators();
        }

        /// construction. Hydration runs only after the channel collection and
        /// operator preferences exist; hydration suppresses all selection and
        /// primary save callbacks so attaching a file never writes it.
        /// </summary>
        public void AttachRestorePersistence(RestoreSettingsPersistence persistence)
        {
            ArgumentNullException.ThrowIfNull(persistence);
            if (restorePersistence is not null)
            {
                return;
            }

            restorePersistence = persistence;
            UserSettingsRestoreSection section = new();
            try
            {
                if (persistence.TryLoad(out UserSettingsRestoreSection loaded))
                {
                    section = loaded;
                }
            }
            catch
            {
                // Degrade to the empty restore section; persistence must not
                // break dashboard construction or hydration.
            }

            pendingRestoreSelectedResourceKeys.Clear();
            foreach (var key in section.SelectedChannels ?? new List<string>())
            {
                var normalized = NormalizeMonitorResourceKey(key);
                if (normalized.Length > 0
                    && !pendingRestoreSelectedResourceKeys.Contains(
                        normalized,
                        StringComparer.OrdinalIgnoreCase))
                {
                    pendingRestoreSelectedResourceKeys.Add(normalized);
                }
            }

            pendingRestorePrimaryResourceKey = NormalizeMonitorResourceKey(section.PrimaryResourceKey);
            selectableEncryptionStates.Clear();
            foreach (var pair in section.SelectableEncryptionStates
                ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase))
            {
                var normalized = NormalizeMonitorResourceKey(pair.Key);
                if (normalized.Length > 0)
                {
                    selectableEncryptionStates[normalized] = pair.Value;
                }
            }

            foreach (var slot in channels)
            {
                slot.SelectableEncryptionRequested += OnSelectableEncryptionRequested;
            }

            ApplyRestoreEncryptionToCurrentChannels();
            ApplyRestoreSelectionIfReady();
        }

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
        /// resource whose assigned channel shares its normalized resource
        /// key, re-projected when the zone selection changes and refreshed
        /// immediately when an item's Enabled changes.
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

        private IReadOnlyList<ChannelSlotViewModel> channels =
            Array.Empty<ChannelSlotViewModel>();

        /// <summary>
        /// The channel resources of the operator dashboard. With a
        /// codeplug zone selected this is the zone's complete channel
        /// collection in codeplug order — exactly the zone channels,
        /// numbered 1..N with <c>CHANNEL 01</c>-style labels, never
        /// truncated and never padded with filler slots; a zone whose
        /// channel list is null or empty exposes an empty collection.
        /// The collection is rebuilt wholesale on every zone switch
        /// (fresh slot instances, so no stale selection state survives),
        /// and a <see cref="PropertyChanged"/> notification is raised
        /// for <c>Channels</c> so shell bindings re-render. Sessions
        /// without a codeplug keep the four fixed compatibility slots
        /// (numbered 1..4), all unassigned. The
        /// <see cref="ChannelSlotViewModel.TarRecordingEnabled"/>
        /// indicator of each assigned resource is projected from the
        /// composed <see cref="TarConfiguration"/> by normalized
        /// resource key, and stays false when no TAR configuration is
        /// composed or the resource is unassigned.
        /// </summary>
        public IReadOnlyList<ChannelSlotViewModel> Channels => channels;

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
        /// The zone currently driving the channel resources, or null
        /// when the codeplug has no zones. Defaults to the first zone
        /// when zones exist. Change-only: a
        /// <see cref="PropertyChanged"/> notification is raised only
        /// when the value actually changes, and a call that changes
        /// nothing raises nothing. Foreign instances (not a member of
        /// <see cref="Zones"/>) and null (while zones exist) are
        /// rejected as silent no-ops. On an accepted change the channel
        /// collection is rebuilt from the new zone's channels, the
        /// slot-scoped selection is reset wholesale, and the TAR
        /// recording indicators are re-projected from the composed
        /// <see cref="TarConfiguration"/>. The
        /// <see cref="PropertyChanged"/> notification is raised only
        /// after the zone assignment, collection rebuild, and selection
        /// reset are complete, so observers see the fully applied
        /// zone/resource/selection state.
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
        /// Raised when one slot enters or leaves local monitor selection.
        /// The shell uses this to stop only the matching speaker pipeline;
        /// decoded receive observation, network handling and TAR remain
        /// independent.
        /// </summary>
        public event Action<ChannelSlotViewModel, bool>? ChannelSelectionChanged;

        /// <summary>
        /// Raised when a slot's local monitor volume changes.
        /// </summary>
        public event Action<ChannelSlotViewModel>? ChannelVolumeChanged;

        /// <summary>
        /// Raised when a slot's local monitor output changes.
        /// </summary>
        public event Action<ChannelSlotViewModel>? ChannelOutputDeviceChanged;

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
        /// Creates the offline dashboard with a compatibility resource
        /// collection when no codeplug zone is supplied, an FNE connection
        /// manager seeded from the given codeplug
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
        /// (<see cref="SelectedZone"/>); <see cref="Channels"/> exposes
        /// the selected zone's complete channel collection in codeplug
        /// order (empty when the zone's channel list is null or empty).
        /// A null
        /// codeplug leaves <see cref="Zones"/> empty, no zone selected,
        /// and the four unassigned compatibility slots in
        /// <see cref="Channels"/>. A null store leaves
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
        /// persistence leaves the slice exactly request-only. The
        /// post-hydration PTT save event persists effective changes back
        /// through the supplied section adapter; malformed or failed saves
        /// are isolated so they never break dashboard operation.
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
            PttSettingsPersistence? pttPersistence = null,
            PreferencesSettingsPersistence? preferencesPersistence = null)
        {
            VocoderStatus = vocoderStatus is null
                ? null
                : vocoderStatus.IsReady
                    ? $"{VocoderReadiness.LogicalLibraryName} ready"
                    : vocoderStatus.Diagnostic;

            audioPersistence = persistence;

            this.tarPersistence = tarPersistence;

            this.pttPersistence = pttPersistence;

            this.preferencesPersistence = preferencesPersistence;

            codeplugGroups = codeplug?.Groups?.Where(group => group is not null).ToList()
                ?? new List<Codeplug.Group>();

            FneConnections = new FneConnectionManagerViewModel(systems);
            FneConnections.PropertyChanged += OnFneConnectionManagerChanged;

            // Retain the codeplug zones in codeplug order; each zone's
            // channel list passes through as-is, including null lists
            // (an empty codeplug yields no zones).
            Zones = codeplug?.Zones
                ?.Select(zone => new ZoneViewModel(zone.Name, zone.Channels))
                .ToList()
                ?? new List<ZoneViewModel>();

            // Sessions without a codeplug keep the four fixed
            // compatibility slots, all unassigned. Codeplug sessions
            // leave the collection empty here; the SelectedZone
            // assignment below rebuilds it from the selected zone's
            // complete channel collection.
            if (Zones.Count == 0)
            {
                var legacy = new ChannelSlotViewModel[CompatibilitySlotCount];
                for (var i = 0; i < CompatibilitySlotCount; i++)
                {
                    var number = i + 1;
                    legacy[i] = new ChannelSlotViewModel(number, $"CHANNEL {number:00}");
                }

                channels = legacy;
            }

            selectedChannelsManager = new SelectedChannelsManager<ChannelSlotViewModel>(
                selectionVisualChanged: (slot, isSelected) => slot.IsSelected = isSelected,
                primaryVisualChanged: (slot, isPrimary) => slot.IsPrimary = isPrimary);

            selectedChannelsManager.SelectedChannelsChanged += OnSelectedChannelsChanged;
            selectedChannelsManager.PrimaryChannelChanged += OnPrimaryChannelChanged;
            selectedChannelsManager.ChannelSelectionChanged += OnChannelSelectionChanged;

            SelectedChannels = selectedChannelsManager.GetSelectedChannels();
            PrimaryChannel = null;

            // Default the zone selection to the first zone; the setter
            // rebuilds the channel collection from its channels (the
            // four compatibility slots stay when no zones exist).
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

            if (Ptt is not null && pttPersistence is not null)
            {
                Ptt.SaveRequested += OnPttSaveRequested;
            }

            if (preferencesPersistence is not null)
            {
                AttachPreferencesPersistence(preferencesPersistence);
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

                // Project the persisted Enabled state into the resource
                // indicators and subscribe to every item so dialog edits
                // refresh the matching resource immediately. Items are
                // fixed at composition, so the subscription is exactly
                // once per item and never needs renewal.
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

            if (persistence is not null)
            {
                try
                {
                    if (persistence.TryLoad(out UserSettingsAudioSection section))
                    {
                        if (catalog is not null)
                        {
                            savedInputId = AudioSettingsPersistence.ToAudioDeviceId(section.AudioInputDeviceKey);
                            savedOutputId = AudioSettingsPersistence.ToAudioDeviceId(section.MasterOutputDeviceKey);
                            savedAgcEnabled = section.AudioInputAgcEnabled;
                        }

                        CopyAudioMap(channelOutputDevices, section.ChannelOutputDevices);
                        CopyAudioMap(channelOutputDeviceKeys, section.ChannelOutputDeviceKeys);
                        CopyAudioMap(channelVolumes, section.ChannelVolumes);
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

            ApplyPersistedAudioStateToCurrentChannels();

            if (AudioSettings is not null)
            {
                AudioSettings.SaveRequested += OnAudioSaveRequested;
                AudioSettings.PropertyChanged += OnAudioSettingsChanged;
            }
        }

        private static void CopyAudioMap<T>(
            Dictionary<string, T> target,
            IReadOnlyDictionary<string, T>? source)
        {
            target.Clear();
            if (source is null)
            {
                return;
            }

            foreach (var pair in source)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key))
                {
                    target[pair.Key.Trim()] = pair.Value;
                }
            }
        }

        private void ApplyPersistedAudioStateToCurrentChannels()
        {
            projectingAudioState = true;
            try
            {
                foreach (var slot in channels)
                {
                    slot.Volume = ResolveMonitorVolume(slot.ResourceKey);
                    var options = BuildMonitorOutputOptions(slot.ResourceKey);
                    slot.SetMonitorOutputDevices(
                        options,
                        FindConfiguredMonitorOutputOption(slot.ResourceKey, options));
                }
            }
            finally
            {
                projectingAudioState = false;
            }
        }

        private static bool IsInheritMasterOutputKey(string? key)
            => string.Equals(
                key?.Trim(),
                "inherit-master-output",
                StringComparison.OrdinalIgnoreCase);

        private static AudioDeviceId ToMonitorOutputDeviceId(string? key)
        {
            if (string.Equals(
                    key?.Trim(),
                    "inherit-master-output",
                    StringComparison.OrdinalIgnoreCase))
            {
                return AudioDeviceId.Default;
            }

            return AudioSettingsPersistence.ToAudioDeviceId(key);
        }

        private IReadOnlyList<AudioDeviceOptionViewModel> BuildMonitorOutputOptions(
            string? resourceKey)
        {
            if (AudioSettings is null)
            {
                return Array.Empty<AudioDeviceOptionViewModel>();
            }

            var options = new List<AudioDeviceOptionViewModel>(AudioSettings.OutputDevices);
            options.Insert(
                0,
                new AudioDeviceOptionViewModel(
                    AudioDeviceId.Default,
                    "Default (Master Output)",
                    true,
                    isInheritMaster: true));

            if (!string.IsNullOrWhiteSpace(resourceKey)
                && channelOutputDeviceKeys.TryGetValue(resourceKey.Trim(), out var savedKey))
            {
                var savedId = ToMonitorOutputDeviceId(savedKey);
                if (!savedId.IsDefault
                    && !options.Exists(option =>
                        string.Equals(option.Id.Value, savedId.Value, StringComparison.OrdinalIgnoreCase)))
                {
                    options.Add(
                        new AudioDeviceOptionViewModel(
                            savedId,
                            "Saved output device unavailable; using Master Output until it returns",
                            false));
                }
            }

            return options;
        }

        private AudioDeviceOptionViewModel? FindConfiguredMonitorOutputOption(
            string? resourceKey,
            IReadOnlyList<AudioDeviceOptionViewModel> options)
        {
            if (string.IsNullOrWhiteSpace(resourceKey))
            {
                return options.Count > 0 ? options[0] : null;
            }

            if (!channelOutputDeviceKeys.TryGetValue(resourceKey.Trim(), out var savedKey))
            {
                var legacyDevice = TryResolveLegacyOutputDevice(resourceKey.Trim());
                return legacyDevice is { } resolvedLegacy
                    ? options.FirstOrDefault(option =>
                        !option.IsInheritMaster && option.Id == resolvedLegacy)
                        ?? (options.Count > 0 ? options[0] : null)
                    : options.Count > 0 ? options[0] : null;
            }

            if (string.Equals(
                    savedKey.Trim(),
                    "inherit-master-output",
                    StringComparison.OrdinalIgnoreCase))
            {
                return options.FirstOrDefault(option => option.IsInheritMaster);
            }

            var normalizedKey = AudioSettingsPersistence.ToSettingsKey(
                ToMonitorOutputDeviceId(savedKey));
            return options.FirstOrDefault(option =>
                !option.IsInheritMaster
                && string.Equals(
                    AudioSettingsPersistence.ToSettingsKey(option.Id),
                    normalizedKey,
                    StringComparison.OrdinalIgnoreCase));
        }

        private void RefreshMonitorOutputOptions()
        {
            projectingAudioState = true;
            try
            {
                foreach (var slot in channels)
                {
                    var options = BuildMonitorOutputOptions(slot.ResourceKey);
                    slot.SetMonitorOutputDevices(
                        options,
                        FindConfiguredMonitorOutputOption(slot.ResourceKey, options));
                }
            }
            finally
            {
                projectingAudioState = false;
            }
        }

        private void PersistAudioSettings(
            AudioDeviceId inputId,
            AudioDeviceId outputId,
            bool agcEnabled)
        {
            if (audioPersistence is null)
            {
                return;
            }

            try
            {
                if (!audioPersistence.TryLoad(out UserSettingsAudioSection section))
                    section = new UserSettingsAudioSection();

                section.AudioInputDeviceKey = AudioSettingsPersistence.ToSettingsKey(inputId);
                section.MasterOutputDeviceKey = AudioSettingsPersistence.ToSettingsKey(outputId);
                section.AudioInputAgcEnabled = agcEnabled;
                section.ChannelOutputDevices = new Dictionary<string, int>(channelOutputDevices, StringComparer.OrdinalIgnoreCase);
                section.ChannelOutputDeviceKeys = new Dictionary<string, string>(channelOutputDeviceKeys, StringComparer.OrdinalIgnoreCase);
                section.ChannelVolumes = new Dictionary<string, double>(channelVolumes, StringComparer.OrdinalIgnoreCase);
                if (WebStreams is { } webStreams)
                {
                    section.WebStreamVolumes = new Dictionary<string, double>(
                        webStreams.Snapshot().Volumes,
                        StringComparer.OrdinalIgnoreCase);
                }

                audioPersistence.Save(section);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Audio settings persistence failed: {ex}");
            }
        }

        private void OnAudioSaveRequested(
            AudioDeviceId inputId,
            AudioDeviceId outputId,
            bool agcEnabled)
        {
            PersistAudioSettings(inputId, outputId, agcEnabled);

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

        /// <summary>
        /// Receives a coordinator registration outcome from the shell. The
        /// coordinator remains headless; this method owns only the passive
        /// text projection used by the PTT capability panel.
        /// </summary>
        public void ReportPttHotkeyStatus(
            HotkeyRegistrationStatus status,
            HotkeyGesture gesture)
        {
            var feedback = status switch
            {
                HotkeyRegistrationStatus.PermissionDenied => PttHotkeyPermissionFeedbackText,
                HotkeyRegistrationStatus.Unsupported => PttHotkeyUnavailableFeedbackText,
                _ => string.Empty,
            };

            if (pttHotkeyFeedback == feedback)
            {
                return;
            }

            pttHotkeyFeedback = feedback;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(PttHotkeyFeedback)));
        }

        private void OnPttSaveRequested(
            HotkeyGesture? gesture,
            bool toggleMode,
            bool allChannels)
        {
            if (pttPersistence is null)
            {
                return;
            }

            try
            {
                if (!pttPersistence.TryLoad(out UserSettingsPttSection section))
                {
                    section = new UserSettingsPttSection();
                }

                section.TogglePTTMode = toggleMode;
                section.GlobalPTTKeysAllChannels = allChannels;

                if (gesture is null)
                {
                    section.GlobalPTTShortcut = 0;
                }
                else if (PersistedHotkeyEncoder.TryMap(gesture.Value, out var persistedKeys))
                {
                    section.GlobalPTTShortcut = persistedKeys;
                }

                pttPersistence.Save(section);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"PTT settings persistence failed: {ex}");
            }
        }

        private void OnPreferencesSaveRequested()
        {
            if (preferencesPersistence is null || Preferences is null)
            {
                return;
            }

            var feedback = PreferencesSavedFeedbackText;
            try
            {
                if (!preferencesPersistence.TryLoad(out UserSettingsPreferencesSection section))
                {
                    section = new UserSettingsPreferencesSection();
                }

                section.TalkPermitTone = Preferences.TalkPermitTone;
                section.MuteRxAudioWhileTransmitting = Preferences.MuteRxAudioWhileTransmitting;
                section.RetainPatchStateOnStartup = Preferences.RetainPatchStateOnStartup;
                section.RestoreSelectedChannelsOnStartup = Preferences.RestoreSelectedChannelsOnStartup;
                section.DarkMode = Preferences.DarkMode;
                section.KeepWindowOnTop = Preferences.KeepWindowOnTop;
                preferencesPersistence.Save(section);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Operator preferences persistence failed: {ex}");
                feedback = PreferencesSaveFailedFeedbackText;
            }

            if (preferencesSaveFeedback == feedback)
            {
                return;
            }

            preferencesSaveFeedback = feedback;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(PreferencesSaveFeedback)));
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
            if (e.PropertyName is nameof(AudioSettingsViewModel.OutputDevices)
                or nameof(AudioSettingsViewModel.SelectedOutputId))
            {
                RefreshMonitorOutputOptions();
            }

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

        private void OnPreferencesChanged(
            object? sender,
            PropertyChangedEventArgs e)
        {
            if (e.PropertyName is not (
                nameof(OperatorPreferencesViewModel.TalkPermitTone)
                or nameof(OperatorPreferencesViewModel.MuteRxAudioWhileTransmitting)
                or nameof(OperatorPreferencesViewModel.RetainPatchStateOnStartup)
                or nameof(OperatorPreferencesViewModel.RestoreSelectedChannelsOnStartup)
                or nameof(OperatorPreferencesViewModel.DarkMode)
                or nameof(OperatorPreferencesViewModel.KeepWindowOnTop)))
            {
                return;
            }

            if (preferencesSaveFeedback.Length == 0)
            {
                return;
            }

            preferencesSaveFeedback = string.Empty;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(PreferencesSaveFeedback)));
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

        private void ApplyRestoreEncryptionToCurrentChannels()
        {
            foreach (var slot in channels)
            {
                if (slot.IsEncryptionSelectable
                    && !string.IsNullOrWhiteSpace(slot.ResourceKey)
                    && selectableEncryptionStates.TryGetValue(
                        slot.ResourceKey.Trim(),
                        out var encrypted))
                {
                    slot.IsTxEncrypted = encrypted;
                }
            }
        }

        private void ApplyRestoreSelectionIfReady()
        {
            if (restorePersistence is null
                || Preferences is null
                || restoreSelectionApplied)
            {
                return;
            }

            restoreSelectionApplied = true;
            restoringSelectionState = true;
            try
            {
                ApplyRestoreEncryptionToCurrentChannels();
                if (!Preferences.RestoreSelectedChannelsOnStartup)
                {
                    return;
                }

                foreach (var resourceKey in pendingRestoreSelectedResourceKeys)
                {
                    var slot = channels.FirstOrDefault(candidate =>
                        string.Equals(
                            candidate.ResourceKey,
                            resourceKey,
                            StringComparison.OrdinalIgnoreCase));
                    if (slot is not null)
                    {
                        selectedChannelsManager.AddSelectedChannel(slot);
                    }
                }

                var primary = channels.FirstOrDefault(candidate =>
                    candidate.IsSelected
                    && string.Equals(
                        candidate.ResourceKey,
                        pendingRestorePrimaryResourceKey,
                        StringComparison.OrdinalIgnoreCase));
                if (primary is not null)
                {
                    selectedChannelsManager.SetPrimaryChannel(primary);
                }
            }
            finally
            {
                restoringSelectionState = false;
            }
        }

        private void PersistRestoreSelectionState()
        {
            if (restorePersistence is null || restoringSelectionState)
            {
                return;
            }

            try
            {
                if (!restorePersistence.TryLoad(out UserSettingsRestoreSection section))
                {
                    section = new UserSettingsRestoreSection();
                }

                if (Preferences?.RestoreSelectedChannelsOnStartup == true)
                {
                    section.SelectedChannels = SelectedChannels
                        .Select(slot => slot.ResourceKey?.Trim())
                        .Where(key => !string.IsNullOrWhiteSpace(key))
                        .Select(key => key!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    section.PrimaryResourceKey = PrimaryChannel?.IsSelected == true
                        ? PrimaryChannel.ResourceKey?.Trim()
                        : null;

                    if (WebStreams is { } webStreams)
                    {
                        section.SelectedWebStreams = webStreams.Snapshot().SelectedNames.ToList();
                    }
                }
                else
                {
                    section.SelectedChannels = new List<string>();
                    section.PrimaryResourceKey = null;
                    section.SelectedWebStreams = new List<string>();
                }

                section.SelectableEncryptionStates = new Dictionary<string, bool>(
                    selectableEncryptionStates,
                    StringComparer.OrdinalIgnoreCase);
                restorePersistence.Save(section);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Restore settings persistence failed: {ex}");
            }
        }

        private void OnSelectableEncryptionRequested(ChannelSlotViewModel slot)
        {
            if (restorePersistence is null || string.IsNullOrWhiteSpace(slot.ResourceKey))
            {
                return;
            }

            selectableEncryptionStates[slot.ResourceKey.Trim()] = slot.IsTxEncrypted;
            PersistRestoreSelectionState();
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
        /// Applies a channel-resource click with the literal WPF
        /// <c>ProcessSelectionClick</c> branch order through the Core
        /// <see cref="SelectedChannelsManager{T}"/>: a primary click
        /// (setPrimary true) on an already-selected resource sets or
        /// moves the primary, or clears it when the resource is already
        /// primary; any other click toggles membership (select
        /// unselected, deselect selected). A primary click on an
        /// unselected resource selects it only. Deselecting the primary
        /// resource also clears the primary.
        /// </summary>
        /// <param name="slotNumber">
        /// The 1-based resource number to click. With a codeplug zone
        /// active the valid range is 1..<see cref="Channels"/>.Count;
        /// the no-codeplug compatibility dashboard keeps the fixed 1..4
        /// range.
        /// </param>
        /// <param name="setPrimary">True for the primary-toggle (Ctrl-click) variant.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="slotNumber"/> is outside the active resource
        /// collection.
        /// </exception>
        public void ProcessChannelClick(int slotNumber, bool setPrimary)
        {
            if (slotNumber < 1 || slotNumber > Channels.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(slotNumber),
                    slotNumber,
                    $"Slot number must be between 1 and {Channels.Count}.");
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
        /// Toggles selection for every resource in the current zone. If any
        /// current resource is unselected, all are selected; otherwise all
        /// are unselected. Primary state is managed by the same selection
        /// manager path as individual clicks.
        /// </summary>
        public void ToggleSelectAllCurrentZone()
        {
            var selectAll = channels.Any(slot => !slot.IsSelected);
            foreach (var slot in channels)
            {
                if (selectAll && !slot.IsSelected)
                {
                    selectedChannelsManager.AddSelectedChannel(slot);
                }
                else if (!selectAll && slot.IsSelected)
                {
                    selectedChannelsManager.RemoveSelectedChannel(slot);
                }
            }
        }

        /// <summary>
        /// Returns true when a stable resource identity is currently selected
        /// for local monitor playback.
        /// </summary>
        public bool IsMonitorEnabled(string? resourceKey)
        {
            var normalizedResourceKey = NormalizeMonitorResourceKey(resourceKey);
            if (normalizedResourceKey.Length == 0)
            {
                return false;
            }

            foreach (var slot in selectedChannels)
            {
                if (string.Equals(
                        slot.ResourceKey,
                        normalizedResourceKey,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Resolves a resource's output device. A currently available
        /// per-resource stable key wins; stale or missing keys fall back to
        /// the currently available master output, then the platform default.
        /// </summary>
        public AudioDeviceId ResolveMonitorOutputDevice(string? resourceKey)
        {
            var normalizedResourceKey = NormalizeMonitorResourceKey(resourceKey);
            if (normalizedResourceKey.Length > 0)
            {
                if (channelOutputDeviceKeys.TryGetValue(normalizedResourceKey, out var savedKey))
                {
                    if (!IsInheritMasterOutputKey(savedKey))
                    {
                        var resourceDevice = ToMonitorOutputDeviceId(savedKey);
                        if (IsAvailableOutput(resourceDevice))
                        {
                            return resourceDevice;
                        }
                    }
                }
                else if (TryResolveLegacyOutputDevice(normalizedResourceKey) is { } legacyDevice)
                {
                    return legacyDevice;
                }
            }

            var master = AudioSettings?.SelectedOutputId ?? AudioDeviceId.Default;
            return IsAvailableOutput(master) ? master : AudioDeviceId.Default;
        }

        /// <summary>
        /// Resolves a resource volume by stable identity, clamping malformed
        /// persisted values into the WPF-compatible 0..4 range. Unmapped
        /// resources use unity gain.
        /// </summary>
        public float ResolveMonitorVolume(string? resourceKey)
        {
            var normalizedResourceKey = NormalizeMonitorResourceKey(resourceKey);
            if (normalizedResourceKey.Length == 0
                || !channelVolumes.TryGetValue(normalizedResourceKey, out var savedVolume)
                || double.IsNaN(savedVolume)
                || double.IsInfinity(savedVolume))
            {
                return 1.0f;
            }

            return (float)Math.Clamp(savedVolume, 0.0, 4.0);
        }

        private static string NormalizeMonitorResourceKey(string? resourceKey)
        {
            if (string.IsNullOrWhiteSpace(resourceKey))
            {
                return string.Empty;
            }

            var normalized = resourceKey.Trim();
            var slotMarker = normalized.IndexOf("|slot:", StringComparison.OrdinalIgnoreCase);
            return slotMarker > 0 ? normalized[..slotMarker] : normalized;
        }

        private void SetMonitorOutputDevice(
            ChannelSlotViewModel slot,
            AudioDeviceOptionViewModel option)
        {
            ArgumentNullException.ThrowIfNull(slot);
            ArgumentNullException.ThrowIfNull(option);
            if (string.IsNullOrWhiteSpace(slot.ResourceKey))
            {
                return;
            }

            var resourceKey = slot.ResourceKey.Trim();
            if (option.IsInheritMaster)
            {
                channelOutputDevices.Remove(resourceKey);
                channelOutputDeviceKeys.Remove(resourceKey);
            }
            else
            {
                channelOutputDevices[resourceKey] = ResolveLegacyOutputDeviceIndex(option.Id);
                channelOutputDeviceKeys[resourceKey] =
                    AudioSettingsPersistence.ToSettingsKey(option.Id);
            }
            PersistAudioSettings(
                AudioSettings?.SelectedInputId ?? AudioDeviceId.Default,
                AudioSettings?.SelectedOutputId ?? AudioDeviceId.Default,
                AudioSettings?.AgcEnabled ?? false);
            ChannelOutputDeviceChanged?.Invoke(slot);
        }

        /// <summary>
        /// Stores a per-resource output choice by stable identity and saves
        /// the complete audio section. A default id means inherit the current
        /// master output for this convenience API.
        /// </summary>
        public void SetMonitorOutputDevice(ChannelSlotViewModel slot, AudioDeviceId deviceId)
        {
            ArgumentNullException.ThrowIfNull(slot);
            if (string.IsNullOrWhiteSpace(slot.ResourceKey))
            {
                return;
            }

            var resourceKey = slot.ResourceKey.Trim();
            if (deviceId.IsDefault)
            {
                channelOutputDevices.Remove(resourceKey);
                channelOutputDeviceKeys.Remove(resourceKey);
            }
            else
            {
                channelOutputDevices[resourceKey] = ResolveLegacyOutputDeviceIndex(deviceId);
                channelOutputDeviceKeys[resourceKey] =
                    AudioSettingsPersistence.ToSettingsKey(deviceId);
            }
            PersistAudioSettings(
                AudioSettings?.SelectedInputId ?? AudioDeviceId.Default,
                AudioSettings?.SelectedOutputId ?? AudioDeviceId.Default,
                AudioSettings?.AgcEnabled ?? false);
            ChannelOutputDeviceChanged?.Invoke(slot);
        }

        private AudioDeviceId? TryResolveLegacyOutputDevice(string resourceKey)
        {
            if (AudioSettings is null
                || !channelOutputDevices.TryGetValue(resourceKey, out var wantedIndex)
                || wantedIndex < 0)
            {
                return null;
            }

            var currentIndex = 0;
            foreach (var option in AudioSettings.OutputDevices)
            {
                if (option.Id.IsDefault || !option.IsAvailable)
                {
                    continue;
                }

                if (currentIndex == wantedIndex)
                {
                    return option.Id;
                }

                currentIndex++;
            }

            return null;
        }

        private int ResolveLegacyOutputDeviceIndex(AudioDeviceId deviceId)
        {
            if (deviceId.IsDefault || AudioSettings is null)
            {
                return LegacyDefaultOutputDevice;
            }

            var legacyIndex = 0;
            foreach (var option in AudioSettings.OutputDevices)
            {
                if (option.Id.IsDefault)
                {
                    continue;
                }

                if (option.IsAvailable
                    && string.Equals(option.Id.Value, deviceId.Value, StringComparison.OrdinalIgnoreCase))
                {
                    return legacyIndex;
                }

                if (option.IsAvailable)
                {
                    legacyIndex++;
                }
            }

            return LegacyDefaultOutputDevice;
        }

        private bool IsAvailableOutput(AudioDeviceId deviceId)
        {
            if (deviceId.IsDefault)
            {
                return true;
            }

            if (AudioSettings is null)
            {
                return false;
            }

            foreach (var option in AudioSettings.OutputDevices)
            {
                if (option.IsAvailable
                    && string.Equals(option.Id.Value, deviceId.Value, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
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
        /// Rebuilds <see cref="Channels"/> from the selected zone's
        /// channel collection in codeplug order: one slot per zone
        /// channel, numbered 1..N and labelled <c>CHANNEL 01</c>-style,
        /// with the channel name, talkgroup and normalized resource key
        /// (<see cref="ResourceIdentity.Build"/>) assigned wholesale.
        /// A zone with a null or empty channel list yields an empty
        /// collection — no filler slots are ever added. Fresh slot
        /// instances are created for every rebuild, so no selection or
        /// assignment state survives a zone switch. The collection is
        /// replaced wholesale (never mutated in place) and a
        /// <see cref="PropertyChanged"/> notification is raised for
        /// <c>Channels</c> so shell bindings re-render. The TAR
        /// recording indicators are re-projected from the composed
        /// <see cref="TarConfiguration"/> (a missing TAR configuration
        /// keeps every indicator false).
        /// </summary>
        private void ReassignSlotsFromSelectedZone()
        {
            foreach (var oldSlot in channels)
            {
                oldSlot.PropertyChanged -= OnChannelSlotPropertyChanged;
                oldSlot.SelectableEncryptionRequested -= OnSelectableEncryptionRequested;
            }

            projectingAudioState = true;
            try
            {
                var rebuilt = new List<ChannelSlotViewModel>();

                if (selectedZone?.Channels is { } zoneChannels)
                {
                    for (var i = 0; i < zoneChannels.Count; i++)
                    {
                        var channel = zoneChannels[i];
                        var number = i + 1;
                        var slot = new ChannelSlotViewModel(number, $"CHANNEL {number:00}");
                        slot.Reassign(
                            channel.Name,
                            channel.Tgid,
                            ResourceIdentity.Build(channel.System, channel.Tgid),
                            channel.Mode,
                            channel.System,
                            channel.RxOnly,
                            channel.CardSize,
                            channel.ResourceColor);
                        slot.IsEncryptionSelectable = CanSelectEncryption(channel);
                        slot.IsTxEncrypted = ResolveTxEncryption(channel, slot.ResourceKey);
                        slot.Volume = ResolveMonitorVolume(slot.ResourceKey);
                        var outputOptions = BuildMonitorOutputOptions(slot.ResourceKey);
                        slot.SetMonitorOutputDevices(
                            outputOptions,
                            FindConfiguredMonitorOutputOption(slot.ResourceKey, outputOptions));
                        slot.PropertyChanged += OnChannelSlotPropertyChanged;
                        slot.SelectableEncryptionRequested += OnSelectableEncryptionRequested;
                        rebuilt.Add(slot);
                    }
                }

                channels = rebuilt;
                ProjectTarIndicators();
                ProjectGroupIndicators();
            }
            finally
            {
                projectingAudioState = false;
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Channels)));
        }

        /// <summary>
        /// Mirrors the WPF selectable-encryption eligibility rule without
        /// composing security or transmit behavior: only P25 channels with
        /// the codeplug selectable flag and valid configured key material
        /// expose the card indicator/action.
        /// </summary>
        private static bool CanSelectEncryption(Codeplug.Channel channel)
        {
            return string.Equals(channel.Mode?.Trim(), "P25", StringComparison.OrdinalIgnoreCase)
                && channel.SelectableEncryption
                && channel.HasEncryptionConfig();
        }

        private bool ResolveTxEncryption(Codeplug.Channel channel, string? resourceKey)
        {
            if (!channel.HasEncryptionConfig())
            {
                return false;
            }

            if (!CanSelectEncryption(channel))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(resourceKey)
                && selectableEncryptionStates.TryGetValue(resourceKey.Trim(), out var encrypted)
                ? encrypted
                : true;
        }

        /// <summary>
        /// Projects the composed TAR configuration into the channel
        /// indicators: each resource's
        /// <see cref="ChannelSlotViewModel.TarRecordingEnabled"/> becomes
        /// the Enabled state of the TAR item whose resource key matches
        /// the resource's own
        /// <see cref="ChannelSlotViewModel.ResourceKey"/> (normalized by
        /// <see cref="ResourceIdentity.Build"/>), or false when no TAR
        /// configuration is composed, the resource is unassigned, or no
        /// item matches. Every dynamic resource in
        /// <see cref="Channels"/> is projected; the slot setter is
        /// change-only, so re-projection never raises spurious
        /// notifications. This is headless indicator state only: nothing
        /// is persisted here and no UI, recorder or lifecycle code runs.
        /// </summary>
        private void ProjectTarIndicators()
        {
            if (TarConfiguration is null)
            {
                return;
            }

            foreach (var slot in Channels)
            {
                slot.TarRecordingEnabled =
                    FindTarItem(slot.ResourceKey)?.Enabled ?? false;
            }
        }

        /// <summary>
        /// Projects persisted patch and multi-select identity maps onto every
        /// current-zone slot. The slot setters are change-only and the
        /// ChannelSlotViewModel computes WPF priority for the visible badge.
        /// </summary>
        private void ProjectGroupIndicators()
        {
            foreach (ChannelSlotViewModel slot in Channels)
            {
                string key = slot.ResourceKey?.Trim() ?? string.Empty;
                slot.IsPatchGroupMember = key.Length > 0 && groupPatchMemberships.ContainsKey(key);
                slot.IsPatchGroupActive = key.Length > 0 && groupActivePatchMemberships.ContainsKey(key);
                slot.IsMultiSelectMember = key.Length > 0 && groupMultiSelectMemberships.ContainsKey(key);
            }
        }

        private static Dictionary<string, List<PatchTalkgroupMember>> FindGroupContext(
            Dictionary<string, Dictionary<string, List<PatchTalkgroupMember>>>? contexts,
            string context)
        {
            foreach (var pair in contexts ?? new Dictionary<string, Dictionary<string, List<PatchTalkgroupMember>>>())
            {
                if (string.Equals(pair.Key, context, StringComparison.OrdinalIgnoreCase))
                {
                    return new Dictionary<string, List<PatchTalkgroupMember>>(
                        pair.Value ?? new Dictionary<string, List<PatchTalkgroupMember>>(),
                        StringComparer.OrdinalIgnoreCase);
                }
            }

            return new Dictionary<string, List<PatchTalkgroupMember>>(StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, bool> FindGroupContext(
            Dictionary<string, Dictionary<string, bool>>? contexts,
            string context)
        {
            foreach (var pair in contexts ?? new Dictionary<string, Dictionary<string, bool>>())
            {
                if (string.Equals(pair.Key, context, StringComparison.OrdinalIgnoreCase))
                {
                    return new Dictionary<string, bool>(
                        pair.Value ?? new Dictionary<string, bool>(),
                        StringComparer.OrdinalIgnoreCase);
                }
            }

            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
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

        private void OnChannelSelectionChanged(ChannelSlotViewModel slot, bool isSelected)
        {
            ChannelSelectionChanged?.Invoke(slot, isSelected);
        }

        private void OnChannelSlotPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not ChannelSlotViewModel slot
                || projectingAudioState
                || string.IsNullOrWhiteSpace(slot.ResourceKey))
            {
                return;
            }

            if (e.PropertyName == nameof(ChannelSlotViewModel.Volume))
            {
                channelVolumes[slot.ResourceKey.Trim()] = slot.Volume;
                PersistAudioSettings(
                    AudioSettings?.SelectedInputId ?? AudioDeviceId.Default,
                    AudioSettings?.SelectedOutputId ?? AudioDeviceId.Default,
                    AudioSettings?.AgcEnabled ?? false);
                ChannelVolumeChanged?.Invoke(slot);
                return;
            }

            if (e.PropertyName == nameof(ChannelSlotViewModel.MonitorOutputDevice)
                && slot.MonitorOutputDevice is { } output)
            {
                SetMonitorOutputDevice(slot, output);
            }
        }

        private void OnSelectedChannelsChanged()
        {
            SelectedChannels = selectedChannelsManager.GetSelectedChannels();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedChannels)));
            PersistRestoreSelectionState();
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
            PersistRestoreSelectionState();
        }
    }
}
