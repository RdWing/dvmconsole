using Avalonia.Media;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace DvmConsole.Desktop;

public enum ConfigurationStudioSection
{
    Overview,
    Systems,
    Zones,
    Streams,
    Groups,
    EncryptionKeys,
    Files
}

public sealed class ConfigurationStudioViewModel : INotifyPropertyChanged
{
    private static readonly IBrush ErrorIndicatorBrush = new SolidColorBrush(Color.Parse("#E5484D"));
    private static readonly IBrush WarningIndicatorBrush = new SolidColorBrush(Color.Parse("#F2B134"));
    private static readonly IBrush ValidIndicatorBrush = new SolidColorBrush(Color.Parse("#5AC878"));
    private readonly MainWindowViewModel runtimeViewModel;
    private readonly UserSettingsStore settingsStore;
    private readonly ConfigurationDraftIdentityRegistry identities = new();
    private readonly ConfigurationStudioDraftHistory history = new();
    private readonly ConfigurationIdentityMigrationPlanner migrationPlanner;
    private readonly Dictionary<string, WidgetPositionSetting> originalWidgetPositions;
    private readonly Dictionary<ChannelConfiguration, WidgetPositionSetting> draftWidgetPositions = [];
    private readonly Dictionary<ZoneConfiguration, string> draftZoneSystemNames = [];
    private readonly Dictionary<SystemConfiguration, ConfigurationHierarchyNode> systemHierarchyNodes = [];
    private readonly Dictionary<ZoneConfiguration, ConfigurationHierarchyNode> zoneHierarchyNodes = [];
    private readonly Dictionary<ChannelConfiguration, ConfigurationHierarchyNode> channelHierarchyNodes = [];
    private readonly Dictionary<ChannelConfiguration, ConfigurationChannelRow> channelRows = [];
    private readonly Dictionary<ChannelConfiguration, (string Signature, ConfigurationChannelPreviewViewModel Preview)> previewCache = [];
    private readonly ConfigurationHierarchyNode unassignedHierarchyNode = new("Unassigned or mixed", isExpanded: true);
    private ConfigurationDocument document;
    private ConfigurationStudioDraftSnapshot currentSnapshot;
    private ConfigurationStudioDraftSnapshot? previewMoveStartSnapshot;
    private string savedFingerprint = string.Empty;
    private ConfigurationStudioNavigationItem selectedNavigation;
    private SystemConfiguration? selectedSystem;
    private ZoneConfiguration? selectedZone;
    private ChannelConfiguration? selectedChannel;
    private ConfigurationStreamRow? selectedStream;
    private GroupConfiguration? selectedGroup;
    private KeyEntry? selectedKey;
    private KeyContainer keyContainer = new();
    private string? keyFilePath;
    private string? keyFileHash;
    private string keyFileSnapshot = string.Empty;
    private string? loadedKeyReference;
    private string searchText = string.Empty;
    private string channelSearchText = string.Empty;
    private string? keyFileLoadError;
    private bool keyFileLoadIsWarning;
    private readonly Dictionary<string, List<RadioAlias>> aliasTables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> aliasFileHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> aliasFileSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> aliasLoadErrors = [];
    private readonly List<string> aliasLoadWarnings = [];
    private string loadedAliasReference = string.Empty;
    private ConfigurationAliasRow? selectedAlias;
    private ConfigurationChannelRow? selectedChannelRow;
    private ConfigurationHierarchyNode? selectedHierarchyNode;
    private bool isZonePreviewExpanded = true;
    private bool isValidationDrawerOpen;
    private string selectedChannelKeyIdHexDigits = string.Empty;
    private string selectedKeyIdHexDigits = string.Empty;
    private EncryptionAlgorithmOption? selectedChannelAlgorithm;
    private EncryptionAlgorithmOption? selectedKeyAlgorithm;
    private IReadOnlyList<EncryptionAlgorithmOption> availableChannelAlgorithms = [];
    private IReadOnlyList<EncryptionAlgorithmOption> availableKeyAlgorithms = [];
    private readonly Dictionary<string, List<PatchMemberSetting>> stagedGroupMemberships = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> stagedGroupModes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, string> lastSystemRenameTargets = [];

