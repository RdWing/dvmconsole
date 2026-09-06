// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Media;
using DvmConsole.Application;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace DvmConsole.Presentation;

public sealed class ConfigurationStudioViewModel :
    INotifyPropertyChanged,
    IConfigurationStudioNavigationViewModel,
    IConfigurationStudioFieldEditSession,
    IConfigurationStudioEditTransactionPort,
    IConfigurationStudioEntityEditSession,
    IConfigurationStudioCompanionCommandSession
{
    private static readonly IBrush ErrorIndicatorBrush = new SolidColorBrush(Color.Parse("#E5484D"));
    private static readonly IBrush WarningIndicatorBrush = new SolidColorBrush(Color.Parse("#F2B134"));
    private static readonly IBrush ValidIndicatorBrush = new SolidColorBrush(Color.Parse("#5AC878"));
    private readonly IConfigurationStudioRuntimeContext runtimeContext;
    private readonly IConfigurationStudioCompanionSource companionSource;
    private readonly ConfigurationStudioPreviewState previewState;
    private ConfigurationId? configurationId;
    private string documentIdentity;
    private readonly ConfigurationDraftIdentityRegistry identities = new();
    private readonly ConfigurationIdentityMigrationPlanner migrationPlanner;
    private readonly Dictionary<ZoneConfiguration, string> draftZoneSystemNames = [];
    private readonly HashSet<Guid> draftCallPrioritySystemIds = [];
    private readonly ConfigurationStudioHierarchyProjector hierarchyProjector = new();
    private readonly ConfigurationStudioDraftProjector draftProjector = new();
    private readonly Dictionary<ChannelConfiguration, ConfigurationChannelRow> channelRows = [];
    private ConfigurationDocument document;
    private readonly ConfigurationStudioEditTransactions editTransactions;
    private readonly ConfigurationStudioEntityEditController entityEdits;
    private readonly ConfigurationStudioCompanionCommandController companionCommands;
    private readonly ConfigurationStudioNavigationState navigationState;
    private SystemConfiguration? selectedSystem;
    private ZoneConfiguration? selectedZone;
    private ChannelConfiguration? selectedChannel;
    private ConfigurationStreamRow? selectedStream;
    private GroupConfiguration? selectedGroup;
    private KeyEntry? selectedKey;
    private readonly ConfigurationStudioCompanionState companions = new();
    private readonly ObservableCollection<KeyEntry> keyEntries = [];
    private readonly ObservableCollection<ConfigurationAliasRow> aliases = [];
    private string searchText = string.Empty;
    private string channelSearchText = string.Empty;
    private ConfigurationAliasRow? selectedAlias;
    private ConfigurationChannelRow? selectedChannelRow;
    private ConfigurationHierarchyNode? selectedHierarchyNode;
    private bool isZonePreviewExpanded = true;
    private bool isValidationDrawerOpen;
    private string selectedChannelKeyIdHexDigits = string.Empty;
    private string selectedKeyIdHexDigits = string.Empty;
    private bool selectedKeyIdInputInvalid;
    private SystemConfiguration? selectedAliasSystem;
    private EncryptionAlgorithmOption? selectedChannelAlgorithm;
    private EncryptionAlgorithmOption? selectedKeyAlgorithm;
    private IReadOnlyList<EncryptionAlgorithmOption> availableChannelAlgorithms = [];
    private IReadOnlyList<EncryptionAlgorithmOption> availableKeyAlgorithms = [];
    private readonly Dictionary<Guid, string> lastSystemRenameTargets = [];

    public ConfigurationStudioViewModel(
        ConfigurationDocument document,
        ConfigurationId? configurationId,
        string documentIdentity,
        IConfigurationStudioRuntimeContext runtimeContext,
        IConfigurationStudioCompanionSource companionSource,
        IConfigurationStudioPreviewFactory previewFactory,
        ConfigurationStudioInitialState initialState,
        ConfigurationStudioSection initialSection)
    {
        this.document = document ?? throw new ArgumentNullException(nameof(document));
        this.configurationId = configurationId;
        this.documentIdentity = !string.IsNullOrWhiteSpace(documentIdentity)
            ? documentIdentity
            : throw new ArgumentException("A host document identity is required.", nameof(documentIdentity));
        this.runtimeContext = runtimeContext ?? throw new ArgumentNullException(nameof(runtimeContext));
        this.companionSource = companionSource ?? throw new ArgumentNullException(nameof(companionSource));
        ArgumentNullException.ThrowIfNull(previewFactory);
        KeyEntries = new ReadOnlyObservableCollection<KeyEntry>(keyEntries);
        Aliases = new ReadOnlyObservableCollection<ConfigurationAliasRow>(aliases);
        ArgumentNullException.ThrowIfNull(initialState);
        identities.RegisterInitial(document.Configuration);
        migrationPlanner = new ConfigurationIdentityMigrationPlanner(document.Configuration, identities);
        navigationState = new ConfigurationStudioNavigationState(initialSection);
        ResetSystemRenameTargets();
        previewState = new ConfigurationStudioPreviewState(
            runtimeContext,
            previewFactory,
            initialState.ChannelPositions);
        InitializeDraftZoneSystems(initialState.ZoneSystemAssignments);
        InitializeDraftCallPrioritySystems(initialState.CallPrioritySystemNames);
        previewState.Synchronize(Configuration.Zones);
        hierarchyProjector.NodePropertyChanged += HandleHierarchyNodePropertyChanged;
        LoadReferencedCompanions();
        editTransactions = new ConfigurationStudioEditTransactions(this, this);
        RefreshCollections();
        ConfigurationStudioDraftSnapshot initialSnapshot = CaptureDraftSnapshot();
        // A newly created ConfigurationDocument deliberately starts dirty: it
        // already owns a managed draft ID but has never produced a committed
        // revision. Preserve that distinction instead of treating the initial
        // empty document as a saved baseline.
        editTransactions.Initialize(initialSnapshot, document.IsDirty);
        entityEdits = new ConfigurationStudioEntityEditController(this);
        companionCommands = new ConfigurationStudioCompanionCommandController(this);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<ConfigurationStudioNavigationItem> Navigation => navigationState.Items;
    public ObservableCollection<ConfigurationHierarchyNode> ConfigurationHierarchy { get; } = [];
    System.Collections.IEnumerable IConfigurationStudioNavigationViewModel.ConfigurationHierarchy
        => ConfigurationHierarchy;
    public ObservableCollection<SystemConfiguration> Systems { get; } = [];
    public ObservableCollection<ZoneConfiguration> Zones { get; } = [];
    public ObservableCollection<ChannelConfiguration> Channels { get; } = [];
    public ObservableCollection<ConfigurationChannelRow> VisibleChannelRows { get; } = [];
    public ObservableCollection<ConfigurationStreamRow> Streams { get; } = [];
    public ObservableCollection<GroupConfiguration> Groups { get; } = [];
    public ReadOnlyObservableCollection<KeyEntry> KeyEntries { get; }
    public ReadOnlyObservableCollection<ConfigurationAliasRow> Aliases { get; }
    public ObservableCollection<IConfigurationChannelPreviewViewModel> PreviewChannels { get; } = [];
    public ObservableCollection<ConfigurationValidationIssue> ValidationIssues { get; } = [];
    public IReadOnlyList<PatchGroupEditorViewModel> OperationalGroups => runtimeContext.OperationalGroups;
    public IReadOnlyList<ConfigurationProtocolOption> ModeOptions { get; } = ConfigurationProtocolCatalog.ForChannels;
    public IReadOnlyList<string> CardSizeOptions { get; } = ["small", "normal", "large"];
    public IReadOnlyList<string> TransportModeOptions { get; } = ["auto", "ecb", "cbc"];
    public IReadOnlyList<string> GroupTypeOptions { get; } = ["patch", "multiselect"];
    public IReadOnlyList<ConfigurationProtocolOption> ProtocolOptions { get; } = ConfigurationProtocolCatalog.ForEncryptionKeys;

    public ConfigurationDocument Document => document;
    internal string DocumentIdentity => documentIdentity;
    public ConsoleConfiguration Configuration => document.Configuration;
    public bool PatchSourceIdPassthrough
    {
        get => Configuration.PatchSourceIdPassthrough;
        set
        {
            if (!CanEdit || Configuration.PatchSourceIdPassthrough == value)
                return;

            ConfigurationStudioDraftSnapshot before = editTransactions.Draft.Current;
            Configuration.PatchSourceIdPassthrough = value;
            CompleteDraftTransition(before, markDocumentDirty: true);
            RefreshValidation();
            NotifyDocumentState();
            OnPropertyChanged();
        }
    }
    public bool CanEdit => !document.IsReadOnly;
    public bool CanEditSelectedAlias => CanEdit && SelectedAlias is not null;
    public bool CanAddChannelToSelectedSystem => CanEdit && SelectedSystem is not null;
    public bool CanSaveDraft => CanEdit && IsDirty;
    public bool CanExportSanitized => !document.IsReadOnly;
    public bool IsDirty => editTransactions.Draft.IsDirty;
    public bool IsKeyFileDirty => companions.IsKeyFileDirty;
    public bool LayoutChanged => previewState.LayoutChanged;
    public bool AliasFilesDirty => companions.AliasFilesDirty;
    public bool CanUndo => editTransactions.Draft.CanUndo;
    public bool CanRedo => editTransactions.Draft.CanRedo;
    public bool HasErrors => ValidationIssues.Any(issue => issue.IsError);
    public bool HasWarnings => ValidationIssues.Any(issue => !issue.IsError);
    public bool HasValidationIssues => ValidationIssues.Count > 0;
    public IBrush ValidationIndicatorBrush => HasErrors
        ? ErrorIndicatorBrush
        : HasWarnings ? WarningIndicatorBrush : ValidIndicatorBrush;
    public string StatusText => document.IsReadOnly
        ? document.ReadOnlyReason ?? "Read-only YAML"
        : IsDirty ? "Draft has unsaved changes" : "No unsaved changes";
    public string IssueSummary
    {
        get
        {
            int errors = ValidationIssues.Count(issue => issue.IsError);
            int warnings = ValidationIssues.Count - errors;
            return $"{errors} error{(errors == 1 ? string.Empty : "s")}, {warnings} warning{(warnings == 1 ? string.Empty : "s")}";
        }
    }
    public string ConfigurationShapeText
        => $"{Systems.Count} systems  •  {Zones.Count} zones  •  {Configuration.Zones.Sum(zone => zone.Channels.Count)} channels  •  {Streams.Count} streams  •  {Groups.Count} groups";
    public string SelectedZoneHeading => SelectedZone is null
        ? "Zone"
        : $"Zone: {SelectedZone.Name}  ({Channels.Count} channels)  ·  FNE: {SelectedZoneSystemDisplayName}";
    public string SelectedZoneSystemDisplayName => SelectedZone is null
        ? "None"
        : string.IsNullOrWhiteSpace(SelectedZoneSystemName) ? "Unassigned or mixed" : SelectedZoneSystemName;
    public string SelectedZoneName
    {
        get => SelectedZone?.Name ?? string.Empty;
        set
        {
            if (SelectedZone is null || string.Equals(SelectedZone.Name, value, StringComparison.Ordinal))
                return;
            SelectedZone.Name = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedZoneHeading));
        }
    }
    public string SelectedZoneSystemName
    {
        get => SelectedZone is not null && draftZoneSystemNames.TryGetValue(SelectedZone, out string? systemName)
            ? systemName
            : string.Empty;
        set
        {
            if (SelectedZone is null || string.IsNullOrWhiteSpace(value))
                return;
            string normalized = value.Trim();
            bool changed = !draftZoneSystemNames.TryGetValue(SelectedZone, out string? current) ||
                           !string.Equals(current, normalized, StringComparison.OrdinalIgnoreCase) ||
                           SelectedZone.Channels.Any(channel =>
                               !string.Equals(channel.System, normalized, StringComparison.OrdinalIgnoreCase));
            if (!changed)
                return;
            draftZoneSystemNames[SelectedZone] = normalized;
            foreach (ChannelConfiguration channel in SelectedZone.Channels)
                channel.System = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedZoneSystemDisplayName));
            OnPropertyChanged(nameof(SelectedZoneHeading));
        }
    }
    public string ValidationStatusText => HasValidationIssues ? IssueSummary : "No errors";
    public string ValidationDrawerHeading => HasErrors
        ? "Fix these issues before saving"
        : "Configuration warnings";
    public string SystemNavigationHeading => $"FNE Systems ({Systems.Count})";
    public string ZoneNavigationHeading => $"Zones & Channels ({Zones.Count})";
    public string StreamNavigationHeading => $"Web Streams ({Streams.Count})";
    public string GroupNavigationHeading => $"Groups ({Groups.Count})";
    public string KeyNavigationHeading => $"Encryption Keys ({KeyEntries.Count})";
    public string FileNavigationHeading => $"Files & Interoperability ({document.UnknownFields.Count})";
    public double PreviewCanvasWidth => Math.Max(runtimeContext.DefaultCanvasWidth, PreviewChannels.Count == 0
        ? runtimeContext.DefaultCanvasWidth
        : PreviewChannels.Max(preview => preview.X + preview.CardWidth + 12));
    public double PreviewCanvasHeight => Math.Max(runtimeContext.CardHeight + 12, PreviewChannels.Count == 0
        ? runtimeContext.CardHeight + 12
        : PreviewChannels.Max(preview => preview.Y + preview.CardHeight + 12));
    public double PreviewCardHeight => runtimeContext.CardHeight;
    public double PreviewUiFontSize => runtimeContext.UiFontSize;
    public double PreviewUiSmallFontSize => runtimeContext.UiSmallFontSize;
    public double PreviewUiCompactFontSize => runtimeContext.UiCompactFontSize;
    public bool IsZonePreviewExpanded
    {
        get => isZonePreviewExpanded;
        set => SetField(ref isZonePreviewExpanded, value);
    }
    public bool IsValidationDrawerOpen
    {
        get => isValidationDrawerOpen;
        set => SetField(ref isValidationDrawerOpen, value);
    }
    public string UnknownFieldsText => document.UnknownFields.Count == 0
        ? "No unmatched YAML fields were found."
        : $"{document.UnknownFields.Count} unmatched field(s) will be preserved when their containing item is retained.";
    public string FullExportText => document.IsReadOnly ? document.SourceText : document.Serialize();
    public string KeyFileIdentifierText => DisplayFileReference(companions.KeyFileIdentifier) ?? "No key file is referenced.";
    public string KeyFileReferenceText => string.IsNullOrWhiteSpace(Configuration.KeyFile)
        ? "No managed key file selected"
        : DisplayFileReference(Configuration.KeyFile) ?? "No managed key file selected";
    public bool HasKeyFile => companions.HasKeyFile;
    public bool CanEditKeyFile => CanEdit && HasKeyFile;
    public bool CanUseOperationalGroups =>
        runtimeContext.IsActiveConfiguration(configurationId, documentIdentity);
    public string OperationalGroupHint => CanUseOperationalGroups
        ? "Enabled changes apply immediately. Use Apply changes to save membership and direction without rewriting YAML or reconnecting FNE sessions."
        : "Operational controls are unavailable because this draft is unsaved or is not the active codeplug.";
    public string ReviewSaveButtonText => IsGroups ? "Save YAML changes…" : "Review & Save";

    public IReadOnlyList<EncryptionAlgorithmOption> AvailableChannelAlgorithms => availableChannelAlgorithms;
    public IReadOnlyList<EncryptionAlgorithmOption> AvailableKeyAlgorithms => availableKeyAlgorithms;

    public EncryptionAlgorithmOption? SelectedChannelAlgorithm
    {
        get => selectedChannelAlgorithm;
        set
        {
            if (SelectedChannel is null || value is null ||
                !availableChannelAlgorithms.Any(option =>
                    string.Equals(option.ConfigurationValue, value.ConfigurationValue, StringComparison.OrdinalIgnoreCase)) ||
                !SetField(ref selectedChannelAlgorithm, value))
                return;
            SelectedChannel.Algo = value.ConfigurationValue;
            OnPropertyChanged(nameof(ChannelEncryptionUsesKey));
        }
    }

    public EncryptionAlgorithmOption? SelectedKeyAlgorithm
    {
        get => selectedKeyAlgorithm;
        set
        {
            if (SelectedKey is null || value?.AlgorithmId is not int algorithmId ||
                !availableKeyAlgorithms.Any(option => option.AlgorithmId == algorithmId) ||
                !SetField(ref selectedKeyAlgorithm, value))
                return;
            SelectedKey.AlgId = algorithmId;
            OnPropertyChanged(nameof(SelectedKeyAlgorithmIdText));
        }
    }

    public string? SelectedKeyProtocol
    {
        get => SelectedKey?.Protocol;
        set
        {
            if (SelectedKey is null || string.IsNullOrWhiteSpace(value) ||
                string.Equals(SelectedKey.Protocol, value, StringComparison.OrdinalIgnoreCase))
                return;
            SelectedKey.Protocol = value.Trim().ToLowerInvariant();
            OnPropertyChanged();
        }
    }

    public string? SelectedKeySystem
    {
        get => SelectedKey?.System;
        set
        {
            if (SelectedKey is null || string.IsNullOrWhiteSpace(value))
                return;
            string normalized = value.Trim();
            if (string.Equals(SelectedKey.System, normalized, StringComparison.OrdinalIgnoreCase))
                return;
            SelectedKey.System = normalized;
            OnPropertyChanged();
        }
    }

    public string SelectedKeyAlgorithmIdText => SelectedKeyAlgorithm?.AlgorithmIdText ?? "—";
    public bool ChannelEncryptionUsesKey => SelectedChannelAlgorithm?.AlgorithmId is not null;
    public bool IsSelectedChannelDmr => string.Equals(SelectedChannel?.Mode, "dmr", StringComparison.OrdinalIgnoreCase);

    public string SelectedChannelKeyIdHexDigits
    {
        get => selectedChannelKeyIdHexDigits;
        set
        {
            string normalized = EncryptionAlgorithmCatalog.StripHexPrefix(value).Trim().ToUpperInvariant();
            if (!SetField(ref selectedChannelKeyIdHexDigits, normalized) || SelectedChannel is null)
                return;
            SelectedChannel.KeyId = normalized.Length == 0 ? null : $"0x{normalized}";
        }
    }

    public string SelectedKeyIdHexDigits
    {
        get => selectedKeyIdHexDigits;
        set
        {
            string normalized = EncryptionAlgorithmCatalog.StripHexPrefix(value).Trim().ToUpperInvariant();
            if (!SetField(ref selectedKeyIdHexDigits, normalized) || SelectedKey is null)
                return;
            selectedKeyIdInputInvalid = !ConfigurationStudioKeyEditor.TryParseKeyId(
                normalized,
                out ushort keyId);
            if (!selectedKeyIdInputInvalid)
                SelectedKey.KeyId = keyId;
        }
    }

    public string SearchText
    {
        get => searchText;
        set
        {
            if (!SetField(ref searchText, value ?? string.Empty))
                return;
            RefreshConfigurationHierarchy(searchText.Trim());
        }
    }

    public string ChannelSearchText
    {
        get => channelSearchText;
        set
        {
            if (!SetField(ref channelSearchText, value ?? string.Empty))
                return;
            RefreshVisibleChannelRows();
        }
    }

    public ConfigurationStudioNavigationItem SelectedNavigation
    {
        get => navigationState.Current;
        set
        {
            if (value is null || ReferenceEquals(navigationState.Current, value))
                return;
            CommitPendingEdits();
            if (!navigationState.Select(value))
                return;
            OnPropertyChanged();
            NotifySectionVisibility();
        }
    }

    public bool IsOverview => navigationState.Is(ConfigurationStudioSection.Overview);
    public bool IsSystems => navigationState.Is(ConfigurationStudioSection.Systems);
    public bool IsZones => navigationState.Is(ConfigurationStudioSection.Zones);
    public bool IsStreams => navigationState.Is(ConfigurationStudioSection.Streams);
    public bool IsGroups => navigationState.Is(ConfigurationStudioSection.Groups);
    public bool IsEncryptionKeys => navigationState.Is(ConfigurationStudioSection.EncryptionKeys);
    public bool IsFiles => navigationState.Is(ConfigurationStudioSection.Files);

    public SystemConfiguration? SelectedSystem
    {
        get => selectedSystem;
        set
        {
            if (SetField(ref selectedSystem, value))
            {
                OnPropertyChanged(nameof(SelectedSystemHasCallPriority));
                OnPropertyChanged(nameof(CanAddChannelToSelectedSystem));
            }
        }
    }
    public bool SelectedSystemHasCallPriority
    {
        get => SelectedSystem is not null &&
            draftCallPrioritySystemIds.Contains(identities.GetSystemId(SelectedSystem));
        set
        {
            if (!CanEdit || SelectedSystem is null)
                return;

            ConfigurationStudioDraftSnapshot before = editTransactions.Draft.Current;
            Guid systemId = identities.GetSystemId(SelectedSystem);
            bool changed = value
                ? draftCallPrioritySystemIds.Add(systemId)
                : draftCallPrioritySystemIds.Remove(systemId);
            if (!changed)
                return;

            CompleteDraftTransition(before);
            OnPropertyChanged();
            NotifyDocumentState();
        }
    }
    public ZoneConfiguration? SelectedZone
    {
        get => selectedZone;
        set
        {
            if (!SetField(ref selectedZone, value))
                return;
            RefreshChannelsAndPreview();
            OnPropertyChanged(nameof(SelectedZoneName));
            OnPropertyChanged(nameof(SelectedZoneHeading));
            OnPropertyChanged(nameof(SelectedZoneSystemName));
            OnPropertyChanged(nameof(SelectedZoneSystemDisplayName));
        }
    }
    public ChannelConfiguration? SelectedChannel
    {
        get => selectedChannel;
        set
        {
            if (!SetField(ref selectedChannel, value))
                return;
            ConfigurationChannelRow? matchingRow = VisibleChannelRows.FirstOrDefault(row => ReferenceEquals(row.Channel, value));
            if (!ReferenceEquals(selectedChannelRow, matchingRow))
            {
                selectedChannelRow = matchingRow;
                OnPropertyChanged(nameof(SelectedChannelRow));
            }
            RefreshChannelEditorState();
            OnPropertyChanged(nameof(IsSelectedChannelDmr));
            foreach (IConfigurationChannelPreviewViewModel preview in PreviewChannels)
                preview.IsSelected = ReferenceEquals(preview.Channel, value);
            if (value is not null &&
                hierarchyProjector.TryGetChannelNode(value, out ConfigurationHierarchyNode? channelNode) &&
                channelNode is not null)
            {
                bool preserveCollapsedBranch = channelNode.Zone is not null &&
                    hierarchyProjector.TryGetZoneNode(channelNode.Zone, out ConfigurationHierarchyNode? collapsedZoneNode) &&
                    collapsedZoneNode is not null &&
                    !collapsedZoneNode.IsExpanded &&
                    ReferenceEquals(selectedHierarchyNode, collapsedZoneNode);
                if (!preserveCollapsedBranch && !ReferenceEquals(selectedHierarchyNode, channelNode))
                {
                    selectedHierarchyNode = channelNode;
                    OnPropertyChanged(nameof(SelectedHierarchyNode));
                }
                if (!preserveCollapsedBranch && channelNode.Zone is not null &&
                    hierarchyProjector.TryGetZoneNode(channelNode.Zone, out ConfigurationHierarchyNode? zoneNode) &&
                    zoneNode is not null)
                {
                    zoneNode.IsExpanded = true;
                    ConfigurationHierarchyNode? systemNode = hierarchyProjector.FindSystemNode(zoneNode);
                    if (systemNode is not null)
                        systemNode.IsExpanded = true;
                }
            }
        }
    }
    public ConfigurationChannelRow? SelectedChannelRow
    {
        get => selectedChannelRow;
        set
        {
            if (!SetField(ref selectedChannelRow, value))
                return;
            SelectedChannel = value?.Channel;
        }
    }
    public ConfigurationStreamRow? SelectedStream
    {
        get => selectedStream;
        set => SetField(ref selectedStream, value);
    }
    public GroupConfiguration? SelectedGroup
    {
        get => selectedGroup;
        set => SetField(ref selectedGroup, value);
    }
    public KeyEntry? SelectedKey
    {
        get => selectedKey;
        set
        {
            if (!SetField(ref selectedKey, value))
                return;
            OnPropertyChanged(nameof(SelectedKeyProtocol));
            OnPropertyChanged(nameof(SelectedKeySystem));
            RefreshKeyEditorState();
        }
    }
    public ConfigurationAliasRow? SelectedAlias
    {
        get => selectedAlias;
        set
        {
            if (!SetField(ref selectedAlias, value))
                return;
            OnPropertyChanged(nameof(CanEditSelectedAlias));
            if (value is null)
                return;
            SystemConfiguration? owner = Systems.FirstOrDefault(system =>
                string.Equals(
                    FindAliasTableIdentifier(system.AliasPath),
                    value.Identifier,
                    StringComparison.OrdinalIgnoreCase));
            if (owner is not null && !ReferenceEquals(selectedAliasSystem, owner))
            {
                selectedAliasSystem = owner;
                OnPropertyChanged(nameof(SelectedAliasSystem));
            }
        }
    }

    public SystemConfiguration? SelectedAliasSystem
    {
        get => selectedAliasSystem;
        set
        {
            if (!SetField(ref selectedAliasSystem, value))
                return;
            string? identifier = value is null ? null : FindAliasTableIdentifier(value.AliasPath);
            SelectedAlias = identifier is null
                ? null
                : Aliases.FirstOrDefault(row => string.Equals(
                    row.Identifier,
                    identifier,
                    StringComparison.OrdinalIgnoreCase));
        }
    }

    public ConfigurationHierarchyNode? SelectedHierarchyNode
    {
        get => selectedHierarchyNode;
        set
        {
            if (!SetField(ref selectedHierarchyNode, value) || value is null)
                return;
            if (value.Channel is not null && value.Zone is not null)
            {
                SelectedZone = value.Zone;
                SelectedChannel = value.Channel;
                SelectSection(ConfigurationStudioSection.Zones);
            }
            else if (value.Zone is not null)
            {
                SelectedZone = value.Zone;
                SelectedChannel = null;
                SelectSection(ConfigurationStudioSection.Zones);
            }
            else if (value.System is not null)
            {
                SelectedSystem = value.System;
                SelectSection(ConfigurationStudioSection.Systems);
            }
        }
    }

    IConfigurationHierarchyNode? IConfigurationStudioNavigationViewModel.SelectedHierarchyNode
    {
        get => SelectedHierarchyNode;
        set => SelectedHierarchyNode = value as ConfigurationHierarchyNode;
    }

    public void SelectSection(ConfigurationStudioSection section)
        => SelectedNavigation = navigationState.Find(section);

    public void CommitFieldEdit()
        => editTransactions.CommitField();

    bool IConfigurationStudioFieldEditSession.CanEditFields => CanEdit;
    bool IConfigurationStudioFieldEditSession.IsCollectionRefreshInProgress
        => editTransactions.IsRefreshing;
    bool IConfigurationStudioFieldEditSession.IsRestoreBindingSettling
        => editTransactions.Draft.IsRestoreBindingSettling;
    ConfigurationStudioDraftSnapshot IConfigurationStudioFieldEditSession.CurrentDraft
        => editTransactions.Draft.Current;

    void IConfigurationStudioFieldEditSession.SynchronizeFieldState()
    {
        identities.Synchronize(Configuration);
        ApplySystemRenameReferences();
        if (!companions.ReferencesMatch(Configuration))
            LoadReferencedCompanions();
        SynchronizeDraftZoneSystems();
        SynchronizeDraftCallPrioritySystems();
        previewState.Synchronize(Configuration.Zones);
    }

    void IConfigurationStudioFieldEditSession.CommitFieldTransition(
        ConfigurationStudioDraftSnapshot before)
        => CompleteDraftTransition(before, markDocumentDirty: true);

    void IConfigurationStudioFieldEditSession.RefreshFieldCollections()
        => RefreshCollections(preserveSelection: true);

    void IConfigurationStudioFieldEditSession.NotifySelectedChannelChanged()
    {
        // Channel rows and the inspector edit the same mutable object. Re-announce
        // the selection after rebuilding projections even when its reference did
        // not change.
        OnPropertyChanged(nameof(SelectedChannel));
    }

    ConsoleConfiguration IConfigurationStudioEntityEditSession.Configuration => Configuration;
    bool IConfigurationStudioEntityEditSession.CanEditEntities => CanEdit;
    SystemConfiguration? IConfigurationStudioEntityEditSession.SelectedSystemForEdit => SelectedSystem;
    ZoneConfiguration? IConfigurationStudioEntityEditSession.SelectedZoneForEdit => SelectedZone;
    ChannelConfiguration? IConfigurationStudioEntityEditSession.SelectedChannelForEdit => SelectedChannel;
    ConfigurationStreamRow? IConfigurationStudioEntityEditSession.SelectedStreamForEdit => SelectedStream;
    GroupConfiguration? IConfigurationStudioEntityEditSession.SelectedGroupForEdit => SelectedGroup;

    string IConfigurationStudioEntityEditSession.GetZoneSystemName(ZoneConfiguration zone)
        => GetDraftZoneSystemName(zone);

    void IConfigurationStudioEntityEditSession.SetZoneSystemName(
        ZoneConfiguration zone,
        string systemName)
        => draftZoneSystemNames[zone] = systemName;

    void IConfigurationStudioEntityEditSession.MutateEntities(Action mutation)
        => Mutate(mutation);

    void IConfigurationStudioEntityEditSession.SelectSystem(SystemConfiguration? system)
        => SelectedSystem = system;

    void IConfigurationStudioEntityEditSession.SelectZone(ZoneConfiguration? zone)
        => SelectedZone = zone;

    void IConfigurationStudioEntityEditSession.SelectChannel(ChannelConfiguration? channel)
        => SelectedChannel = channel;

    void IConfigurationStudioEntityEditSession.SelectStream(WebStreamConfiguration? stream)
        => SelectedStream = Streams.FirstOrDefault(row => ReferenceEquals(row.Stream, stream));

    void IConfigurationStudioEntityEditSession.SelectGroup(GroupConfiguration? group)
        => SelectedGroup = group;

    void IConfigurationStudioEntityEditSession.SelectSectionForEdit(ConfigurationStudioSection section)
        => SelectSection(section);

    ConsoleConfiguration IConfigurationStudioCompanionCommandSession.Configuration => Configuration;
    bool IConfigurationStudioCompanionCommandSession.CanEditCompanions => CanEdit;
    SystemConfiguration? IConfigurationStudioCompanionCommandSession.SelectedSystemForCompanion
        => SelectedSystem;
    SystemConfiguration? IConfigurationStudioCompanionCommandSession.SelectedAliasSystemForCompanion
        => SelectedAliasSystem;
    KeyEntry? IConfigurationStudioCompanionCommandSession.SelectedKeyForCompanion => SelectedKey;
    ConfigurationAliasRow? IConfigurationStudioCompanionCommandSession.SelectedAliasForCompanion
        => SelectedAlias;
    int IConfigurationStudioCompanionCommandSession.KeyCount => KeyEntries.Count;
    ConfigurationStudioDraftSnapshot IConfigurationStudioCompanionCommandSession.CurrentDraft
        => editTransactions.Draft.Current;

    void IConfigurationStudioCompanionCommandSession.EnsureKeyFile()
    {
        if (HasKeyFile)
            return;
        companions.EnsureKeyFile(Configuration);
        Replace(keyEntries, companions.Keys.Keys);
    }

    void IConfigurationStudioCompanionCommandSession.AddKey(KeyEntry key)
    {
        companions.AddKey(key);
        keyEntries.Add(key);
    }

    void IConfigurationStudioCompanionCommandSession.RemoveKey(KeyEntry key)
    {
        companions.RemoveKey(key);
        keyEntries.Remove(key);
    }

    (string Identifier, RadioAlias Alias) IConfigurationStudioCompanionCommandSession.AddAlias(
        SystemConfiguration system)
        => companions.AddAlias(Configuration, system);

    bool IConfigurationStudioCompanionCommandSession.RemoveAlias(ConfigurationAliasRow row)
        => companions.RemoveAlias(row);

    void IConfigurationStudioCompanionCommandSession.CompleteKeyAddition(
        ConfigurationStudioDraftSnapshot before,
        KeyEntry key)
    {
        SelectedKey = key;
        RefreshKeyEditorState();
        CompleteDraftTransition(before, markDocumentDirty: true);
        RefreshValidation();
        NotifyDocumentState();
        OnPropertyChanged(nameof(Configuration));
        OnPropertyChanged(nameof(KeyFileReferenceText));
        OnPropertyChanged(nameof(KeyFileIdentifierText));
        OnPropertyChanged(nameof(HasKeyFile));
        OnPropertyChanged(nameof(CanEditKeyFile));
        OnPropertyChanged(nameof(IsKeyFileDirty));
    }

    void IConfigurationStudioCompanionCommandSession.CompleteKeyRemoval()
    {
        SelectedKey = KeyEntries.FirstOrDefault();
        CommitKeyEdit();
    }

    void IConfigurationStudioCompanionCommandSession.CompleteAliasAddition(
        ConfigurationStudioDraftSnapshot before,
        string identifier,
        RadioAlias alias)
    {
        CompleteDraftTransition(before, markDocumentDirty: true);
        RebuildAliasRows(identifier);
        SelectedAlias = Aliases.First(row => ReferenceEquals(row.Alias, alias));
        RefreshValidation();
        NotifyDocumentState();
        OnPropertyChanged(nameof(Configuration));
        OnPropertyChanged(nameof(AliasFilesDirty));
    }

    void IConfigurationStudioCompanionCommandSession.CompleteAliasRemoval(ConfigurationAliasRow row)
    {
        aliases.Remove(row);
        SelectedAlias = Aliases.FirstOrDefault();
        CommitAliasEdit();
    }

    /// <summary>
    /// Commits the currently bound editor state before a document-level action.
    /// Text bindings update their sources as the operator types, so saving,
    /// exporting, navigating, or closing does not depend on a particular view
    /// receiving a LostFocus event first.
    /// </summary>
    public void CommitPendingEdits()
        => editTransactions.CommitPending();

    public string AttachKeyFile(string suggestedName, string content)
    {
        if (!CanEdit)
            throw new InvalidOperationException("This configuration is read-only.");
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedName);
        ArgumentNullException.ThrowIfNull(content);

        ConfigurationStudioDraftSnapshot before = editTransactions.Draft.Current;
        string reference = companions.AttachKeyFile(Configuration, suggestedName, content);
        Replace(keyEntries, companions.Keys.Keys);
        SelectedKey = KeyEntries.FirstOrDefault();
        CompleteDraftTransition(before, markDocumentDirty: true);
        RefreshValidation();
        NotifyDocumentState();
        OnPropertyChanged(nameof(Configuration));
        OnPropertyChanged(nameof(KeyFileReferenceText));
        OnPropertyChanged(nameof(KeyFileIdentifierText));
        OnPropertyChanged(nameof(HasKeyFile));
        OnPropertyChanged(nameof(CanEditKeyFile));
        OnPropertyChanged(nameof(IsKeyFileDirty));
        return reference;
    }

    public string AttachAliasFile(
        SystemConfiguration system,
        string suggestedName,
        string content)
    {
        if (!CanEdit)
            throw new InvalidOperationException("This configuration is read-only.");
        ArgumentNullException.ThrowIfNull(system);
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedName);
        ArgumentNullException.ThrowIfNull(content);
        if (!Configuration.Systems.Contains(system))
            throw new ArgumentException("The selected system is not part of this configuration.", nameof(system));

        ConfigurationStudioDraftSnapshot before = editTransactions.Draft.Current;
        string reference = companions.AttachAliasFile(Configuration, system, suggestedName, content);
        RebuildAliasRows(reference);
        CompleteDraftTransition(before, markDocumentDirty: true);
        RefreshCollections(preserveSelection: true);
        RefreshValidation();
        NotifyDocumentState();
        OnPropertyChanged(nameof(Configuration));
        OnPropertyChanged(nameof(SelectedSystem));
        OnPropertyChanged(nameof(AliasFilesDirty));
        return reference;
    }

    public void CommitZoneSystemEdit()
    {
        CommitFieldEdit();
        RefreshConfigurationHierarchy();
    }

    public void CommitKeyEdit()
    {
        if (!CanEdit || editTransactions.Draft.IsRestoreBindingSettling)
            return;
        ConfigurationStudioDraftSnapshot before = editTransactions.Draft.Current;
        KeyEntry? selection = SelectedKey;
        if (selection is not null)
            companions.MarkKeyContentChanged();
        if (selection is not null)
        {
            int index = KeyEntries.IndexOf(selection);
            if (index >= 0)
                keyEntries[index] = selection;
        }
        SelectedKey = selection;
        if (!selectedKeyIdInputInvalid)
            RefreshKeyEditorState();
        CompleteDraftTransition(before);
        RefreshValidation();
        NotifyDocumentState();
    }

    public void CommitChannelModeEdit()
    {
        if (SelectedChannel is null || editTransactions.Draft.IsRestoreBindingSettling)
            return;
        RefreshChannelAlgorithmOptions(normalizeUnsupported: true);
        RefreshChannelKeyIdText();
        OnPropertyChanged(nameof(IsSelectedChannelDmr));
        CommitFieldEdit();
    }

    public void CommitChannelAlgorithmEdit()
    {
        if (editTransactions.Draft.IsRestoreBindingSettling)
            return;
        RefreshChannelAlgorithmOptions(normalizeUnsupported: false);
        RefreshChannelKeyIdText();
        CommitFieldEdit();
    }

    public void CommitKeyProtocolEdit()
    {
        if (editTransactions.Draft.IsRestoreBindingSettling)
            return;
        RefreshKeyAlgorithmOptions(normalizeUnsupported: true);
        CommitKeyEdit();
    }

    public void OpenValidationDrawer() => IsValidationDrawerOpen = HasValidationIssues;

    public void NavigateToIssue(ConfigurationValidationIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        IsValidationDrawerOpen = true;
        int? firstIndex = ParseIndexedPath(issue.Path, issue.Domain == "Encryption Keys" ? "keys" :
            issue.Domain is "Channels" or "Zones" or "Web Streams" ? "zones" :
            issue.Domain == "Systems" ? "systems" :
            issue.Domain == "Groups" ? "groups" : string.Empty);

        switch (issue.Domain)
        {
            case "Systems":
                SelectSection(ConfigurationStudioSection.Systems);
                if (firstIndex is int systemIndex && systemIndex >= 0 && systemIndex < Systems.Count)
                    SelectedSystem = Systems[systemIndex];
                break;
            case "Zones":
            case "Channels":
                SelectSection(ConfigurationStudioSection.Zones);
                if (firstIndex is int zoneIndex && zoneIndex >= 0 && zoneIndex < Zones.Count)
                {
                    SelectedZone = Zones[zoneIndex];
                    int? channelIndex = ParseIndexedPath(issue.Path, "channels");
                    if (channelIndex is int index && index >= 0 && index < Channels.Count)
                        SelectedChannel = Channels[index];
                }
                break;
            case "Web Streams":
                SelectSection(ConfigurationStudioSection.Streams);
                if (firstIndex is int streamZoneIndex &&
                    streamZoneIndex >= 0 && streamZoneIndex < Zones.Count)
                {
                    ZoneConfiguration streamZone = Zones[streamZoneIndex];
                    int? streamIndex = ParseIndexedPath(issue.Path, "web_streams");
                    if (streamIndex is int index &&
                        index >= 0 && index < streamZone.WebStreams.Count)
                    {
                        WebStreamConfiguration stream = streamZone.WebStreams[index];
                        SelectedStream = Streams.FirstOrDefault(row =>
                            ReferenceEquals(row.Zone, streamZone) &&
                            ReferenceEquals(row.Stream, stream));
                    }
                }
                break;
            case "Groups":
                SelectSection(ConfigurationStudioSection.Groups);
                if (firstIndex is int groupIndex && groupIndex >= 0 && groupIndex < Groups.Count)
                    SelectedGroup = Groups[groupIndex];
                break;
            case "Encryption Keys":
                SelectSection(ConfigurationStudioSection.EncryptionKeys);
                if (firstIndex is int keyIndex && keyIndex >= 0 && keyIndex < KeyEntries.Count)
                    SelectedKey = KeyEntries[keyIndex];
                break;
            default:
                SelectSection(ConfigurationStudioSection.Files);
                break;
        }
    }

    public void CommitAliasEdit()
    {
        if (!CanEdit || editTransactions.Draft.IsRestoreBindingSettling)
            return;
        if (SelectedAlias is { } alias)
            companions.MarkAliasContentChanged(alias.Identifier);
        CompleteDraftTransition(editTransactions.Draft.Current);
        RefreshValidation();
        NotifyDocumentState();
    }

    public void Undo() => editTransactions.Undo();
    public void Redo() => editTransactions.Redo();

    internal int BeginHistoryRestoreBindingSettlement()
        => editTransactions.BeginBindingSettlement();

    internal void CompleteHistoryRestoreBindingSettlement(int version)
        => editTransactions.CompleteBindingSettlement(version);

    public void AddSystem()
        => entityEdits.AddSystem();

    public void DuplicateSystem()
        => entityEdits.DuplicateSystem();

    public void DeleteSystem()
        => entityEdits.DeleteSystem();

    public void DeleteSystem(SystemConfiguration system)
        => entityEdits.DeleteSystem(system);

    public void AddZone()
        => entityEdits.AddZone();

    public void DuplicateZone()
        => entityEdits.DuplicateZone();

    public void DeleteZone()
        => entityEdits.DeleteZone();

    public void AddChannel()
        => entityEdits.AddChannel();

    public void AddChannelToSelectedSystem()
        => entityEdits.AddChannelToSelectedSystem();

    public void DuplicateChannel()
        => entityEdits.DuplicateChannel();

    public void DeleteChannel()
        => entityEdits.DeleteChannel();

    public void MoveChannel(int offset)
        => entityEdits.MoveChannel(offset);

    public void SetChannelsRxOnly(IEnumerable<ChannelConfiguration> channels, bool rxOnly)
        => entityEdits.SetChannelsRxOnly(channels, rxOnly);

    public void ApplySelectedCardSize(IEnumerable<ChannelConfiguration> channels)
        => entityEdits.ApplySelectedCardSize(channels);

    public void AddStream()
        => entityEdits.AddStream();

    public void DeleteStream()
        => entityEdits.DeleteStream();

    public void MoveSelectedStreamTo(ZoneConfiguration zone)
        => entityEdits.MoveSelectedStreamTo(zone);

    public void AddGroup()
        => entityEdits.AddGroup();

    public void DeleteGroup()
        => entityEdits.DeleteGroup();

    public void AddKey()
        => companionCommands.AddKey();

    public void DeleteKey()
        => companionCommands.DeleteKey();

    public void AddAlias()
        => companionCommands.AddAlias();

    public void DeleteAlias()
        => companionCommands.DeleteAlias();

    public string? ApplyOperationalGroup(PatchGroupEditorViewModel group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return runtimeContext.ApplyOperationalGroups([group]);
    }

    public string? ApplyAllOperationalGroups()
        => CanUseOperationalGroups
            ? runtimeContext.ApplyOperationalGroups(OperationalGroups)
            : "Operator state can only be changed for the active codeplug.";

    public void SetOperationalGroupEnabled(PatchGroupEditorViewModel group)
    {
        ArgumentNullException.ThrowIfNull(group);
        runtimeContext.SetOperationalGroupEnabled(group);
    }

    internal ConfigurationStudioSaveState CaptureSaveState()
        => companions.CaptureSaveState(document.Serialize(), ValidationIssues.ToArray());

    internal IReadOnlyDictionary<string, string> CaptureExportCompanionContents()
        => companions.CaptureExportContents(Configuration);

    internal void ApplyOperatorStateForSave(
        UserSettings settings,
        string destinationIdentity,
        bool identityChanged)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationIdentity);
        if (identityChanged)
        {
            CodeplugGroupStateStore.CopyForSaveAs(settings, documentIdentity, destinationIdentity);
            CodeplugStudioStateStore.CopyForSaveAs(settings, documentIdentity, destinationIdentity);
        }
        ApplyIdentityMigrations(settings, destinationIdentity, destinationIdentity, identityChanged);
        foreach (KeyValuePair<ChannelConfiguration, WidgetPositionSetting> position in previewState.Positions)
        {
            settings.ChannelWidgetPositions[GetChannelSettingsKey(position.Key)] = new WidgetPositionSetting
            {
                X = position.Value.X,
                Y = position.Value.Y
            };
        }
        CodeplugStudioState destinationStudioState = CodeplugStudioStateStore.Get(settings, destinationIdentity);
        destinationStudioState.ZoneSystemAssignments = draftZoneSystemNames
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Value))
            .ToDictionary(
                entry => entry.Key.Name,
                entry => entry.Value,
                StringComparer.OrdinalIgnoreCase);
        destinationStudioState.CallPrioritySystemNames = draftCallPrioritySystemIds
            .Select(identities.FindSystem)
            .Where(system => system is not null)
            .Select(system => system!.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void AcceptSaved(
        string hostDocumentIdentity,
        ConfigurationId savedConfigurationId,
        ConfigurationSavePlan plan)
    {
        string codeplugText = plan.Files.First(file => file.Category == "Codeplug").Content;
        document = companionSource.AcceptSaved(document, hostDocumentIdentity, codeplugText);
        documentIdentity = hostDocumentIdentity;
        configurationId = savedConfigurationId;
        LoadReferencedCompanions();
        previewState.AcceptSaved();
        migrationPlanner.ResetBaseline(Configuration);
        ResetSystemRenameTargets();
        editTransactions.Draft.AcceptSaved(CaptureDraftSnapshot());
        NotifyDocumentState();
    }

    internal string BuildIdentityMigrationReviewText() => BuildIdentityMigrationSummary();

    public void MovePreviewChannel(IConfigurationChannelPreviewViewModel preview, double x, double y)
    {
        previewState.Move(preview, x, y);
        OnPropertyChanged(nameof(PreviewCanvasWidth));
        OnPropertyChanged(nameof(PreviewCanvasHeight));
    }

    public void BeginPreviewMove()
    {
        editTransactions.Draft.BeginPreviewMove();
    }

    public void CommitPreviewMove()
    {
        if (!editTransactions.Draft.TryCompletePreviewMove(out ConfigurationStudioDraftSnapshot? before))
            return;
        CompleteDraftTransition(before!);
        NotifyDocumentState();
    }

    private void ApplyIdentityMigrations(
        UserSettings settings,
        string statePath,
        string destinationPath,
        bool pathChanged)
    {
        Dictionary<string, string> systemRenames = migrationPlanner.BuildSystemRenames();
        IReadOnlyList<string> deletedSystems = migrationPlanner.BuildDeletedSystems();

        foreach (KeyValuePair<string, string> rename in systemRenames)
        {
            MoveDictionaryEntry(settings.RxJitterBuffersBySystem, rename.Key, rename.Value);
            if (string.Equals(settings.LastSelectedSystemName, rename.Key, StringComparison.OrdinalIgnoreCase))
                settings.LastSelectedSystemName = rename.Value;
        }
        foreach (string deleted in deletedSystems)
        {
            settings.RxJitterBuffersBySystem.Remove(deleted);
            if (string.Equals(settings.LastSelectedSystemName, deleted, StringComparison.OrdinalIgnoreCase))
                settings.LastSelectedSystemName = null;
        }

        CodeplugGroupState state = CodeplugGroupStateStore.GetOrMigrate(settings, statePath);
        foreach (List<PatchMemberSetting> members in state.Memberships.Values)
        {
            members.RemoveAll(member => deletedSystems.Contains(member.SystemName, StringComparer.OrdinalIgnoreCase));
            foreach (PatchMemberSetting member in members)
            {
                if (systemRenames.TryGetValue(member.SystemName, out string? renamed))
                    member.SystemName = renamed;
            }
        }

        (Dictionary<string, string> groupRenames, IReadOnlyList<string> deletedGroups) = migrationPlanner.BuildGroupMigrations();
        foreach (KeyValuePair<string, string> rename in groupRenames)
        {
            MoveDictionaryEntry(state.Memberships, rename.Key, rename.Value);
            MoveDictionaryEntry(state.OneWayModes, rename.Key, rename.Value);
            MoveDictionaryEntry(state.EnabledStates, rename.Key, rename.Value);
        }

        foreach (string deleted in deletedGroups)
        {
            state.Memberships.Remove(deleted);
            state.OneWayModes.Remove(deleted);
            state.EnabledStates.Remove(deleted);
        }

        foreach (ChannelIdentityMigration migration in migrationPlanner.BuildChannelMigrations())
        {
            string original = migration.OriginalSettingsKey;
            string? current = migration.CurrentSettingsKey;
            if (current is not null)
            {
                MoveDictionaryEntry(settings.ChannelWidgetPositions, original, current);
                MoveDictionaryEntry(settings.ChannelVolumes, original, current);
                MoveDictionaryEntry(settings.ChannelStereoBalances, original, current);
                MoveDictionaryEntry(settings.ChannelOutputDeviceIds, original, current);
                MoveDictionaryEntry(settings.RecordingIgnoredSubscriberIds, original, current);
                MoveDictionaryEntry(settings.TransmitEncryptionStates, original, current);
                MoveListEntry(settings.ReceiveEnabledChannelKeys, original, current);
                MoveListEntry(settings.TransmitSelectedChannelKeys, original, current);
                MoveListEntry(settings.RecordingEnabledChannelKeys, original, current);
                if (string.Equals(settings.LastSelectedChannelKey, original, StringComparison.OrdinalIgnoreCase))
                    settings.LastSelectedChannelKey = current;
            }
            else
            {
                settings.ChannelWidgetPositions.Remove(original);
                settings.ChannelVolumes.Remove(original);
                settings.ChannelStereoBalances.Remove(original);
                settings.ChannelOutputDeviceIds.Remove(original);
                settings.RecordingIgnoredSubscriberIds.Remove(original);
                settings.TransmitEncryptionStates.Remove(original);
                RemoveListEntry(settings.ReceiveEnabledChannelKeys, original);
                RemoveListEntry(settings.TransmitSelectedChannelKeys, original);
                RemoveListEntry(settings.RecordingEnabledChannelKeys, original);
                if (string.Equals(settings.LastSelectedChannelKey, original, StringComparison.OrdinalIgnoreCase))
                    settings.LastSelectedChannelKey = null;
            }
        }

        foreach (List<PatchMemberSetting> members in state.Memberships.Values)
            foreach (PatchMemberSetting member in members)
            {
                string originalMemberSystem = systemRenames.FirstOrDefault(rename =>
                    string.Equals(rename.Value, member.SystemName, StringComparison.OrdinalIgnoreCase)).Key ?? member.SystemName;
                OriginalChannelIdentity? originalChannel = migrationPlanner.OriginalChannels.FirstOrDefault(channel =>
                    string.Equals(channel.System, originalMemberSystem, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(channel.Name, member.ChannelName, StringComparison.OrdinalIgnoreCase) &&
                    uint.TryParse(channel.DestinationId, out uint destinationId) && destinationId == member.DestinationId);
                if (originalChannel is null)
                    continue;
                ChannelConfiguration? currentChannel = migrationPlanner.FindCurrentChannel(originalChannel);
                if (currentChannel is not null)
                    member.ChannelName = currentChannel.Name;
            }

        foreach (StreamIdentityMigration migration in migrationPlanner.BuildStreamMigrations(pathChanged))
        {
            OriginalStreamIdentity original = migration.Original;
            WebStreamConfiguration? current = migration.Current;
            string originalWidgetKey = WidgetPositionKey.ForWebStream(original.Name);
            if (current is null)
            {
                settings.WebStreamOutputDeviceIds.Remove(original.Name);
                settings.WebStreamVolumes.Remove(original.Name);
                settings.ChannelWidgetPositions.Remove(originalWidgetKey);
                continue;
            }
            if (!string.Equals(original.Name, current.Name, StringComparison.OrdinalIgnoreCase))
            {
                MoveDictionaryEntry(settings.WebStreamOutputDeviceIds, original.Name, current.Name);
                MoveDictionaryEntry(settings.WebStreamVolumes, original.Name, current.Name);
                MoveDictionaryEntry(
                    settings.ChannelWidgetPositions,
                    originalWidgetKey,
                    WidgetPositionKey.ForWebStream(current.Name));
            }

            string oldIdentity = companionSource.CreateWebStreamAuthorizationIdentity(
                documentIdentity,
                original.Configuration);
            string newIdentity = companionSource.CreateWebStreamAuthorizationIdentity(
                destinationPath,
                current);
            if (oldIdentity.Length > 0 && newIdentity.Length > 0)
                MoveListEntry(settings.SelectedWebStreams, oldIdentity, newIdentity, StringComparison.Ordinal);
        }
    }

    private void ApplySystemRenameReferences()
    {
        foreach (SystemConfiguration system in Configuration.Systems)
        {
            Guid systemId = identities.GetSystemId(system);
            string current = system.Name;
            string previous = lastSystemRenameTargets.GetValueOrDefault(systemId, current);
            if (string.Equals(previous, current, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (ChannelConfiguration channel in Configuration.Zones.SelectMany(zone => zone.Channels))
            {
                if (string.Equals(channel.System, previous, StringComparison.OrdinalIgnoreCase))
                    channel.System = current;
            }
            companions.ApplySystemRename(previous, current);
            foreach (ZoneConfiguration zone in draftZoneSystemNames
                         .Where(entry => string.Equals(entry.Value, previous, StringComparison.OrdinalIgnoreCase))
                         .Select(entry => entry.Key)
                         .ToArray())
            {
                draftZoneSystemNames[zone] = current;
            }
            lastSystemRenameTargets[systemId] = current;
        }
        SynchronizeDraftZoneSystems();
    }

    private string BuildIdentityMigrationSummary()
    {
        Dictionary<string, string> systems = migrationPlanner.BuildSystemRenames();
        IReadOnlyList<string> deletedSystems = migrationPlanner.BuildDeletedSystems();
        (Dictionary<string, string> groups, IReadOnlyList<string> deletedGroups) = migrationPlanner.BuildGroupMigrations();
        IReadOnlyList<ChannelIdentityMigration> channels = migrationPlanner.BuildChannelMigrations();
        IReadOnlyList<StreamIdentityMigration> streams = migrationPlanner.BuildStreamMigrations();
        var lines = new List<string>();
        lines.AddRange(systems.Select(rename => $"• System state: {rename.Key} → {rename.Value}"));
        lines.AddRange(deletedSystems.Select(name => $"• Remove system state: {name}"));
        lines.AddRange(groups.Select(rename => $"• Group state: {rename.Key} → {rename.Value}"));
        lines.AddRange(deletedGroups.Select(name => $"• Remove group state: {name}"));
        lines.AddRange(channels.Select(change => change.Current is null
            ? $"• Remove channel state: {change.OriginalSettingsKey.Replace('\u001F', '/')}"
            : $"• Channel state: {change.OriginalSettingsKey.Replace('\u001F', '/')} → {change.CurrentSettingsKey!.Replace('\u001F', '/')}"));
        lines.AddRange(streams.Select(change => change.Current is null
            ? $"• Remove stream state: {change.Original.Name}"
            : $"• Stream state: {change.Original.Name} → {change.Current.Name}"));
        return lines.Count == 0 ? string.Empty : "\n\nOperator-state migrations:\n" + string.Join("\n", lines);
    }

    private static void MoveDictionaryEntry<T>(Dictionary<string, T> dictionary, string original, string current)
    {
        if (dictionary.Remove(original, out T? value))
            dictionary[current] = value;
    }

    private static void MoveListEntry(
        List<string> list,
        string original,
        string current,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        int index = list.FindIndex(value => string.Equals(value, original, comparison));
        if (index >= 0)
            list[index] = current;
    }

    private static void RemoveListEntry(List<string> list, string value)
        => list.RemoveAll(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));

    private void Mutate(Action action)
    {
        if (!CanEdit)
            return;
        ConfigurationStudioDraftSnapshot before = editTransactions.Draft.Current;
        action();
        identities.Synchronize(Configuration);
        SynchronizeSystemRenameTargets();
        SynchronizeDraftCallPrioritySystems();
        SynchronizeDraftZoneSystems();
        previewState.Synchronize(Configuration.Zones);
        CompleteDraftTransition(before, markDocumentDirty: true);
        RefreshCollections(preserveSelection: true);
    }

    private void RefreshChannelEditorState()
    {
        RefreshChannelAlgorithmOptions(normalizeUnsupported: false);
        RefreshChannelKeyIdText();
    }

    private void RefreshChannelAlgorithmOptions(bool normalizeUnsupported)
    {
        IReadOnlyList<EncryptionAlgorithmOption> nextAlgorithms =
            EncryptionAlgorithmCatalog.ForChannelMode(SelectedChannel?.Mode);
        EncryptionAlgorithmOption? option = EncryptionAlgorithmCatalog.FindChannelOption(
            SelectedChannel?.Mode,
            SelectedChannel?.Algo);
        if (option is null && normalizeUnsupported)
        {
            option = nextAlgorithms.Count > 0 ? nextAlgorithms[0] : null;
            if (SelectedChannel is not null && option is not null)
                SelectedChannel.Algo = option.ConfigurationValue;
        }
        bool algorithmsChanged = !availableChannelAlgorithms.SequenceEqual(nextAlgorithms);
        availableChannelAlgorithms = nextAlgorithms;
        selectedChannelAlgorithm = option;
        if (algorithmsChanged)
            OnPropertyChanged(nameof(AvailableChannelAlgorithms));
        OnPropertyChanged(nameof(SelectedChannelAlgorithm));
        OnPropertyChanged(nameof(ChannelEncryptionUsesKey));
    }

    private void RefreshChannelKeyIdText()
    {
        selectedChannelKeyIdHexDigits = EncryptionAlgorithmCatalog.FormatChannelKeyIdDigits(
            SelectedChannel?.Mode,
            SelectedChannel?.KeyId);
        OnPropertyChanged(nameof(SelectedChannelKeyIdHexDigits));
    }

    private void RefreshKeyEditorState()
    {
        RefreshKeyAlgorithmOptions(normalizeUnsupported: false);
        selectedKeyIdInputInvalid = false;
        selectedKeyIdHexDigits = ConfigurationStudioKeyEditor
            .BuildState(SelectedKey, normalizeUnsupported: false)
            .KeyIdHexDigits;
        OnPropertyChanged(nameof(SelectedKeyIdHexDigits));
    }

    private void RefreshKeyAlgorithmOptions(bool normalizeUnsupported)
    {
        ConfigurationStudioKeyEditorState state =
            ConfigurationStudioKeyEditor.BuildState(SelectedKey, normalizeUnsupported);
        IReadOnlyList<EncryptionAlgorithmOption> nextAlgorithms = state.Algorithms;
        EncryptionAlgorithmOption? option = state.SelectedAlgorithm;
        bool algorithmsChanged = !availableKeyAlgorithms.SequenceEqual(nextAlgorithms);
        availableKeyAlgorithms = nextAlgorithms;
        selectedKeyAlgorithm = option;
        if (algorithmsChanged)
            OnPropertyChanged(nameof(AvailableKeyAlgorithms));
        OnPropertyChanged(nameof(SelectedKeyAlgorithm));
        OnPropertyChanged(nameof(SelectedKeyAlgorithmIdText));
    }

    private void RefreshCollections(bool preserveSelection = false)
        => editTransactions.Refresh(preserveSelection);

    ConfigurationStudioSelectionNames IConfigurationStudioEditTransactionPort.CaptureSelectionNames()
        => new(SelectedSystem?.Name, SelectedZone?.Name, SelectedChannel?.Name,
            SelectedStream?.Stream.Name, SelectedGroup?.Name, SelectedAliasSystem?.Name);

    void IConfigurationStudioEditTransactionPort.RefreshEditorCollections(ConfigurationStudioSelectionNames? selection)
    {
        Replace(Systems, Configuration.Systems);
        Replace(Zones, Configuration.Zones);
        SynchronizeDraftCallPrioritySystems();
        SynchronizeDraftZoneSystems();
        Replace(Streams, Configuration.Zones.SelectMany(zone => zone.WebStreams.Select(stream => new ConfigurationStreamRow(zone, stream))));
        Replace(Groups, Configuration.Groups);
        SelectedSystem = Systems.FirstOrDefault(system => string.Equals(system.Name, selection?.System, StringComparison.OrdinalIgnoreCase)) ?? Systems.FirstOrDefault();
        SelectedZone = Zones.FirstOrDefault(zone => string.Equals(zone.Name, selection?.Zone, StringComparison.OrdinalIgnoreCase)) ?? Zones.FirstOrDefault();
        RefreshChannelsAndPreview();
        SelectedChannel = Channels.FirstOrDefault(channel => string.Equals(channel.Name, selection?.Channel, StringComparison.OrdinalIgnoreCase)) ?? Channels.FirstOrDefault();
        SelectedStream = Streams.FirstOrDefault(row => string.Equals(row.Stream.Name, selection?.Stream, StringComparison.OrdinalIgnoreCase)) ?? Streams.FirstOrDefault();
        SelectedGroup = Groups.FirstOrDefault(group => string.Equals(group.Name, selection?.Group, StringComparison.OrdinalIgnoreCase)) ?? Groups.FirstOrDefault();
        SelectedAliasSystem = Systems.FirstOrDefault(system =>
            string.Equals(system.Name, selection?.AliasSystem, StringComparison.OrdinalIgnoreCase)) ?? Systems.FirstOrDefault();
        RefreshConfigurationHierarchy();
        RefreshValidation();
        NotifyDocumentState();
    }

    private void RefreshChannelsAndPreview()
    {
        previewState.Synchronize(Configuration.Zones);
        Replace(Channels, SelectedZone?.Channels ?? []);
        RefreshVisibleChannelRows();
        if (SelectedZone is null)
        {
            PreviewChannels.Clear();
            return;
        }
        Replace(PreviewChannels, previewState.Project(SelectedZone));
        // Row projection already preserves the selection or chooses a valid
        // fallback. Update recreated previews without briefly selecting row one.
        foreach (IConfigurationChannelPreviewViewModel preview in PreviewChannels)
            preview.IsSelected = ReferenceEquals(preview.Channel, SelectedChannel);
        OnPropertyChanged(nameof(LayoutChanged));
        OnPropertyChanged(nameof(SelectedZoneHeading));
        OnPropertyChanged(nameof(PreviewCanvasWidth));
        OnPropertyChanged(nameof(PreviewCanvasHeight));
    }

    private void RefreshVisibleChannelRows()
    {
        ChannelConfiguration? selected = SelectedChannel;
        string query = channelSearchText.Trim();
        HashSet<ChannelConfiguration> currentChannels = Configuration.Zones
            .SelectMany(zone => zone.Channels)
            .ToHashSet();
        foreach (ChannelConfiguration removed in channelRows.Keys.Where(channel => !currentChannels.Contains(channel)).ToArray())
            channelRows.Remove(removed);
        ConfigurationChannelRow[] allRows = Channels.Select((channel, index) =>
        {
            if (!channelRows.TryGetValue(channel, out ConfigurationChannelRow? row))
            {
                row = new ConfigurationChannelRow(index + 1, channel, CanEdit);
                channelRows[channel] = row;
            }
            row.Refresh(index + 1);
            return row;
        }).ToArray();
        IEnumerable<ConfigurationChannelRow> rows = allRows;
        if (query.Length > 0)
        {
            rows = rows.Where(row =>
                row.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.System.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.DestinationText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.ModeText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.EncryptionText.Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        Replace(VisibleChannelRows, rows);
        SelectedChannelRow = VisibleChannelRows.FirstOrDefault(row => ReferenceEquals(row.Channel, selected))
            ?? VisibleChannelRows.FirstOrDefault();
    }

    private void RefreshValidation()
    {
        Replace(
            ValidationIssues,
            ConfigurationStudioValidationCollaborator.BuildIssues(
                document.Validate(),
                companions.Keys,
                Configuration.Systems,
                selectedKeyIdInputInvalid,
                SelectedKey,
                companions.KeyFileLoadError,
                companions.KeyFileLoadIsWarning,
                companions.AliasLoadErrors,
                companions.AliasLoadWarnings,
                companions.AliasTables));
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(HasValidationIssues));
        OnPropertyChanged(nameof(IssueSummary));
        OnPropertyChanged(nameof(ValidationStatusText));
        OnPropertyChanged(nameof(ValidationDrawerHeading));
        OnPropertyChanged(nameof(ValidationIndicatorBrush));
        if (!HasValidationIssues)
            IsValidationDrawerOpen = false;
    }

    private void LoadReferencedCompanions()
    {
        ConfigurationStudioCompanionSnapshot snapshot = companionSource.Load(document);
        companions.Load(snapshot, Configuration);
        Replace(keyEntries, companions.Keys.Keys);
        SelectedKey = KeyEntries.FirstOrDefault();
        OnPropertyChanged(nameof(KeyFileIdentifierText));
        OnPropertyChanged(nameof(HasKeyFile));
        OnPropertyChanged(nameof(CanEditKeyFile));

        Replace(aliases, companions.ProjectAliasRows());
        SelectedAlias = Aliases.FirstOrDefault();
        SelectedAliasSystem = SelectedAliasSystem is not null && Systems.Contains(SelectedAliasSystem)
            ? SelectedAliasSystem
            : Systems.FirstOrDefault();
        OnPropertyChanged(nameof(AliasFilesDirty));
    }

    private string? FindAliasTableIdentifier(string reference)
        => companions.FindAliasTableIdentifier(reference);

    private void RebuildAliasRows(string selectedIdentifier)
    {
        Replace(aliases, companions.ProjectAliasRows());
        SelectedAlias = Aliases.FirstOrDefault(row =>
            string.Equals(row.Identifier, selectedIdentifier, StringComparison.OrdinalIgnoreCase));
    }

    private void InitializeDraftZoneSystems(IReadOnlyDictionary<string, string> savedZoneSystemAssignments)
    {
        string fallback = Configuration.Systems.FirstOrDefault()?.Name ?? string.Empty;
        foreach (ZoneConfiguration zone in Configuration.Zones)
        {
            string[] systems = zone.Channels
                .Select(channel => channel.System?.Trim() ?? string.Empty)
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            draftZoneSystemNames[zone] = systems.Length switch
            {
                0 when savedZoneSystemAssignments.TryGetValue(zone.Name, out string? savedSystem) &&
                       Configuration.Systems.Any(system => string.Equals(system.Name, savedSystem, StringComparison.OrdinalIgnoreCase))
                    => savedSystem,
                0 => fallback,
                1 => systems[0],
                _ => string.Empty
            };
        }
    }

    private void InitializeDraftCallPrioritySystems(IReadOnlyCollection<string> savedSystemNames)
    {
        draftCallPrioritySystemIds.Clear();
        foreach (SystemConfiguration system in Configuration.Systems)
        {
            if (savedSystemNames.Contains(system.Name, StringComparer.OrdinalIgnoreCase))
                draftCallPrioritySystemIds.Add(identities.GetSystemId(system));
        }
    }

    private void SynchronizeDraftCallPrioritySystems()
        => draftCallPrioritySystemIds.RemoveWhere(id => identities.FindSystem(id) is null);

    private void SynchronizeDraftZoneSystems()
    {
        HashSet<ZoneConfiguration> currentZones = Configuration.Zones.ToHashSet();
        foreach (ZoneConfiguration removed in draftZoneSystemNames.Keys.Where(zone => !currentZones.Contains(zone)).ToArray())
            draftZoneSystemNames.Remove(removed);

        string fallback = Configuration.Systems.FirstOrDefault()?.Name ?? string.Empty;
        foreach (ZoneConfiguration zone in Configuration.Zones)
        {
            string[] systems = zone.Channels
                .Select(channel => channel.System?.Trim() ?? string.Empty)
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (systems.Length == 1)
                draftZoneSystemNames[zone] = systems[0];
            else if (systems.Length > 1)
                draftZoneSystemNames[zone] = string.Empty;
            else if (!draftZoneSystemNames.ContainsKey(zone))
                draftZoneSystemNames[zone] = fallback;
        }
    }

    private string GetDraftZoneSystemName(ZoneConfiguration zone)
        => draftZoneSystemNames.TryGetValue(zone, out string? systemName)
            ? systemName
            : Configuration.Systems.FirstOrDefault()?.Name ?? string.Empty;

    private void RefreshConfigurationHierarchy(string? query = null)
    {
        ConfigurationStudioHierarchyProjection projection = hierarchyProjector.Project(
            Configuration,
            GetDraftZoneSystemName,
            query ?? searchText,
            SelectedSystem,
            SelectedZone,
            SelectedChannel);
        Replace(ConfigurationHierarchy, projection.Roots);
        if (!ReferenceEquals(selectedHierarchyNode, projection.SelectedNode))
        {
            selectedHierarchyNode = projection.SelectedNode;
            OnPropertyChanged(nameof(SelectedHierarchyNode));
        }
    }

    private void HandleHierarchyNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConfigurationHierarchyNode.IsExpanded) ||
            sender is not ConfigurationHierarchyNode { IsExpanded: false } collapsedNode ||
            selectedHierarchyNode is not { } selectedNode ||
            ReferenceEquals(collapsedNode, selectedNode) ||
            !ContainsHierarchyNode(collapsedNode, selectedNode))
        {
            return;
        }

        // Tree controls keep a selected descendant visible by reopening its
        // parent. Select the collapsed branch itself so the user's choice wins.
        SelectedHierarchyNode = collapsedNode;
    }

    private static bool ContainsHierarchyNode(
        ConfigurationHierarchyNode ancestor,
        ConfigurationHierarchyNode candidate)
        => ancestor.Children.Any(child =>
            ReferenceEquals(child, candidate) || ContainsHierarchyNode(child, candidate));

    private ConfigurationStudioDraftSnapshot CaptureDraftSnapshot()
        => draftProjector.Capture(
            document,
            identities,
            companions,
            previewState,
            draftZoneSystemNames,
            draftCallPrioritySystemIds);

    private void CompleteDraftTransition(
        ConfigurationStudioDraftSnapshot before,
        bool markDocumentDirty = false)
        => editTransactions.RecordTransition(before, markDocumentDirty);

    bool IConfigurationStudioEditTransactionPort.IsReadOnly => document.IsReadOnly;
    ConfigurationStudioSection IConfigurationStudioEditTransactionPort.Section => navigationState.Current.Section;
    void IConfigurationStudioEditTransactionPort.CommitKeyEditor() => CommitKeyEdit();
    void IConfigurationStudioEditTransactionPort.CommitAliasEditor() => CommitAliasEdit();
    ConfigurationStudioDraftSnapshot IConfigurationStudioEditTransactionPort.CaptureDraft() => CaptureDraftSnapshot();
    void IConfigurationStudioEditTransactionPort.MarkDocumentDirty() => document.MarkDirty();

    ConfigurationStudioSelectionIds IConfigurationStudioEditTransactionPort.CaptureSelectionIds()
        => new(
            selectedSystem is null ? null : identities.GetSystemId(selectedSystem),
            selectedZone is null ? null : identities.GetZoneId(selectedZone),
            selectedChannel is null ? null : identities.GetChannelId(selectedChannel),
            selectedStream is null ? null : identities.GetStreamId(selectedStream.Stream),
            selectedGroup is null ? null : identities.GetGroupId(selectedGroup));

    void IConfigurationStudioEditTransactionPort.ApplyDraft(ConfigurationStudioDraftSnapshot snapshot)
    {
        document = companionSource.ParseDraft(snapshot.Yaml, document);
        identities.Restore(document.Configuration, snapshot.IdentityLayout);
        RestoreReferencedFiles(snapshot.ReferencedFiles);

        previewState.Restore(snapshot.WidgetPositions, identities.FindChannel);

        draftZoneSystemNames.Clear();
        foreach (KeyValuePair<Guid, string> entry in snapshot.ZoneSystemAssignments)
        {
            if (identities.FindZone(entry.Key) is { } zone)
                draftZoneSystemNames[zone] = entry.Value;
        }

        draftCallPrioritySystemIds.Clear();
        draftCallPrioritySystemIds.UnionWith(snapshot.CallPrioritySystemIds);

        ResetSystemRenameTargets();
    }

    void IConfigurationStudioEditTransactionPort.RestoreSelection(ConfigurationStudioSelectionIds selection)
    {
        SelectedSystem = selection.System is Guid systemId
            ? identities.FindSystem(systemId) ?? SelectedSystem
            : SelectedSystem;
        SelectedZone = selection.Zone is Guid zoneId
            ? identities.FindZone(zoneId) ?? SelectedZone
            : SelectedZone;
        SelectedChannel = selection.Channel is Guid channelId
            ? identities.FindChannel(channelId) ?? SelectedChannel
            : SelectedChannel;
        if (selection.Stream is Guid streamId && identities.FindStream(streamId) is { } stream)
            SelectedStream = Streams.FirstOrDefault(row => ReferenceEquals(row.Stream, stream));
        SelectedGroup = selection.Group is Guid groupId
            ? identities.FindGroup(groupId) ?? SelectedGroup
            : SelectedGroup;
    }

    void IConfigurationStudioEditTransactionPort.NotifyDraftRestored()
    {
        OnPropertyChanged(nameof(Document));
        OnPropertyChanged(nameof(Configuration));
        OnPropertyChanged(nameof(PatchSourceIdPassthrough));
        OnPropertyChanged(nameof(SelectedSystemHasCallPriority));
    }

    private void RestoreReferencedFiles(ConfigurationStudioReferencedFilesSnapshot snapshot)
    {
        companions.Restore(snapshot);
        Replace(keyEntries, companions.Keys.Keys);
        SelectedKey = KeyEntries.FirstOrDefault();

        Replace(aliases, companions.ProjectAliasRows());
        SelectedAlias = Aliases.FirstOrDefault();
    }

    private void ResetSystemRenameTargets()
    {
        lastSystemRenameTargets.Clear();
        foreach (SystemConfiguration system in Configuration.Systems)
            lastSystemRenameTargets[identities.GetSystemId(system)] = system.Name;
    }

    private void SynchronizeSystemRenameTargets()
    {
        HashSet<Guid> currentSystemIds = Configuration.Systems
            .Select(identities.GetSystemId)
            .ToHashSet();
        foreach (Guid removed in lastSystemRenameTargets.Keys
                     .Where(id => !currentSystemIds.Contains(id))
                     .ToArray())
        {
            lastSystemRenameTargets.Remove(removed);
        }
        foreach (SystemConfiguration system in Configuration.Systems)
            lastSystemRenameTargets.TryAdd(identities.GetSystemId(system), system.Name);
    }

    private static int? ParseIndexedPath(string path, string collectionName)
    {
        if (string.IsNullOrEmpty(collectionName))
            return null;
        string marker = collectionName + "[";
        int start = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;
        start += marker.Length;
        int end = path.IndexOf(']', start);
        return end > start && int.TryParse(path[start..end], NumberStyles.None, CultureInfo.InvariantCulture, out int index)
            ? index
            : null;
    }

    private void NotifySectionVisibility()
    {
        foreach (string name in new[]
                 {
                     nameof(IsOverview), nameof(IsSystems), nameof(IsZones), nameof(IsStreams),
                     nameof(IsGroups), nameof(IsEncryptionKeys), nameof(IsFiles),
                     nameof(ReviewSaveButtonText)
                 })
            OnPropertyChanged(name);
    }

    private void NotifyDocumentState()
    {
        foreach (string name in new[]
                 {
                     nameof(IsDirty), nameof(CanSaveDraft), nameof(StatusText), nameof(CanUndo), nameof(CanRedo),
                     nameof(ConfigurationShapeText), nameof(UnknownFieldsText), nameof(LayoutChanged),
                     nameof(ValidationStatusText), nameof(ValidationDrawerHeading), nameof(ValidationIndicatorBrush),
                     nameof(HasValidationIssues), nameof(HasWarnings), nameof(SelectedZoneHeading),
                     nameof(SystemNavigationHeading), nameof(ZoneNavigationHeading),
                     nameof(StreamNavigationHeading), nameof(GroupNavigationHeading),
                     nameof(KeyNavigationHeading), nameof(FileNavigationHeading),
                     nameof(PreviewCanvasWidth), nameof(PreviewCanvasHeight),
                     nameof(PatchSourceIdPassthrough)
                 })
            OnPropertyChanged(name);
    }

    private static string UniqueName(string baseName, IEnumerable<string> existing)
    {
        HashSet<string> names = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName))
            return baseName;
        for (int suffix = 2; ; suffix++)
        {
            string candidate = $"{baseName} {suffix}";
            if (!names.Contains(candidate))
                return candidate;
        }
    }

    internal static string? DisplayFileReference(string? value)
    {
        string reference = value?.Trim() ?? string.Empty;
        if (reference.Length == 0)
            return null;

        string normalized = reference.Replace('\\', '/').TrimEnd('/');
        bool isAbsolute = normalized[0] == '/' ||
            (normalized.Length >= 3 && char.IsLetter(normalized[0]) && normalized[1] == ':' && normalized[2] == '/');
        if (!isAbsolute)
            return normalized;

        int separator = normalized.LastIndexOf('/');
        return separator >= 0 ? normalized[(separator + 1)..] : normalized;
    }

    private static string GetChannelSettingsKey(ChannelConfiguration channel)
        => $"{channel.System}\u001F{channel.Name}";

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        T[] replacement = source.ToArray();
        if (target.Count == replacement.Length && target.SequenceEqual(replacement))
            return;
        target.Clear();
        foreach (T item in replacement)
            target.Add(item);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