    public ConfigurationStudioViewModel(
        ConfigurationDocument document,
        MainWindowViewModel runtimeViewModel,
        UserSettingsStore settingsStore,
        ConfigurationStudioSection initialSection)
    {
        this.document = document ?? throw new ArgumentNullException(nameof(document));
        this.runtimeViewModel = runtimeViewModel ?? throw new ArgumentNullException(nameof(runtimeViewModel));
        this.settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        identities.RegisterInitial(document.Configuration);
        migrationPlanner = new ConfigurationIdentityMigrationPlanner(document.Configuration, identities);
        Navigation =
        [
            new(ConfigurationStudioSection.Overview, "Overview", "File status and validation"),
            new(ConfigurationStudioSection.Systems, "FNE Systems", "Connections and credentials"),
            new(ConfigurationStudioSection.Zones, "Zones & Channels", "CPS-style channel editor"),
            new(ConfigurationStudioSection.Streams, "Web Streams", "Streams across all zones"),
            new(ConfigurationStudioSection.Groups, "Groups", "Definitions and operator state"),
            new(ConfigurationStudioSection.EncryptionKeys, "Encryption Keys", "Referenced local key file"),
            new(ConfigurationStudioSection.Files, "Files & Interoperability", "Paths, aliases, YAML, and exports")
        ];
        selectedNavigation = Navigation.First(item => item.Section == initialSection);
        foreach (OriginalSystemIdentity system in migrationPlanner.OriginalSystems)
            lastSystemRenameTargets[system.Id] = system.Name;
        UserSettings initialSettings = settingsStore.Load();
        originalWidgetPositions = initialSettings.ChannelWidgetPositions.ToDictionary(
            entry => entry.Key,
            entry => new WidgetPositionSetting { X = entry.Value.X, Y = entry.Value.Y },
            StringComparer.OrdinalIgnoreCase);
        InitializeDraftZoneSystems(CodeplugStudioStateStore.Get(initialSettings, document.SourcePath));
        InitializeDraftWidgetPositions();
        LoadReferencedKeyFile();
        LoadReferencedAliasFiles();
        RefreshCollections();
        currentSnapshot = CaptureDraftSnapshot();
        savedFingerprint = currentSnapshot.Fingerprint;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<ConfigurationStudioNavigationItem> Navigation { get; }
    public ObservableCollection<ConfigurationHierarchyNode> ConfigurationHierarchy { get; } = [];
    public ObservableCollection<SystemConfiguration> Systems { get; } = [];
    public ObservableCollection<ZoneConfiguration> Zones { get; } = [];
    public ObservableCollection<ChannelConfiguration> Channels { get; } = [];
    public ObservableCollection<ConfigurationChannelRow> VisibleChannelRows { get; } = [];
    public ObservableCollection<ConfigurationStreamRow> Streams { get; } = [];
    public ObservableCollection<GroupConfiguration> Groups { get; } = [];
    public ObservableCollection<KeyEntry> KeyEntries { get; } = [];
    public ObservableCollection<ConfigurationAliasRow> Aliases { get; } = [];
    public ObservableCollection<ConfigurationChannelPreviewViewModel> PreviewChannels { get; } = [];
    public ObservableCollection<ConfigurationValidationIssue> ValidationIssues { get; } = [];
    public IReadOnlyList<PatchGroupEditorViewModel> OperationalGroups => runtimeViewModel.PatchGroups;
    public IReadOnlyList<ConfigurationProtocolOption> ModeOptions { get; } = ConfigurationProtocolCatalog.ForChannels;
    public IReadOnlyList<string> CardSizeOptions { get; } = ["small", "normal", "large"];
    public IReadOnlyList<string> TransportModeOptions { get; } = ["auto", "ecb", "cbc"];
    public IReadOnlyList<string> GroupTypeOptions { get; } = ["patch", "multiselect"];
    public IReadOnlyList<ConfigurationProtocolOption> ProtocolOptions { get; } = ConfigurationProtocolCatalog.ForEncryptionKeys;

    public ConfigurationDocument Document => document;
    public ConsoleConfiguration Configuration => document.Configuration;
    public bool CanEdit => !document.IsReadOnly;
    public bool CanExportSanitized => !document.IsReadOnly;
    public bool IsDirty => !string.Equals(currentSnapshot.Fingerprint, savedFingerprint, StringComparison.Ordinal);
    public bool IsKeyFileDirty => keyFilePath is not null &&
        !string.Equals(keyFileSnapshot, KeyFileLoader.Serialize(keyContainer), StringComparison.Ordinal);
    public bool LayoutChanged => draftWidgetPositions.Any(entry =>
        !originalWidgetPositions.TryGetValue(GetChannelSettingsKey(entry.Key), out WidgetPositionSetting? saved) ||
        Math.Abs(saved.X - entry.Value.X) >= 0.01 || Math.Abs(saved.Y - entry.Value.Y) >= 0.01);
    public bool AliasFilesDirty => aliasTables.Any(entry =>
        !aliasFileSnapshots.TryGetValue(entry.Key, out string? snapshot) ||
        !string.Equals(snapshot, AliasFileLoader.Serialize(entry.Value), StringComparison.Ordinal));
    public bool CanUndo => history.CanUndo;
    public bool CanRedo => history.CanRedo;
    public bool HasErrors => ValidationIssues.Any(issue => issue.IsError);
    public bool HasWarnings => ValidationIssues.Any(issue => !issue.IsError);
    public bool HasValidationIssues => ValidationIssues.Count > 0;
    public IBrush ValidationIndicatorBrush => HasErrors
        ? ErrorIndicatorBrush
        : HasWarnings ? WarningIndicatorBrush : ValidIndicatorBrush;
    public string DocumentPathText => document.SourcePath ?? "Unsaved configuration";
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
    public string StreamNavigationHeading => $"Web Streams ({Streams.Count})";
    public string GroupNavigationHeading => $"Groups ({Groups.Count})";
    public string KeyNavigationHeading => $"Encryption Keys ({KeyEntries.Count})";
    public string FileNavigationHeading => $"Files & Interoperability ({document.UnknownFields.Count})";
    public double PreviewCanvasWidth => Math.Max(MainWindowViewModel.DefaultWidgetCanvasWidth, PreviewChannels.Count == 0
        ? MainWindowViewModel.DefaultWidgetCanvasWidth
        : PreviewChannels.Max(preview => preview.X + preview.CardWidth + 12));
    public double PreviewCanvasHeight => Math.Max(runtimeViewModel.ChannelCardHeight + 12, PreviewChannels.Count == 0
        ? runtimeViewModel.ChannelCardHeight + 12
        : PreviewChannels.Max(preview => preview.Y + preview.CardHeight + 12));
    public double PreviewCardHeight => runtimeViewModel.ChannelCardHeight;
    public double PreviewUiFontSize => runtimeViewModel.UiFontSize;
    public double PreviewUiSmallFontSize => runtimeViewModel.UiSmallFontSize;
    public double PreviewUiCompactFontSize => runtimeViewModel.UiCompactFontSize;
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
    public string KeyFilePathText => keyFilePath ?? "No key file is referenced.";
    public bool HasKeyFile => keyFilePath is not null;
    public bool CanUseOperationalGroups => document.SourcePath is not null &&
        runtimeViewModel.CurrentCodeplugPath is not null &&
        string.Equals(Path.GetFullPath(document.SourcePath), Path.GetFullPath(runtimeViewModel.CurrentCodeplugPath), StringComparison.OrdinalIgnoreCase);
    public string OperationalGroupHint => CanUseOperationalGroups
        ? "Enable and multi-select PTT actions affect the active console immediately. Apply operator state stages membership and direction for Review & Save."
        : "Operational controls are unavailable because this draft is unsaved or is not the active codeplug.";

    public IReadOnlyList<EncryptionAlgorithmOption> AvailableChannelAlgorithms => availableChannelAlgorithms;
    public IReadOnlyList<EncryptionAlgorithmOption> AvailableKeyAlgorithms => availableKeyAlgorithms;

    public EncryptionAlgorithmOption? SelectedChannelAlgorithm
    {
        get => selectedChannelAlgorithm;
        set
        {
            if (SelectedChannel is null || value is null || !SetField(ref selectedChannelAlgorithm, value))
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
            SelectedKey.KeyId = ushort.TryParse(
                normalized,
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out ushort keyId)
                ? keyId
                : (ushort)0;
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
        get => selectedNavigation;
        set
        {
            if (value is null || ReferenceEquals(selectedNavigation, value))
                return;
            selectedNavigation = value;
            OnPropertyChanged();
            NotifySectionVisibility();
        }
    }

    public bool IsOverview => selectedNavigation.Section == ConfigurationStudioSection.Overview;
    public bool IsSystems => selectedNavigation.Section == ConfigurationStudioSection.Systems;
    public bool IsZones => selectedNavigation.Section == ConfigurationStudioSection.Zones;
    public bool IsStreams => selectedNavigation.Section == ConfigurationStudioSection.Streams;
    public bool IsGroups => selectedNavigation.Section == ConfigurationStudioSection.Groups;
    public bool IsEncryptionKeys => selectedNavigation.Section == ConfigurationStudioSection.EncryptionKeys;
    public bool IsFiles => selectedNavigation.Section == ConfigurationStudioSection.Files;

    public SystemConfiguration? SelectedSystem
    {
        get => selectedSystem;
        set => SetField(ref selectedSystem, value);
    }
    public ZoneConfiguration? SelectedZone
    {
        get => selectedZone;
        set
        {
            if (!SetField(ref selectedZone, value))
                return;
            RefreshChannelsAndPreview();
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
            foreach (ConfigurationChannelPreviewViewModel preview in PreviewChannels)
                preview.IsSelected = ReferenceEquals(preview.Channel, value);
            if (value is not null && channelHierarchyNodes.TryGetValue(value, out ConfigurationHierarchyNode? channelNode))
            {
                if (!ReferenceEquals(selectedHierarchyNode, channelNode))
                {
                    selectedHierarchyNode = channelNode;
                    OnPropertyChanged(nameof(SelectedHierarchyNode));
                }
                if (channelNode.Zone is not null && zoneHierarchyNodes.TryGetValue(channelNode.Zone, out ConfigurationHierarchyNode? zoneNode))
                {
                    zoneNode.IsExpanded = true;
                    ConfigurationHierarchyNode? systemNode = ConfigurationHierarchy.FirstOrDefault(node => node.Children.Contains(zoneNode));
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
            RefreshKeyEditorState();
        }
    }
    public ConfigurationAliasRow? SelectedAlias
    {
        get => selectedAlias;
        set => SetField(ref selectedAlias, value);
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
                SelectSection(ConfigurationStudioSection.Zones);
            }
            else if (value.System is not null)
            {
                SelectedSystem = value.System;
                SelectSection(ConfigurationStudioSection.Systems);
            }
        }
    }

    public void SelectSection(ConfigurationStudioSection section)
        => SelectedNavigation = Navigation.First(item => item.Section == section);

    public void CommitFieldEdit()
    {
        if (!CanEdit)
            return;
        ConfigurationStudioDraftSnapshot before = currentSnapshot;
        identities.Synchronize(Configuration);
        ApplySystemRenameReferences();
        if (!string.Equals(loadedKeyReference, Configuration.KeyFile, StringComparison.Ordinal))
            LoadReferencedKeyFile();
        string aliasReference = CreateAliasReference();
        if (!string.Equals(loadedAliasReference, aliasReference, StringComparison.Ordinal))
            LoadReferencedAliasFiles();
        SynchronizeDraftZoneSystems();
        SynchronizeDraftWidgetPositions();
        CompleteDraftTransition(before, markDocumentDirty: true);
        RefreshCollections(preserveSelection: true);
    }

    public void CommitZoneSystemEdit()
    {
        CommitFieldEdit();
        RefreshConfigurationHierarchy();
    }

    public void CommitKeyEdit()
    {
        ConfigurationStudioDraftSnapshot before = currentSnapshot;
        KeyEntry? selection = SelectedKey;
        if (selection is not null)
        {
            int index = KeyEntries.IndexOf(selection);
            if (index >= 0)
                KeyEntries[index] = selection;
        }
        SelectedKey = selection;
        RefreshKeyEditorState();
        CompleteDraftTransition(before);
        RefreshValidation();
        NotifyDocumentState();
    }

    public void CommitChannelModeEdit()
    {
        if (SelectedChannel is null)
            return;
        RefreshChannelAlgorithmOptions(normalizeUnsupported: true);
        RefreshChannelKeyIdText();
        OnPropertyChanged(nameof(IsSelectedChannelDmr));
        CommitFieldEdit();
    }

    public void CommitChannelAlgorithmEdit()
    {
        RefreshChannelKeyIdText();
        CommitFieldEdit();
    }

    public void CommitKeyProtocolEdit()
    {
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
        CompleteDraftTransition(currentSnapshot);
        RefreshValidation();
        NotifyDocumentState();
    }

    public void Undo()
    {
        if (document.IsReadOnly || history.Undo(currentSnapshot) is not { } snapshot)
            return;
        RestoreDraftSnapshot(snapshot);
    }

    public void Redo()
    {
        if (document.IsReadOnly || history.Redo(currentSnapshot) is not { } snapshot)
            return;
        RestoreDraftSnapshot(snapshot);
    }

    public void AddSystem()
    {
        Mutate(() => Configuration.Systems.Add(new SystemConfiguration
        {
            Name = UniqueName("New System", Configuration.Systems.Select(system => system.Name)),
            Address = "127.0.0.1",
            Port = 62031,
            Identity = "DVM Console",
            TransportEncryptionMode = "auto"
        }));
        SelectedSystem = Configuration.Systems.Last();
    }

    public void DuplicateSystem()
    {
        if (SelectedSystem is null)
            return;
        SystemConfiguration source = SelectedSystem;
        Mutate(() => Configuration.Systems.Add(new SystemConfiguration
        {
            Name = UniqueName($"{source.Name} Copy", Configuration.Systems.Select(system => system.Name)),
            Identity = source.Identity,
            Address = source.Address,
            Port = source.Port,
            Password = source.Password,
            PresharedKey = source.PresharedKey,
            KmfPresharedKey = source.KmfPresharedKey,
            Encrypted = source.Encrypted,
            TransportEncryptionMode = source.TransportEncryptionMode,
            PeerId = source.PeerId,
            Rid = source.Rid,
            AliasPath = source.AliasPath
        }));
        SelectedSystem = Configuration.Systems.Last();
    }

    public void DeleteSystem()
    {
        if (SelectedSystem is { } system)
            Mutate(() => Configuration.Systems.Remove(system));
    }

    public void AddZone()
    {
        var zone = new ZoneConfiguration
        {
            Name = UniqueName("New Zone", Configuration.Zones.Select(zone => zone.Name))
        };
        Mutate(() =>
        {
            Configuration.Zones.Add(zone);
            draftZoneSystemNames[zone] = Configuration.Systems.FirstOrDefault()?.Name ?? string.Empty;
        });
        SelectedZone = zone;
    }

    public void DuplicateZone()
    {
        if (SelectedZone is null)
            return;
        ZoneConfiguration source = SelectedZone;
        var copy = new ZoneConfiguration
        {
            Name = UniqueName($"{source.Name} Copy", Configuration.Zones.Select(zone => zone.Name)),
            TabColor = source.TabColor,
            TabTextColor = source.TabTextColor,
            Channels = source.Channels.Select(CloneChannel).ToList(),
            WebStreams = source.WebStreams.Select(CloneStream).ToList()
        };
        Mutate(() =>
        {
            Configuration.Zones.Add(copy);
            draftZoneSystemNames[copy] = GetDraftZoneSystemName(source);
        });
        SelectedZone = copy;
    }

    public void DeleteZone()
    {
        if (SelectedZone is { } zone)
            Mutate(() => Configuration.Zones.Remove(zone));
    }

    public void AddChannel()
    {
        if (SelectedZone is null)
            return;
        string systemName = GetDraftZoneSystemName(SelectedZone);
        Mutate(() => SelectedZone.Channels.Add(new ChannelConfiguration
        {
            Name = UniqueName("New Channel", Configuration.Zones.SelectMany(zone => zone.Channels).Select(channel => channel.Name)),
            System = systemName,
            Tgid = "1",
            CardSize = "normal"
        }));
        SelectedChannel = SelectedZone.Channels.Last();
    }

    public void DuplicateChannel()
    {
        if (SelectedZone is null || SelectedChannel is null)
            return;
        ChannelConfiguration copy = CloneChannel(SelectedChannel);
        copy.Name = UniqueName($"{SelectedChannel.Name} Copy", Configuration.Zones.SelectMany(zone => zone.Channels).Select(channel => channel.Name));
        copy.System = GetDraftZoneSystemName(SelectedZone);
        Mutate(() => SelectedZone.Channels.Add(copy));
        SelectedChannel = copy;
    }

    public void DeleteChannel()
    {
        if (SelectedZone is { } zone && SelectedChannel is { } channel)
            Mutate(() => zone.Channels.Remove(channel));
    }

    public void MoveChannel(int offset)
    {
        if (SelectedZone is null || SelectedChannel is null)
            return;
        int index = SelectedZone.Channels.IndexOf(SelectedChannel);
        int destination = index + offset;
        if (index < 0 || destination < 0 || destination >= SelectedZone.Channels.Count)
            return;
        Mutate(() =>
        {
            SelectedZone.Channels.RemoveAt(index);
            SelectedZone.Channels.Insert(destination, SelectedChannel);
        });
    }

    public void SetChannelsRxOnly(IEnumerable<ChannelConfiguration> channels, bool rxOnly)
    {
        ChannelConfiguration[] selected = channels.Distinct().ToArray();
        if (selected.Length == 0)
            return;
        Mutate(() =>
        {
            foreach (ChannelConfiguration channel in selected)
                channel.RxOnly = rxOnly;
        });
    }

    public void ApplySelectedCardSize(IEnumerable<ChannelConfiguration> channels)
    {
        if (SelectedChannel is not { } source)
            return;
        ChannelConfiguration[] selected = channels.Distinct().ToArray();
        if (selected.Length == 0)
            return;
        Mutate(() =>
        {
            foreach (ChannelConfiguration channel in selected)
                channel.CardSize = source.CardSize;
        });
    }

    public void AddStream()
    {
        ZoneConfiguration? zone = SelectedZone ?? Configuration.Zones.FirstOrDefault();
        if (zone is null)
            return;
        var stream = new WebStreamConfiguration
        {
            Name = UniqueName("New Stream", Configuration.Zones.SelectMany(item => item.WebStreams).Select(item => item.Name)),
            Url = "https://example.invalid/stream"
        };
        Mutate(() => zone.WebStreams.Add(stream));
        SelectedStream = Streams.FirstOrDefault(row => ReferenceEquals(row.Stream, stream));
    }

    public void DeleteStream()
    {
        if (SelectedStream is { } row)
            Mutate(() => row.Zone.WebStreams.Remove(row.Stream));
    }

    public void MoveSelectedStreamTo(ZoneConfiguration zone)
    {
        if (SelectedStream is not { } row || ReferenceEquals(row.Zone, zone))
            return;
        WebStreamConfiguration stream = row.Stream;
        Mutate(() =>
        {
            row.Zone.WebStreams.Remove(stream);
            zone.WebStreams.Add(stream);
        });
        SelectedStream = Streams.FirstOrDefault(item => ReferenceEquals(item.Stream, stream));
    }

    public void AddGroup()
    {
        Mutate(() => Configuration.Groups.Add(new GroupConfiguration
        {
            Name = UniqueName("New Group", Configuration.Groups.Select(group => group.Name)),
            Type = "patch"
        }));
        SelectedGroup = Configuration.Groups.Last();
    }

    public void DeleteGroup()
    {
        if (SelectedGroup is { } group)
            Mutate(() => Configuration.Groups.Remove(group));
    }

    public void AddKey()
    {
        if (!HasKeyFile)
            return;
        EncryptionAlgorithmOption algorithm = EncryptionAlgorithmCatalog.ForKeyProtocol("p25")[0];
        var key = new KeyEntry
        {
            Protocol = algorithm.Protocol,
            KeyId = 1,
            AlgId = algorithm.AlgorithmId ?? 0,
            Key = string.Empty
        };
        keyContainer.Keys.Add(key);
        KeyEntries.Add(key);
        SelectedKey = key;
        keyFileLoadError = null;
        keyFileLoadIsWarning = false;
        CommitKeyEdit();
    }

    public void DeleteKey()
    {
        if (SelectedKey is not { } key)
            return;
        keyContainer.Keys.Remove(key);
        KeyEntries.Remove(key);
        SelectedKey = KeyEntries.FirstOrDefault();
        CommitKeyEdit();
    }

    public void AddAlias()
    {
        KeyValuePair<string, List<RadioAlias>> table = aliasTables.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(table.Key))
            return;
        uint nextRid = table.Value.Count == 0 ? 1 : table.Value.Max(alias => alias.Rid) + 1;
        var alias = new RadioAlias { Rid = nextRid, Alias = "New Alias" };
        table.Value.Add(alias);
        var row = new ConfigurationAliasRow(table.Key, alias);
        Aliases.Add(row);
        SelectedAlias = row;
        CommitAliasEdit();
    }

    public void DeleteAlias()
    {
        if (SelectedAlias is not { } row || !aliasTables.TryGetValue(row.FilePath, out List<RadioAlias>? table))
            return;
        table.Remove(row.Alias);
        Aliases.Remove(row);
        SelectedAlias = Aliases.FirstOrDefault();
        CommitAliasEdit();
    }

    public string? StageOperationalGroup(PatchGroupEditorViewModel group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (group.GetMembershipValidationError() is { } error)
            return error;
        ConfigurationStudioDraftSnapshot before = currentSnapshot;
        stagedGroupMemberships[group.Name] = group.GetMembersInRoutingOrder()
            .Select(member => new PatchMemberSetting
            {
                SystemName = member.Channel.Definition.SystemName,
                DestinationId = member.Channel.Definition.DestinationId,
                ChannelName = member.Channel.Name
            })
            .ToList();
        stagedGroupModes[group.Name] = group.IsPatchGroup && group.IsOneWay;
        CompleteDraftTransition(before);
        NotifyDocumentState();
        return null;
    }

    public ConfigurationSavePlan CreateSavePlan(string destinationPath)
    {
        string fullDestination = Path.GetFullPath(destinationPath);
        var issues = ValidationIssues.ToList();
        var files = new List<ConfigurationFileChange>
        {
            new(
                fullDestination,
                document.Serialize(),
                document.SourcePath is not null && string.Equals(fullDestination, document.SourcePath, StringComparison.OrdinalIgnoreCase)
                    ? document.SourceHash
                    : null,
                "Codeplug",
                ContainsSecrets: true)
        };

        if (keyFilePath is not null)
        {
            string keyTarget = ResolveReferencedSaveTarget(
                Configuration.KeyFile,
                keyFilePath,
                fullDestination);
            if (IsKeyFileDirty || !string.Equals(keyTarget, keyFilePath, StringComparison.OrdinalIgnoreCase))
            {
                files.Add(new ConfigurationFileChange(
                    keyTarget,
                    KeyFileLoader.Serialize(keyContainer),
                    GetExpectedHash(keyTarget, keyFilePath, keyFileHash),
                    "Encryption key file",
                    ContainsSecrets: true));
            }
        }

        foreach (KeyValuePair<string, List<RadioAlias>> aliasTable in aliasTables)
        {
            string serialized = AliasFileLoader.Serialize(aliasTable.Value);
            bool dirty = !aliasFileSnapshots.TryGetValue(aliasTable.Key, out string? snapshot) ||
                !string.Equals(snapshot, serialized, StringComparison.Ordinal);
            foreach (string target in GetAliasSaveTargets(aliasTable.Key, fullDestination))
            {
                if (!dirty && string.Equals(target, aliasTable.Key, StringComparison.OrdinalIgnoreCase))
                    continue;
                files.Add(new ConfigurationFileChange(
                    target,
                    serialized,
                    GetExpectedHash(target, aliasTable.Key, aliasFileHashes.GetValueOrDefault(aliasTable.Key)),
                    "RID alias file",
                    ContainsSecrets: false));
            }
        }

        UserSettings settings = settingsStore.Load();
        if (document.SourcePath is not null && !string.Equals(document.SourcePath, fullDestination, StringComparison.OrdinalIgnoreCase))
        {
            CodeplugGroupStateStore.CopyForSaveAs(settings, document.SourcePath, fullDestination);
            CodeplugStudioStateStore.CopyForSaveAs(settings, document.SourcePath, fullDestination);
        }
        ApplyIdentityMigrations(settings, fullDestination, fullDestination);
        CodeplugGroupState destinationGroupState = CodeplugGroupStateStore.GetOrMigrate(settings, fullDestination);
        Dictionary<string, string> groupRenames = migrationPlanner.BuildGroupMigrations().Renames;
        foreach (KeyValuePair<string, List<PatchMemberSetting>> membership in stagedGroupMemberships)
            destinationGroupState.Memberships[groupRenames.GetValueOrDefault(membership.Key, membership.Key)] = membership.Value;
        foreach (KeyValuePair<string, bool> mode in stagedGroupModes)
            destinationGroupState.OneWayModes[groupRenames.GetValueOrDefault(mode.Key, mode.Key)] = mode.Value;
        foreach (KeyValuePair<ChannelConfiguration, WidgetPositionSetting> position in draftWidgetPositions)
        {
            settings.ChannelWidgetPositions[GetChannelSettingsKey(position.Key)] = new WidgetPositionSetting
            {
                X = position.Value.X,
                Y = position.Value.Y
            };
        }
        CodeplugStudioState destinationStudioState = CodeplugStudioStateStore.Get(settings, fullDestination);
        destinationStudioState.ZoneSystemAssignments = draftZoneSystemNames
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Value))
            .ToDictionary(
                entry => entry.Key.Name,
                entry => entry.Value,
                StringComparer.OrdinalIgnoreCase);
        UserSettingsSnapshot settingsSnapshot = settingsStore.CaptureSnapshot(settings);
        files.Add(new ConfigurationFileChange(
            settingsStore.Path,
            settingsSnapshot.Json,
            File.Exists(settingsStore.Path) ? ConfigurationDocument.ComputeFileHash(settingsStore.Path) : null,
            "Operator settings",
            ContainsSecrets: false));

        return new ConfigurationSavePlan(files, issues);
    }

    private string ResolveReferencedSaveTarget(
        string? reference,
        string currentResolvedPath,
        string codeplugDestination)
    {
        if (string.IsNullOrWhiteSpace(reference) || Path.IsPathRooted(reference) || document.SourcePath is null)
            return currentResolvedPath;
        string sourceDirectory = Path.GetDirectoryName(document.SourcePath) ?? AppContext.BaseDirectory;
        string destinationDirectory = Path.GetDirectoryName(codeplugDestination) ?? AppContext.BaseDirectory;
        if (string.Equals(sourceDirectory, destinationDirectory, StringComparison.OrdinalIgnoreCase))
            return currentResolvedPath;
        return Path.GetFullPath(Path.Combine(destinationDirectory, reference));
    }

    private IReadOnlyList<string> GetAliasSaveTargets(string currentResolvedPath, string codeplugDestination)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SystemConfiguration system in Configuration.Systems)
        {
            if (string.IsNullOrWhiteSpace(system.AliasPath))
                continue;
            string sourceResolved;
            try
            {
                sourceResolved = ConfigurationLoader.ResolvePath(Configuration, system.AliasPath);
            }
            catch (InvalidOperationException)
            {
                continue;
            }
            if (string.Equals(sourceResolved, currentResolvedPath, StringComparison.OrdinalIgnoreCase))
            {
                targets.Add(ResolveReferencedSaveTarget(
                    system.AliasPath,
                    currentResolvedPath,
                    codeplugDestination));
            }
        }
        if (targets.Count == 0)
            targets.Add(currentResolvedPath);
        return targets.ToArray();
    }

    private static string? GetExpectedHash(string target, string source, string? sourceHash)
    {
        if (string.Equals(target, source, StringComparison.OrdinalIgnoreCase))
            return sourceHash;
        return File.Exists(target) ? ConfigurationDocument.ComputeFileHash(target) : null;
    }

    public void AcceptSaved(string path, ConfigurationSavePlan plan)
    {
        string codeplugText = plan.Files.First(file => file.Category == "Codeplug").Content;
        document.AcceptSaved(path, codeplugText);
        LoadReferencedKeyFile();
        LoadReferencedAliasFiles();
        originalWidgetPositions.Clear();
        foreach (KeyValuePair<ChannelConfiguration, WidgetPositionSetting> position in draftWidgetPositions)
        {
            originalWidgetPositions[GetChannelSettingsKey(position.Key)] = new WidgetPositionSetting
            {
                X = position.Value.X,
                Y = position.Value.Y
            };
        }
        stagedGroupMemberships.Clear();
        stagedGroupModes.Clear();
        migrationPlanner.ResetBaseline(Configuration);
        lastSystemRenameTargets.Clear();
        foreach (OriginalSystemIdentity system in migrationPlanner.OriginalSystems)
            lastSystemRenameTargets[system.Id] = system.Name;
        history.Clear();
        currentSnapshot = CaptureDraftSnapshot();
        savedFingerprint = currentSnapshot.Fingerprint;
        NotifyDocumentState();
    }

    public string BuildReviewText(ConfigurationSavePlan plan)
    {
        string changes = string.Join("\n", plan.Files.Select(file =>
            $"• {file.Category}: {file.Path}"));
        string compatibility = document.UnknownFields.Count > 0
            ? $"\n\n{document.UnknownFields.Count} unmatched YAML field(s) will be retained."
            : string.Empty;
        string migrations = BuildIdentityMigrationSummary();
        return $"Configuration Studio will write:\n\n{changes}{compatibility}{migrations}\n\n" +
               "Edited sections are written in canonical YAML. Comments and hand formatting inside those sections may change. " +
               "Restricted backups are created before any original file is replaced.";
    }

    public void MovePreviewChannel(ConfigurationChannelPreviewViewModel preview, double x, double y)
    {
        preview.X = Math.Round(Math.Max(0, x) / 10) * 10;
        preview.Y = Math.Round(Math.Max(0, y) / 10) * 10;
        draftWidgetPositions[preview.Channel] = new WidgetPositionSetting { X = preview.X, Y = preview.Y };
        OnPropertyChanged(nameof(PreviewCanvasWidth));
        OnPropertyChanged(nameof(PreviewCanvasHeight));
    }

    public void BeginPreviewMove()
    {
        previewMoveStartSnapshot ??= currentSnapshot;
    }

    public void CommitPreviewMove()
    {
        if (previewMoveStartSnapshot is not { } before)
            return;
        previewMoveStartSnapshot = null;
        CompleteDraftTransition(before);
        NotifyDocumentState();
    }

    private void ApplyIdentityMigrations(UserSettings settings, string statePath, string destinationPath)
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

        bool pathChanged = document.SourcePath is not null &&
            !string.Equals(document.SourcePath, destinationPath, StringComparison.OrdinalIgnoreCase);
        foreach (StreamIdentityMigration migration in migrationPlanner.BuildStreamMigrations(pathChanged))
        {
            OriginalStreamIdentity original = migration.Original;
            WebStreamConfiguration? current = migration.Current;
            if (current is null)
            {
                settings.WebStreamOutputDeviceIds.Remove(original.Name);
                settings.WebStreamVolumes.Remove(original.Name);
                continue;
            }
            if (!string.Equals(original.Name, current.Name, StringComparison.OrdinalIgnoreCase))
            {
                MoveDictionaryEntry(settings.WebStreamOutputDeviceIds, original.Name, current.Name);
                MoveDictionaryEntry(settings.WebStreamVolumes, original.Name, current.Name);
            }

            string oldIdentity = WebStreamSelectionIdentity.Create(document.SourcePath ?? statePath, original.Configuration);
            string newIdentity = WebStreamSelectionIdentity.Create(destinationPath, current);
            if (oldIdentity.Length > 0 && newIdentity.Length > 0)
                MoveListEntry(settings.SelectedWebStreams, oldIdentity, newIdentity, StringComparison.Ordinal);
        }
    }

    private void ApplySystemRenameReferences()
    {
        foreach (OriginalSystemIdentity original in migrationPlanner.OriginalSystems)
        {
            string previous = lastSystemRenameTargets.GetValueOrDefault(original.Id, original.Name);
            string current = migrationPlanner.FindCurrentSystem(original)?.Name ?? original.Name;
            if (string.Equals(previous, current, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (ChannelConfiguration channel in Configuration.Zones.SelectMany(zone => zone.Channels))
            {
                if (string.Equals(channel.System, previous, StringComparison.OrdinalIgnoreCase))
                    channel.System = current;
            }
            lastSystemRenameTargets[original.Id] = current;
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
        ConfigurationStudioDraftSnapshot before = currentSnapshot;
        action();
        identities.Synchronize(Configuration);
        SynchronizeDraftZoneSystems();
        SynchronizeDraftWidgetPositions();
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
        availableChannelAlgorithms = EncryptionAlgorithmCatalog.ForChannelMode(SelectedChannel?.Mode);
        OnPropertyChanged(nameof(AvailableChannelAlgorithms));
        EncryptionAlgorithmOption? option = EncryptionAlgorithmCatalog.FindChannelOption(
            SelectedChannel?.Mode,
            SelectedChannel?.Algo);
        if (option is null && normalizeUnsupported)
        {
            option = availableChannelAlgorithms.Count > 0 ? availableChannelAlgorithms[0] : null;
            if (SelectedChannel is not null && option is not null)
                SelectedChannel.Algo = option.ConfigurationValue;
        }
        selectedChannelAlgorithm = option;
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
        selectedKeyIdHexDigits = SelectedKey is null
            ? string.Empty
            : SelectedKey.KeyId.ToString("X", CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(SelectedKeyIdHexDigits));
    }

    private void RefreshKeyAlgorithmOptions(bool normalizeUnsupported)
    {
        availableKeyAlgorithms = EncryptionAlgorithmCatalog.ForKeyProtocol(SelectedKey?.Protocol);
        OnPropertyChanged(nameof(AvailableKeyAlgorithms));
        EncryptionAlgorithmOption? option = SelectedKey is null
            ? null
            : EncryptionAlgorithmCatalog.FindKeyOption(SelectedKey.Protocol, SelectedKey.AlgId);
        if (option is null && normalizeUnsupported)
        {
            option = availableKeyAlgorithms.Count > 0 ? availableKeyAlgorithms[0] : null;
            if (SelectedKey is not null && option?.AlgorithmId is int algorithmId)
                SelectedKey.AlgId = algorithmId;
        }
        selectedKeyAlgorithm = option;
        OnPropertyChanged(nameof(SelectedKeyAlgorithm));
        OnPropertyChanged(nameof(SelectedKeyAlgorithmIdText));
    }

    private void RefreshCollections(bool preserveSelection = false)
    {
        string? systemName = preserveSelection ? SelectedSystem?.Name : null;
        string? zoneName = preserveSelection ? SelectedZone?.Name : null;
        string? channelName = preserveSelection ? SelectedChannel?.Name : null;
        string? streamName = preserveSelection ? SelectedStream?.Stream.Name : null;
        string? groupName = preserveSelection ? SelectedGroup?.Name : null;

        Replace(Systems, Configuration.Systems);
        Replace(Zones, Configuration.Zones);
        SynchronizeDraftZoneSystems();
        Replace(Streams, Configuration.Zones.SelectMany(zone => zone.WebStreams.Select(stream => new ConfigurationStreamRow(zone, stream))));
        Replace(Groups, Configuration.Groups);
        SelectedSystem = Systems.FirstOrDefault(system => string.Equals(system.Name, systemName, StringComparison.OrdinalIgnoreCase)) ?? Systems.FirstOrDefault();
        SelectedZone = Zones.FirstOrDefault(zone => string.Equals(zone.Name, zoneName, StringComparison.OrdinalIgnoreCase)) ?? Zones.FirstOrDefault();
        RefreshChannelsAndPreview();
        SelectedChannel = Channels.FirstOrDefault(channel => string.Equals(channel.Name, channelName, StringComparison.OrdinalIgnoreCase)) ?? Channels.FirstOrDefault();
        SelectedStream = Streams.FirstOrDefault(row => string.Equals(row.Stream.Name, streamName, StringComparison.OrdinalIgnoreCase)) ?? Streams.FirstOrDefault();
        SelectedGroup = Groups.FirstOrDefault(group => string.Equals(group.Name, groupName, StringComparison.OrdinalIgnoreCase)) ?? Groups.FirstOrDefault();
        RefreshConfigurationHierarchy();
        RefreshValidation();
        NotifyDocumentState();
    }

    private void RefreshChannelsAndPreview()
    {
        SynchronizeDraftWidgetPositions();
        Replace(Channels, SelectedZone?.Channels ?? []);
        RefreshVisibleChannelRows();
        if (SelectedZone is null)
        {
            PreviewChannels.Clear();
            return;
        }
        HashSet<ChannelConfiguration> selectedChannels = SelectedZone.Channels.ToHashSet();
        foreach (ChannelConfiguration removed in previewCache.Keys.Where(channel => !selectedChannels.Contains(channel)).ToArray())
            previewCache.Remove(removed);
        var previews = new List<ConfigurationChannelPreviewViewModel>(SelectedZone.Channels.Count);
        for (int index = 0; index < SelectedZone.Channels.Count; index++)
        {
            ChannelConfiguration channel = SelectedZone.Channels[index];
            WidgetPositionSetting position = draftWidgetPositions[channel];
            double x = position.X;
            double y = position.Y;
            string signature = GetPreviewSignature(channel);
            if (!previewCache.TryGetValue(channel, out var cached) || !string.Equals(cached.Signature, signature, StringComparison.Ordinal))
            {
                cached = (signature, new ConfigurationChannelPreviewViewModel(
                    channel,
                    x,
                    y,
                    runtimeViewModel.ChannelCardHeight,
                    runtimeViewModel.DarkMode));
                previewCache[channel] = cached;
            }
            cached.Preview.X = x;
            cached.Preview.Y = y;
            previews.Add(cached.Preview);
        }
        Replace(PreviewChannels, previews);
        SelectedChannel = VisibleChannelRows.FirstOrDefault()?.Channel;
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
                row = new ConfigurationChannelRow(index + 1, channel);
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
        Replace(ValidationIssues, document.Validate().Concat(ValidateKeys()));
        if (keyFileLoadError is not null)
        {
            ValidationIssues.Add(new ConfigurationValidationIssue(
                keyFileLoadIsWarning ? ConfigurationValidationSeverity.Warning : ConfigurationValidationSeverity.Error,
                "Encryption Keys",
                "keyFile",
                keyFileLoadError));
        }
        foreach (string error in aliasLoadErrors)
            ValidationIssues.Add(new ConfigurationValidationIssue(ConfigurationValidationSeverity.Error, "Files & Interoperability", "aliasPath", error));
        foreach (string warning in aliasLoadWarnings)
            ValidationIssues.Add(new ConfigurationValidationIssue(ConfigurationValidationSeverity.Warning, "Files & Interoperability", "aliasPath", warning));
        foreach (KeyValuePair<string, List<RadioAlias>> table in aliasTables)
            foreach (IGrouping<uint, RadioAlias> duplicate in table.Value.GroupBy(alias => alias.Rid).Where(group => group.Count() > 1))
                ValidationIssues.Add(new ConfigurationValidationIssue(ConfigurationValidationSeverity.Error, "Files & Interoperability", table.Key, $"RID {duplicate.Key} is duplicated in '{table.Key}'."));
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

    private IEnumerable<ConfigurationValidationIssue> ValidateKeys()
        => KeyFileValidator.Validate(keyContainer);

    private void LoadReferencedKeyFile()
    {
        keyContainer = new KeyContainer();
        keyFilePath = null;
        keyFileHash = null;
        keyFileSnapshot = string.Empty;
        keyFileLoadError = null;
        keyFileLoadIsWarning = false;
        loadedKeyReference = Configuration.KeyFile;
        if (string.IsNullOrWhiteSpace(Configuration.KeyFile) || document.SourcePath is null)
        {
            Replace(KeyEntries, Array.Empty<KeyEntry>());
            return;
        }
        try
        {
            keyFilePath = ConfigurationLoader.ResolvePath(Configuration, Configuration.KeyFile);
            if (File.Exists(keyFilePath))
            {
                keyContainer = KeyFileLoader.Load(keyFilePath);
                keyFileHash = ConfigurationDocument.ComputeFileHash(keyFilePath);
                keyFileSnapshot = KeyFileLoader.Serialize(keyContainer);
            }
            else
            {
                keyFileLoadError = $"The referenced key file does not exist: {keyFilePath}";
                keyFileLoadIsWarning = true;
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException or YamlDotNet.Core.YamlException)
        {
            keyFileLoadError = $"The referenced key file could not be opened: {exception.Message}";
        }
        Replace(KeyEntries, keyContainer.Keys);
        SelectedKey = KeyEntries.FirstOrDefault();
        OnPropertyChanged(nameof(KeyFilePathText));
        OnPropertyChanged(nameof(HasKeyFile));
    }

    private void LoadReferencedAliasFiles()
    {
        aliasTables.Clear();
        aliasFileHashes.Clear();
        aliasFileSnapshots.Clear();
        aliasLoadErrors.Clear();
        aliasLoadWarnings.Clear();
        Aliases.Clear();
        loadedAliasReference = CreateAliasReference();
        if (document.SourcePath is null)
            return;

        foreach (SystemConfiguration system in Configuration.Systems)
        {
            if (string.IsNullOrWhiteSpace(system.AliasPath))
                continue;
            string path;
            try
            {
                path = ConfigurationLoader.ResolvePath(Configuration, system.AliasPath);
                if (!aliasTables.TryGetValue(path, out List<RadioAlias>? aliases))
                {
                    if (!File.Exists(path))
                    {
                        aliases = [];
                        aliasTables[path] = aliases;
                        aliasFileSnapshots[path] = string.Empty;
                        aliasLoadWarnings.Add($"Alias file for system '{system.Name}' does not exist yet: {path}");
                        continue;
                    }
                    aliases = AliasFileLoader.Load(path);
                    aliasTables[path] = aliases;
                    aliasFileHashes[path] = ConfigurationDocument.ComputeFileHash(path);
                    aliasFileSnapshots[path] = AliasFileLoader.Serialize(aliases);
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or YamlDotNet.Core.YamlException)
            {
                aliasLoadErrors.Add($"Alias file for system '{system.Name}' could not be opened: {exception.Message}");
            }
        }

        foreach (KeyValuePair<string, List<RadioAlias>> table in aliasTables)
            foreach (RadioAlias alias in table.Value)
                Aliases.Add(new ConfigurationAliasRow(table.Key, alias));
        SelectedAlias = Aliases.FirstOrDefault();
        OnPropertyChanged(nameof(AliasFilesDirty));
    }

    private string CreateAliasReference()
        => string.Join("\u001F", Configuration.Systems.Select(system => system.AliasPath ?? string.Empty));

    private void InitializeDraftZoneSystems(CodeplugStudioState savedState)
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
                0 when savedState.ZoneSystemAssignments.TryGetValue(zone.Name, out string? savedSystem) &&
                       Configuration.Systems.Any(system => string.Equals(system.Name, savedSystem, StringComparison.OrdinalIgnoreCase))
                    => savedSystem,
                0 => fallback,
                1 => systems[0],
                _ => string.Empty
            };
        }
    }

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
        string normalized = (query ?? searchText).Trim();
        bool Matches(string value) => normalized.Length == 0 ||
            value.Contains(normalized, StringComparison.OrdinalIgnoreCase);

        var roots = new List<ConfigurationHierarchyNode>();
        foreach (SystemConfiguration system in Configuration.Systems)
        {
            if (!systemHierarchyNodes.TryGetValue(system, out ConfigurationHierarchyNode? systemNode))
            {
                systemNode = new ConfigurationHierarchyNode(system.Name, system: system, isExpanded: true);
                systemHierarchyNodes[system] = systemNode;
            }

            bool systemMatches = Matches(system.Name) || Matches(system.Address);
            var visibleZones = new List<ConfigurationHierarchyNode>();
            foreach (ZoneConfiguration zone in Configuration.Zones.Where(zone =>
                         string.Equals(GetDraftZoneSystemName(zone), system.Name, StringComparison.OrdinalIgnoreCase)))
            {
                ConfigurationHierarchyNode zoneNode = GetZoneHierarchyNode(zone);
                bool zoneMatches = Matches(zone.Name);
                ConfigurationHierarchyNode[] channelNodes = zone.Channels
                    .Where(channel => normalized.Length == 0 || systemMatches || zoneMatches ||
                        Matches(channel.Name) || Matches(channel.Tgid) ||
                        Matches(ConfigurationProtocolCatalog.DisplayName(channel.Mode)))
                    .Select(channel => GetChannelHierarchyNode(zone, channel))
                    .ToArray();
                Replace(zoneNode.Children, channelNodes);
                zoneNode.Refresh();
                if (normalized.Length == 0 || systemMatches || zoneMatches || channelNodes.Length > 0)
                    visibleZones.Add(zoneNode);
            }

            Replace(systemNode.Children, visibleZones);
            systemNode.Refresh();
            if (normalized.Length == 0 || systemMatches || visibleZones.Count > 0)
                roots.Add(systemNode);
        }

        ZoneConfiguration[] unassignedZones = Configuration.Zones
            .Where(zone => string.IsNullOrWhiteSpace(GetDraftZoneSystemName(zone)) ||
                           !Configuration.Systems.Any(system => string.Equals(
                               system.Name,
                               GetDraftZoneSystemName(zone),
                               StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var visibleUnassigned = new List<ConfigurationHierarchyNode>();
        foreach (ZoneConfiguration zone in unassignedZones)
        {
            ConfigurationHierarchyNode zoneNode = GetZoneHierarchyNode(zone);
            bool zoneMatches = Matches(zone.Name);
            ConfigurationHierarchyNode[] channelNodes = zone.Channels
                .Where(channel => normalized.Length == 0 || zoneMatches || Matches(channel.Name) || Matches(channel.Tgid))
                .Select(channel => GetChannelHierarchyNode(zone, channel))
                .ToArray();
            Replace(zoneNode.Children, channelNodes);
            zoneNode.Refresh();
            if (normalized.Length == 0 || zoneMatches || channelNodes.Length > 0)
                visibleUnassigned.Add(zoneNode);
        }
        Replace(unassignedHierarchyNode.Children, visibleUnassigned);
        unassignedHierarchyNode.Refresh();
        if (visibleUnassigned.Count > 0)
            roots.Add(unassignedHierarchyNode);

        Replace(ConfigurationHierarchy, roots);
        ConfigurationHierarchyNode? currentNode = SelectedChannel is not null &&
                                                  channelHierarchyNodes.TryGetValue(SelectedChannel, out ConfigurationHierarchyNode? selectedChannelNode)
            ? selectedChannelNode
            : SelectedZone is not null && zoneHierarchyNodes.TryGetValue(SelectedZone, out ConfigurationHierarchyNode? selectedZoneNode)
                ? selectedZoneNode
                : SelectedSystem is not null && systemHierarchyNodes.TryGetValue(SelectedSystem, out ConfigurationHierarchyNode? selectedSystemNode)
                    ? selectedSystemNode
                    : null;
        if (!ReferenceEquals(selectedHierarchyNode, currentNode))
        {
            selectedHierarchyNode = currentNode;
            OnPropertyChanged(nameof(SelectedHierarchyNode));
        }
        if (SelectedZone is not null && zoneHierarchyNodes.TryGetValue(SelectedZone, out ConfigurationHierarchyNode? currentZoneNode))
        {
            currentZoneNode.IsExpanded = true;
            ConfigurationHierarchyNode? parentSystemNode = roots.FirstOrDefault(root => root.Children.Contains(currentZoneNode));
            if (parentSystemNode is not null)
                parentSystemNode.IsExpanded = true;
        }
    }

    private ConfigurationHierarchyNode GetZoneHierarchyNode(ZoneConfiguration zone)
    {
        if (!zoneHierarchyNodes.TryGetValue(zone, out ConfigurationHierarchyNode? node))
        {
            node = new ConfigurationHierarchyNode(zone.Name, zone: zone);
            zoneHierarchyNodes[zone] = node;
        }
        return node;
    }

    private ConfigurationHierarchyNode GetChannelHierarchyNode(
        ZoneConfiguration zone,
        ChannelConfiguration channel)
    {
        if (!channelHierarchyNodes.TryGetValue(channel, out ConfigurationHierarchyNode? node))
        {
            node = new ConfigurationHierarchyNode(channel.Name, zone: zone, channel: channel);
            channelHierarchyNodes[channel] = node;
        }
        node.Refresh();
        return node;
    }

    private ConfigurationStudioDraftSnapshot CaptureDraftSnapshot()
    {
        string yaml = document.IsReadOnly ? document.SourceText : document.Serialize();
        ConfigurationDraftIdentityLayout identityLayout = identities.Capture(Configuration);
        ConfigurationStudioReferencedFilesSnapshot referencedFiles = CaptureReferencedFilesSnapshot();
        Dictionary<Guid, WidgetPositionSetting> positions = draftWidgetPositions.ToDictionary(
            entry => identities.GetChannelId(entry.Key),
            entry => new WidgetPositionSetting { X = entry.Value.X, Y = entry.Value.Y });
        Dictionary<Guid, string> zoneSystems = draftZoneSystemNames.ToDictionary(
            entry => identities.GetZoneId(entry.Key),
            entry => entry.Value);
        Dictionary<string, IReadOnlyList<PatchMemberSetting>> memberships = stagedGroupMemberships.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<PatchMemberSetting>)entry.Value.Select(ClonePatchMember).ToArray(),
            StringComparer.OrdinalIgnoreCase);
        var modes = new Dictionary<string, bool>(stagedGroupModes, StringComparer.OrdinalIgnoreCase);

        var fingerprintComponents = new List<string>
        {
            yaml,
            string.Join(",", identityLayout.SystemIds),
            string.Join("|", identityLayout.Zones.Select(zone =>
                $"{zone.ZoneId}:{string.Join(',', zone.ChannelIds)}:{string.Join(',', zone.StreamIds)}")),
            string.Join(",", identityLayout.GroupIds),
            referencedFiles.KeyFileContent
        };
        fingerprintComponents.AddRange(referencedFiles.AliasContents
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => $"alias:{entry.Key}\n{entry.Value}"));
        fingerprintComponents.AddRange(positions
            .OrderBy(entry => entry.Key)
            .Select(entry => $"position:{entry.Key}:{entry.Value.X:R}:{entry.Value.Y:R}"));
        fingerprintComponents.AddRange(zoneSystems
            .OrderBy(entry => entry.Key)
            .Select(entry => $"zone-system:{entry.Key}:{entry.Value}"));
        fingerprintComponents.AddRange(memberships
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => $"group:{entry.Key}:" + string.Join("|", entry.Value.Select(member =>
                $"{member.SystemName}:{member.DestinationId}:{member.ChannelName}"))));
        fingerprintComponents.AddRange(modes
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => $"group-mode:{entry.Key}:{entry.Value}"));

        return new ConfigurationStudioDraftSnapshot(
            yaml,
            identityLayout,
            referencedFiles,
            positions,
            zoneSystems,
            memberships,
            modes,
            ConfigurationStudioDraftSnapshot.ComputeFingerprint(fingerprintComponents));
    }

    private ConfigurationStudioReferencedFilesSnapshot CaptureReferencedFilesSnapshot()
        => new(
            keyFilePath,
            keyFileHash,
            keyFileSnapshot,
            loadedKeyReference,
            keyFileLoadError,
            keyFileLoadIsWarning,
            KeyFileLoader.Serialize(keyContainer),
            aliasTables.ToDictionary(
                entry => entry.Key,
                entry => AliasFileLoader.Serialize(entry.Value),
                StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(aliasFileHashes, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(aliasFileSnapshots, StringComparer.OrdinalIgnoreCase),
            aliasLoadErrors.ToArray(),
            aliasLoadWarnings.ToArray(),
            loadedAliasReference);

    private static string GetPreviewSignature(ChannelConfiguration channel)
        => string.Join('\u001F',
            channel.Name,
            channel.System,
            channel.Tgid,
            channel.Mode,
            channel.Slot.ToString(CultureInfo.InvariantCulture),
            channel.Algo ?? string.Empty,
            channel.KeyId ?? string.Empty,
            channel.CardSize ?? string.Empty,
            channel.ResourceColor ?? string.Empty,
            channel.RxOnly.ToString(CultureInfo.InvariantCulture),
            channel.SelectableEncryption.ToString(CultureInfo.InvariantCulture));

    private void CompleteDraftTransition(
        ConfigurationStudioDraftSnapshot before,
        bool markDocumentDirty = false)
    {
        ConfigurationStudioDraftSnapshot after = CaptureDraftSnapshot();
        history.Record(before, after);
        currentSnapshot = after;
        if (markDocumentDirty && !string.Equals(before.Yaml, after.Yaml, StringComparison.Ordinal))
            document.MarkDirty();
    }

    private void RestoreDraftSnapshot(ConfigurationStudioDraftSnapshot snapshot)
    {
        Guid? selectedSystemId = selectedSystem is null ? null : identities.GetSystemId(selectedSystem);
        Guid? selectedZoneId = selectedZone is null ? null : identities.GetZoneId(selectedZone);
        Guid? selectedChannelId = selectedChannel is null ? null : identities.GetChannelId(selectedChannel);
        Guid? selectedStreamId = selectedStream is null ? null : identities.GetStreamId(selectedStream.Stream);
        Guid? selectedGroupId = selectedGroup is null ? null : identities.GetGroupId(selectedGroup);
        string? sourcePath = document.SourcePath;
        document = ConfigurationDocument.Parse(snapshot.Yaml, sourcePath);
        identities.Restore(document.Configuration, snapshot.IdentityLayout);
        RestoreReferencedFiles(snapshot.ReferencedFiles);

        draftWidgetPositions.Clear();
        foreach (KeyValuePair<Guid, WidgetPositionSetting> entry in snapshot.WidgetPositions)
        {
            if (identities.FindChannel(entry.Key) is { } channel)
                draftWidgetPositions[channel] = new WidgetPositionSetting { X = entry.Value.X, Y = entry.Value.Y };
        }

        draftZoneSystemNames.Clear();
        foreach (KeyValuePair<Guid, string> entry in snapshot.ZoneSystemAssignments)
        {
            if (identities.FindZone(entry.Key) is { } zone)
                draftZoneSystemNames[zone] = entry.Value;
        }

        stagedGroupMemberships.Clear();
        foreach (KeyValuePair<string, IReadOnlyList<PatchMemberSetting>> entry in snapshot.StagedGroupMemberships)
            stagedGroupMemberships[entry.Key] = entry.Value.Select(ClonePatchMember).ToList();
        stagedGroupModes.Clear();
        foreach (KeyValuePair<string, bool> entry in snapshot.StagedGroupModes)
            stagedGroupModes[entry.Key] = entry.Value;

        lastSystemRenameTargets.Clear();
        foreach (OriginalSystemIdentity original in migrationPlanner.OriginalSystems)
        {
            lastSystemRenameTargets[original.Id] = migrationPlanner.FindCurrentSystem(original)?.Name
                ?? original.Name;
        }

        previewCache.Clear();
        currentSnapshot = CaptureDraftSnapshot();
        if (!string.Equals(currentSnapshot.Fingerprint, savedFingerprint, StringComparison.Ordinal))
            document.MarkDirty();
        RefreshCollections();
        SelectedSystem = selectedSystemId is Guid systemId
            ? identities.FindSystem(systemId) ?? SelectedSystem
            : SelectedSystem;
        SelectedZone = selectedZoneId is Guid zoneId
            ? identities.FindZone(zoneId) ?? SelectedZone
            : SelectedZone;
        SelectedChannel = selectedChannelId is Guid channelId
            ? identities.FindChannel(channelId) ?? SelectedChannel
            : SelectedChannel;
        if (selectedStreamId is Guid streamId && identities.FindStream(streamId) is { } stream)
            SelectedStream = Streams.FirstOrDefault(row => ReferenceEquals(row.Stream, stream));
        SelectedGroup = selectedGroupId is Guid groupId
            ? identities.FindGroup(groupId) ?? SelectedGroup
            : SelectedGroup;
        OnPropertyChanged(nameof(Document));
        OnPropertyChanged(nameof(Configuration));
    }

    private void RestoreReferencedFiles(ConfigurationStudioReferencedFilesSnapshot snapshot)
    {
        keyFilePath = snapshot.KeyFilePath;
        keyFileHash = snapshot.KeyFileHash;
        keyFileSnapshot = snapshot.KeyFileBaseline;
        loadedKeyReference = snapshot.LoadedKeyReference;
        keyFileLoadError = snapshot.KeyFileLoadError;
        keyFileLoadIsWarning = snapshot.KeyFileLoadIsWarning;
        keyContainer = KeyFileLoader.Parse(snapshot.KeyFileContent);
        Replace(KeyEntries, keyContainer.Keys);
        SelectedKey = KeyEntries.FirstOrDefault();

        aliasTables.Clear();
        foreach (KeyValuePair<string, string> entry in snapshot.AliasContents)
            aliasTables[entry.Key] = AliasFileLoader.Parse(entry.Value);
        aliasFileHashes.Clear();
        foreach (KeyValuePair<string, string> entry in snapshot.AliasFileHashes)
            aliasFileHashes[entry.Key] = entry.Value;
        aliasFileSnapshots.Clear();
        foreach (KeyValuePair<string, string> entry in snapshot.AliasFileBaselines)
            aliasFileSnapshots[entry.Key] = entry.Value;
        aliasLoadErrors.Clear();
        aliasLoadErrors.AddRange(snapshot.AliasLoadErrors);
        aliasLoadWarnings.Clear();
        aliasLoadWarnings.AddRange(snapshot.AliasLoadWarnings);
        loadedAliasReference = snapshot.LoadedAliasReference;
        Aliases.Clear();
        foreach (KeyValuePair<string, List<RadioAlias>> table in aliasTables)
            foreach (RadioAlias alias in table.Value)
                Aliases.Add(new ConfigurationAliasRow(table.Key, alias));
        SelectedAlias = Aliases.FirstOrDefault();
    }

    private static PatchMemberSetting ClonePatchMember(PatchMemberSetting member)
        => new()
        {
            SystemName = member.SystemName,
            DestinationId = member.DestinationId,
            ChannelName = member.ChannelName
        };

    private void InitializeDraftWidgetPositions()
    {
        foreach (ZoneConfiguration zone in Configuration.Zones)
        {
            double x = 0;
            double y = 0;
            foreach (ChannelConfiguration channel in zone.Channels)
            {
                double width = ChannelViewModel.ResolveCardWidth(channel.CardSize);
                if (x > 0 && x + width > MainWindowViewModel.DefaultWidgetCanvasWidth)
                {
                    x = 0;
                    y += runtimeViewModel.ChannelCardHeight + MainWindowViewModel.ChannelWidgetSpacing;
                }

                if (!draftWidgetPositions.ContainsKey(channel))
                {
                    string key = GetChannelSettingsKey(channel);
                    draftWidgetPositions[channel] = originalWidgetPositions.TryGetValue(key, out WidgetPositionSetting? saved)
                        ? new WidgetPositionSetting { X = saved.X, Y = saved.Y }
                        : new WidgetPositionSetting { X = x, Y = y };
                }

                x += width + MainWindowViewModel.ChannelWidgetSpacing;
            }
        }
    }

    private void SynchronizeDraftWidgetPositions()
    {
        HashSet<ChannelConfiguration> currentChannels = Configuration.Zones
            .SelectMany(zone => zone.Channels)
            .ToHashSet();
        foreach (ChannelConfiguration removedChannel in draftWidgetPositions.Keys
                     .Where(channel => !currentChannels.Contains(channel))
                     .ToArray())
        {
            draftWidgetPositions.Remove(removedChannel);
        }
        InitializeDraftWidgetPositions();
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
                     nameof(IsGroups), nameof(IsEncryptionKeys), nameof(IsFiles)
                 })
            OnPropertyChanged(name);
    }

    private void NotifyDocumentState()
    {
        foreach (string name in new[]
                 {
                     nameof(IsDirty), nameof(StatusText), nameof(CanUndo), nameof(CanRedo),
                     nameof(ConfigurationShapeText), nameof(UnknownFieldsText), nameof(LayoutChanged),
                     nameof(ValidationStatusText), nameof(ValidationDrawerHeading), nameof(ValidationIndicatorBrush),
                     nameof(HasValidationIssues), nameof(HasWarnings), nameof(SelectedZoneHeading),
                     nameof(SystemNavigationHeading),
                     nameof(StreamNavigationHeading), nameof(GroupNavigationHeading),
                     nameof(KeyNavigationHeading), nameof(FileNavigationHeading),
                     nameof(PreviewCanvasWidth), nameof(PreviewCanvasHeight)
                 })
            OnPropertyChanged(name);
    }

    private static ChannelConfiguration CloneChannel(ChannelConfiguration source)
        => new()
        {
            Name = source.Name,
            System = source.System,
            Tgid = source.Tgid,
            Slot = source.Slot,
            Algo = source.Algo,
            KeyId = source.KeyId,
            Mode = source.Mode,
            ResourceColor = source.ResourceColor,
            RxOnly = source.RxOnly,
            SelectableEncryption = source.SelectableEncryption,
            CardSize = source.CardSize
        };

    private static WebStreamConfiguration CloneStream(WebStreamConfiguration source)
        => new()
        {
            Name = source.Name,
            Url = source.Url,
            AuthUsername = source.AuthUsername,
            AuthPassword = source.AuthPassword,
            IdleColor = source.IdleColor
        };

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
