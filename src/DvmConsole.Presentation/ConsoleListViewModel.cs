// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using DvmConsole.Application;

namespace DvmConsole.Presentation;

public sealed class ConsoleListViewModel : IAsyncDisposable
{
    private readonly IConsoleApplicationSession session;
    private readonly ChannelPttController ptt;
    private readonly Dictionary<ChannelId, ChannelListItemViewModel> itemsById = [];
    private readonly Dictionary<ChannelId, ConsoleListGroupViewModel[]> channelGroups = [];
    private readonly object presentationSync = new();
    private Dictionary<ChannelId, ChannelMeterSample> pendingMeters = [];
    private Dictionary<ChannelId, ChannelMeterSample> drainingMeters = [];
    private HashSet<ChannelId> pendingChannels = [];
    private HashSet<ChannelId> drainingChannels = [];
    private bool snapshotDispatchScheduled;
    private bool meterDispatchScheduled;
    private int disposed;
    private int presentationActive = 1;

    public ConsoleListViewModel(IConsoleApplicationSession session, ChannelPttController ptt)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.ptt = ptt ?? throw new ArgumentNullException(nameof(ptt));
        Items = new ObservableCollection<ChannelListItemViewModel>();
        BuildItems(session.Topology, session.Snapshot);
        BuildGroups(session.Topology);
        session.SnapshotChanged += HandleSnapshotChanged;
        session.MeterSampled += HandleMeterSampled;
    }

    public ObservableCollection<ChannelListItemViewModel> Items { get; }
    public ObservableCollection<IConsoleListRow> VisibleRows { get; } = [];

    private void BuildGroups(ConsoleTopologySnapshot topology)
    {
        var systemNames = topology.Systems.ToDictionary(system => system.Id, system => system.Name);
        var zoneNames = topology.Zones.ToDictionary(zone => zone.Id, zone => zone.Name);
        bool multipleSystems = Items.Select(item => item.Descriptor.SystemId).Distinct().Skip(1).Any();
        foreach (var system in Items.GroupBy(item => item.Descriptor.SystemId))
        {
            var groupedZones = system.GroupBy(item => item.Descriptor.ZoneId).ToArray();
            IConsoleListRow[] zones = groupedZones.Length == 1 ? system.Cast<IConsoleListRow>().ToArray() : groupedZones
                .Select(zone => new ConsoleListGroupViewModel(
                    zoneNames.GetValueOrDefault(zone.Key, "Other zone"), false, zone.ToArray()))
                .ToArray();
            var group = new ConsoleListGroupViewModel(
                systemNames.GetValueOrDefault(system.Key, "Other system"), true, zones)
            { SystemId = system.Key, ShowReceiveActivity = multipleSystems || groupedZones.Length > 1 };
            foreach (var item in system)
            {
                var zone = zones.OfType<ConsoleListGroupViewModel>().FirstOrDefault(zone => zone.Contains(item));
                ConsoleListGroupViewModel[] ancestors = zone is null ? [group] : [group, zone];
                channelGroups.Add(item.Id, ancestors);
                foreach (var ancestor in ancestors)
                    ancestor.UpdateReceiveActivity(item.Id, item.Snapshot.ReceiveEnabled && item.Snapshot.ReceiveActive);
            }
            VisibleRows.Add(group);
            foreach (IConsoleListRow row in group.VisibleDescendants())
                VisibleRows.Add(row);
        }
    }

    public void RestoreOrder(IReadOnlyList<string> order)
    {
        var ranks = order.Select((id, index) => (id, index)).GroupBy(pair => pair.id)
            .ToDictionary(group => group.Key, group => group.First().index);
        // Only rank peers within a zone; never move channels between systems or zones.
        var desired = Items.GroupBy(item => (item.Descriptor.SystemId, item.Descriptor.ZoneId))
            .SelectMany(zone => zone.OrderBy(item => ranks.GetValueOrDefault(item.Id.ToString(), int.MaxValue))).ToArray();
        for (int i = 0; i < desired.Length; i++)
            if (!ReferenceEquals(Items[i], desired[i])) Items.Move(Items.IndexOf(desired[i]), i);
        var effectiveRanks = Items.Select((item, index) => (item, index)).ToDictionary(pair => pair.item.Id.ToString(), pair => pair.index);
        var roots = channelGroups.Values.Select(groups => groups[0]).Distinct().ToArray();
        foreach (var group in roots) group.RestoreOrder(effectiveRanks);
        var visible = roots.SelectMany(group => new IConsoleListRow[] { group }.Concat(group.VisibleDescendants())).ToArray();
        for (int i = 0; i < visible.Length; i++)
            if (!ReferenceEquals(VisibleRows[i], visible[i])) VisibleRows.Move(VisibleRows.IndexOf(visible[i]), i);
    }

    public bool MoveWithinZone(ChannelId source, ChannelId target)
    {
        if (source == target || !itemsById.TryGetValue(source, out var from) ||
            !itemsById.TryGetValue(target, out var to) ||
            from.Descriptor.SystemId != to.Descriptor.SystemId || from.Descriptor.ZoneId != to.Descriptor.ZoneId)
            return false;
        var order = Items.Select(item => item.Id.ToString()).ToList();
        int destination = order.IndexOf(target.ToString());
        order.Remove(source.ToString());
        order.Insert(destination, source.ToString());
        RestoreOrder(order);
        return true;
    }

    public ChannelListItemViewModel? RevealChannel(ChannelId id)
    {
        if (!itemsById.TryGetValue(id, out var item)) return null;
        // Expand only the ancestors on this channel's path, preserving all other view state.
        for (int index = 0; index < VisibleRows.Count; index++)
            if (VisibleRows[index] is ConsoleListGroupViewModel group &&
                !group.IsExpanded && group.Contains(item)) ToggleGroup(group);
        return item;
    }

    public void ToggleGroup(ConsoleListGroupViewModel group)
    {
        int index = VisibleRows.IndexOf(group);
        if (index < 0) return;
        // Keep channel objects and the other rows intact: meters, channel expansion,
        // and the virtualizing panel's anchors survive group navigation.
        if (group.IsExpanded)
        {
            int count = group.VisibleDescendants().Count();
            for (int removed = 0; removed < count; removed++)
                VisibleRows.RemoveAt(index + 1);
            group.SetExpanded(false);
        }
        else
        {
            group.SetExpanded(true);
            foreach (IConsoleListRow row in group.VisibleDescendants())
                VisibleRows.Insert(++index, row);
        }
    }

    public ValueTask PressPttAsync(ChannelId channelId, CancellationToken cancellationToken = default)
        => ptt.PressAsync(channelId, cancellationToken);

    public ValueTask ReleasePttAsync(ChannelId channelId, CancellationToken cancellationToken = default)
        => ptt.ReleaseAsync(channelId, cancellationToken);

    public ValueTask TogglePttAsync(ChannelId channelId, CancellationToken cancellationToken = default)
        => ptt.ToggleAsync(channelId, cancellationToken);

    public ValueTask UnkeyPttAsync(ChannelId channelId, CancellationToken cancellationToken = default)
        => ptt.UnkeyAsync(channelId, cancellationToken);

    public ValueTask ReleaseAllPttAsync(CancellationToken cancellationToken = default)
        => ptt.ReleaseAllAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        session.SnapshotChanged -= HandleSnapshotChanged;
        session.MeterSampled -= HandleMeterSampled;
        lock (presentationSync)
        {
            pendingMeters.Clear();
            pendingChannels.Clear();
            meterDispatchScheduled = false;
        }
        await ptt.ReleaseAllAsync(CancellationToken.None);
    }

    public void SetPresentationActive(bool active)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            throw new InvalidOperationException("Presentation activation belongs to the UI dispatcher.");
        if (Interlocked.Exchange(ref presentationActive, active ? 1 : 0) == (active ? 1 : 0))
            return;
        lock (presentationSync)
        {
            pendingMeters.Clear();
            pendingChannels.Clear();
        }
        if (active && Volatile.Read(ref disposed) == 0)
            ApplySnapshot(session.Snapshot);
    }

    private void HandleSnapshotChanged(object? sender, ConsoleSnapshotChangedEventArgs args)
    {
        lock (presentationSync)
        {
            if (Volatile.Read(ref presentationActive) == 0 || Volatile.Read(ref disposed) != 0)
                return;
            pendingChannels.UnionWith(args.ChangedChannels ?? (IEnumerable<ChannelId>)itemsById.Keys);
            if (pendingChannels.Count == 0 || snapshotDispatchScheduled)
                return;
            snapshotDispatchScheduled = true;
        }
        RunOnUiThread(DrainPendingSnapshots);
    }

    private void DrainPendingSnapshots()
    {
        lock (presentationSync)
        {
            if (Volatile.Read(ref presentationActive) == 0 || Volatile.Read(ref disposed) != 0)
            {
                pendingChannels.Clear();
                snapshotDispatchScheduled = false;
                return;
            }
            (pendingChannels, drainingChannels) = (drainingChannels, pendingChannels);
        }
        // Read the authoritative latest snapshot, not an obsolete event payload.
        // Unioning changed IDs retains updates from every coalesced notification.
        try { ApplySnapshot(session.Snapshot, drainingChannels); }
        finally
        {
            drainingChannels.Clear();
            lock (presentationSync)
            {
                snapshotDispatchScheduled = pendingChannels.Count > 0 &&
                    Volatile.Read(ref presentationActive) != 0 && Volatile.Read(ref disposed) == 0;
                if (snapshotDispatchScheduled)
                    Dispatcher.UIThread.Post(DrainPendingSnapshots);
            }
        }
    }

    private void HandleMeterSampled(object? sender, ChannelMeterSample sample)
    {
        lock (presentationSync)
        {
            if (Volatile.Read(ref disposed) != 0 || Volatile.Read(ref presentationActive) == 0)
                return;
            pendingMeters[sample.ChannelId] = sample;
            if (meterDispatchScheduled)
                return;
            meterDispatchScheduled = true;
        }

        Dispatcher.UIThread.Post(DrainPendingMeters);
    }

    private void DrainPendingMeters()
    {
        lock (presentationSync)
        {
            if (Volatile.Read(ref disposed) != 0 || Volatile.Read(ref presentationActive) == 0)
            {
                pendingMeters.Clear();
                meterDispatchScheduled = false;
                return;
            }

            (pendingMeters, drainingMeters) = (drainingMeters, pendingMeters);
            meterDispatchScheduled = false;
        }

        try
        {
            foreach (ChannelMeterSample sample in drainingMeters.Values)
            {
                if (itemsById.TryGetValue(sample.ChannelId, out ChannelListItemViewModel? item))
                    item.ApplyMeter(sample);
            }
        }
        finally { drainingMeters.Clear(); }
    }

    private void BuildItems(ConsoleTopologySnapshot topology, ConsoleRuntimeSnapshot snapshot)
    {
        var systemNames = topology.Systems.ToDictionary(system => system.Id, system => system.Name);
        var zoneNames = topology.Zones.ToDictionary(zone => zone.Id, zone => zone.Name);
        var added = new HashSet<ChannelId>();

        foreach (SystemDescriptor system in topology.Systems)
        {
            bool firstInSystem = true;
            foreach (ZoneDescriptor zone in topology.Zones)
            {
                ChannelDescriptor[] channels = topology.Channels
                    .Where(channel => channel.SystemId == system.Id && channel.ZoneId == zone.Id)
                    .ToArray();
                if (channels.Length == 0)
                    continue;

                for (int index = 0; index < channels.Length; index++)
                {
                    AddItem(
                        channels[index],
                        snapshot,
                        firstInSystem && index == 0 ? system.Name : null,
                        index == 0 ? zone.Name : null);
                    added.Add(channels[index].Id);
                }
                firstInSystem = false;
            }
        }

        // Keep malformed or partially migrated topologies operator-visible.
        foreach (ChannelDescriptor descriptor in topology.Channels.Where(channel => !added.Contains(channel.Id)))
        {
            AddItem(
                descriptor,
                snapshot,
                systemNames.GetValueOrDefault(descriptor.SystemId, "Other system"),
                zoneNames.GetValueOrDefault(descriptor.ZoneId, "Other zone"));
        }
    }

    private void AddItem(
        ChannelDescriptor descriptor,
        ConsoleRuntimeSnapshot snapshot,
        string? systemHeading,
        string? zoneHeading)
    {
        snapshot.Channels.TryGetValue(descriptor.Id, out ChannelControlSnapshot? state);
        var item = new ChannelListItemViewModel(
            descriptor,
            session.Commands,
            state,
            systemHeading,
            zoneHeading);
        itemsById.Add(descriptor.Id, item);
        Items.Add(item);
    }

    private void ApplySnapshot(ConsoleRuntimeSnapshot snapshot, IReadOnlyCollection<ChannelId>? changed = null)
    {
        foreach (ChannelId id in changed ?? (IEnumerable<ChannelId>)itemsById.Keys)
        {
            if (itemsById.TryGetValue(id, out ChannelListItemViewModel? item) &&
                snapshot.Channels.TryGetValue(id, out ChannelControlSnapshot? state))
            {
                bool previous = item.Snapshot.ReceiveEnabled && item.Snapshot.ReceiveActive;
                bool receiving = state.ReceiveEnabled && state.ReceiveActive;
                item.ApplyState(state);
                if (previous != receiving)
                    foreach (var group in channelGroups[id]) group.UpdateReceiveActivity(item.Id, receiving);
            }
        }
    }

    private static void RunOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }
}

public sealed class ChannelListItemViewModel : INotifyPropertyChanged, IConsoleListRow
{
    private readonly IConsoleCommands commands;
    private ChannelControlSnapshot state;
    private bool isExpanded;
    private double meterRms;
    private double meterPeak;

    public ChannelListItemViewModel(
        ChannelDescriptor descriptor,
        IConsoleCommands commands,
        ChannelControlSnapshot? initialState,
        string? systemHeading = null,
        string? zoneHeading = null)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        this.commands = commands ?? throw new ArgumentNullException(nameof(commands));
        state = initialState ?? EmptyState(descriptor.Id);
        SystemHeading = systemHeading;
        ZoneHeading = zoneHeading;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    internal event EventHandler? SnapshotApplied;
    internal ChannelControlSnapshot Snapshot => state;
    public ChannelDescriptor Descriptor { get; }
    public string? SystemHeading { get; }
    public string? ZoneHeading { get; }
    public bool ShowsSystemHeading => !string.IsNullOrWhiteSpace(SystemHeading);
    public bool ShowsZoneHeading => !string.IsNullOrWhiteSpace(ZoneHeading);
    public ChannelId Id => Descriptor.Id;
    public string Name => Descriptor.Name;
    public string TalkgroupText => $"TG {Descriptor.DestinationId}";
    public string ProtocolText => Descriptor.Protocol.Equals("Dmr", StringComparison.OrdinalIgnoreCase)
        ? $"DMR TS{Descriptor.Slot + 1}"
        : Descriptor.Protocol.ToUpperInvariant();
    public string StateText => UseTouchText && string.Equals(state.StateText, "Idle", StringComparison.OrdinalIgnoreCase)
        ? string.Empty : state.StateText;
    public bool UseTouchText { get; set; }
    public string ChannelMetadataText => $"{TalkgroupText} · {ProtocolText}";
    public string LastCallerText => UseTouchText
        ? FormatTouchCaller(state.LastCaller, state.LastCallerSourceId ?? state.ReceiveSourceId)
        : state.LastCaller.Length == 0 ? "Last: --" : $"Last: {state.LastCaller}";

    // Keep transient mute status in the existing single-line summary on touch
    // layouts. Adding a details row during PTT changes virtualized row heights.
    public string CallerDetailText => UseTouchText && IsMuteStatusVisible
        ? $"{LastCallerText} · {MuteText}" : LastCallerText;
    public bool ShowMuteDetails => !UseTouchText && IsMuteStatusVisible;

    public static string FormatTouchCaller(string alias, uint? source)
    {
        if (source is not uint id) return string.IsNullOrWhiteSpace(alias) || alias == "--" ? "—" : alias;
        string rid = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(alias) || alias == "--" || alias == rid
            ? $"RID {rid}" : $"{alias} · RID {rid}";
    }
    public bool ReceiveEnabled => state.ReceiveEnabled;
    public bool ReceiveActive => state.ReceiveActive;
    public string ReceiveButtonText => !UseTouchText && state.ReceiveEnabled ? "RX ON" : "RX";
    public bool IsTransmitting => state.Transmitting;
    public bool IsPttEnabled => state.Transmitting ||
        (CanSelectTransmitTargets &&
         (Descriptor.AllowsTransmitDuringReceive || !state.ReceiveActive));
    public bool CanSelectTransmitTargets => state.Authority != TargetAuthorityState.Unavailable &&
        !Descriptor.ReceiveOnly &&
        (!state.SelectedTransmitEncrypted || state.TransmitKeyAvailable);
    public string PttText => state.Transmitting ? "Release" : "PTT";
    public bool IsTransmitSelected => state.TransmitSelected;
    public bool IsPageSelected => state.PageSelected;
    public bool IsAlertSelected => state.AlertSelected;
    public bool IsTransmitEncrypted => state.SelectedTransmitEncrypted;
    public bool CanToggleEncryption =>
        state.TransmitEncryptionConfigured &&
        state.TransmitEncryptionSelectable &&
        !state.Transmitting &&
        (state.SelectedTransmitEncrypted || state.TransmitKeyAvailable);
    public bool IsExpanded => isExpanded;
    public AudioMeterState Meter => new(meterRms, meterPeak);
    public double MeterRmsWidth => Math.Clamp(meterRms, 0, 100);
    public double MeterPeakX => Math.Clamp(meterPeak, 0, 100) - 1;
    public bool IsMeterPeakVisible => meterPeak > 0;
    public string VolumeText => $"Volume {state.Gain:0.00}×";
    public double VolumeSliderValue => NeutralSliderMath.VolumeGainToPosition(state.Gain);
    public string ReceiveEncryptionText => state.ObservedReceiveEncrypted ? "RX secure" : string.Empty;
    public bool HasReceiveEncryptionObservation => state.ObservedReceiveEncrypted;
    public string TransmitEncryptionText
    {
        get
        {
            string policy = state.TransmitEncryptionConfigured && state.TransmitEncryptionSelectable
                ? "selected"
                : "fixed";
            if (!state.SelectedTransmitEncrypted)
                return $"TX encryption: {policy} clear";
            return state.TransmitKeyAvailable
                ? $"TX encryption: {policy} secure · key available"
                : $"TX encryption: {policy} secure · key unavailable";
        }
    }
    public string AuthorityText => state.Authority switch
    {
        TargetAuthorityState.Unavailable => $"Unavailable: {state.AuthorityReason}",
        TargetAuthorityState.Pending => "Authority pending",
        _ => "Authority available"
    };
    public bool HasAuthorityFailure => state.Authority == TargetAuthorityState.Unavailable;
    public bool CanRecord => commands is IConsoleRecordingCommands recordings && recordings.CanRecord(Id);
    public bool IsRecordingEnabled => state.TarArmed;
    public bool HasRecordingFault => state.RecordingFault is not null;
    public string TarText => state.RecordingFault is not null
        ? $"TAR fault: {state.RecordingFault}"
        : state.RecordingFinalizing
            ? "TAR finalizing"
            : state.Recording
                ? "TAR recording"
                : state.TarArmed ? "TAR armed" : "TAR off";
    public string PlaybackText => state.RecordingPlayback
        ? "Recording playback active"
        : "Recording playback idle";
    public bool IsPlaybackStatusVisible => state.RecordingPlayback;
    public string RouteText => string.IsNullOrWhiteSpace(state.OutputRoute)
        ? "Output route: default"
        : $"Output route: {state.OutputRoute}";
    public string MuteText => state.EffectiveMuteReason is null
        ? "Mute: none"
        : $"Muted by {state.EffectiveMuteReason}";
    public bool IsMuteStatusVisible => state.EffectiveMuteReason is not null;
    public string PatchText => state.Patches.Count == 0
        ? "Patch: none"
        : string.Join(" · ", state.Patches.Select(patch => patch.IsOneWay
            ? $"{patch.Name}: {(patch.IsSource ? "source →" : "destination")}"
            : $"{patch.Name}: {(patch.IsEnabled ? "active" : "off")}"));
    public bool IsPatchStatusVisible => state.Patches.Count > 0;
    public bool IsTarStatusVisible => state.TarArmed || state.Recording ||
        state.RecordingFinalizing || state.RecordingFault is not null;
    public string DiagnosticText => (state.Fault, state.PendingOperation) switch
    {
        ({ Length: > 0 } fault, { Length: > 0 } pending) => $"{fault} · {pending}",
        ({ Length: > 0 } fault, _) => fault,
        (_, { Length: > 0 } pending) => pending,
        _ => string.Empty
    };
    public bool HasDiagnostic => DiagnosticText.Length > 0;

    public void ToggleExpansion()
    {
        isExpanded = !isExpanded;
        OnPropertyChanged(nameof(IsExpanded));
    }

    public ValueTask ToggleReceiveAsync(CancellationToken cancellationToken = default)
        => commands.SetReceiveEnabledAsync(Id, !state.ReceiveEnabled, cancellationToken);

    public ValueTask ToggleTransmitSelectionAsync(CancellationToken cancellationToken = default)
        => commands.SetTransmitSelectedAsync(Id, !state.TransmitSelected, cancellationToken);

    public ValueTask TogglePageSelectionAsync(CancellationToken cancellationToken = default)
        => commands.SetPageSelectedAsync(Id, !state.PageSelected, cancellationToken);

    public ValueTask ToggleAlertSelectionAsync(CancellationToken cancellationToken = default)
        => commands.SetAlertSelectedAsync(Id, !state.AlertSelected, cancellationToken);

    public ValueTask ToggleRecordingAsync(CancellationToken cancellationToken = default)
        => commands is IConsoleRecordingCommands recordings
            ? recordings.SetRecordingEnabledAsync(Id, !IsRecordingEnabled, cancellationToken)
            : ValueTask.CompletedTask;

    public ValueTask ToggleTransmitEncryptionAsync(CancellationToken cancellationToken = default)
        => commands.SetTransmitEncryptedAsync(Id, !state.SelectedTransmitEncrypted, cancellationToken);

    public ValueTask SetVolumeSliderValueAsync(
        double position,
        CancellationToken cancellationToken = default)
        => commands.SetChannelGainAsync(
            Id,
            NeutralSliderMath.VolumePositionToGain(position),
            cancellationToken);

    internal void ApplyState(ChannelControlSnapshot replacement)
    {
        if (state.HasSameContent(replacement))
            return;

        ChannelControlSnapshot previous = state;
        state = replacement;
        if (previous.StateText != state.StateText)
            OnPropertyChanged(nameof(StateText));
        if (previous.LastCaller != state.LastCaller || previous.LastCallerSourceId != state.LastCallerSourceId || previous.ReceiveSourceId != state.ReceiveSourceId)
            OnPropertiesChanged(nameof(LastCallerText), nameof(CallerDetailText));
        if (previous.ReceiveEnabled != state.ReceiveEnabled)
            OnPropertiesChanged(nameof(ReceiveEnabled), nameof(ReceiveButtonText));
        if (previous.ReceiveActive != state.ReceiveActive)
            OnPropertyChanged(nameof(ReceiveActive));
        if (previous.Transmitting != state.Transmitting)
            OnPropertiesChanged(nameof(IsTransmitting), nameof(PttText));
        if (previous.Transmitting != state.Transmitting || previous.ReceiveActive != state.ReceiveActive ||
            previous.Authority != state.Authority || previous.SelectedTransmitEncrypted != state.SelectedTransmitEncrypted ||
            previous.TransmitKeyAvailable != state.TransmitKeyAvailable)
            OnPropertiesChanged(nameof(IsPttEnabled), nameof(CanSelectTransmitTargets));
        if (previous.TransmitSelected != state.TransmitSelected)
            OnPropertyChanged(nameof(IsTransmitSelected));
        if (previous.PageSelected != state.PageSelected)
            OnPropertyChanged(nameof(IsPageSelected));
        if (previous.AlertSelected != state.AlertSelected)
            OnPropertyChanged(nameof(IsAlertSelected));
        if (previous.SelectedTransmitEncrypted != state.SelectedTransmitEncrypted)
            OnPropertyChanged(nameof(IsTransmitEncrypted));
        if (previous.Transmitting != state.Transmitting ||
            previous.TransmitEncryptionConfigured != state.TransmitEncryptionConfigured ||
            previous.TransmitEncryptionSelectable != state.TransmitEncryptionSelectable ||
            previous.SelectedTransmitEncrypted != state.SelectedTransmitEncrypted ||
            previous.TransmitKeyAvailable != state.TransmitKeyAvailable)
            OnPropertiesChanged(nameof(CanToggleEncryption), nameof(TransmitEncryptionText));
        if (previous.Gain != state.Gain)
            OnPropertiesChanged(nameof(VolumeText), nameof(VolumeSliderValue));
        if (previous.ObservedReceiveEncrypted != state.ObservedReceiveEncrypted)
            OnPropertiesChanged(nameof(ReceiveEncryptionText), nameof(HasReceiveEncryptionObservation));
        if (previous.Authority != state.Authority || previous.AuthorityReason != state.AuthorityReason)
            OnPropertiesChanged(nameof(AuthorityText), nameof(HasAuthorityFailure));
        if (previous.Recording != state.Recording || previous.RecordingFinalizing != state.RecordingFinalizing ||
            previous.RecordingFault != state.RecordingFault || previous.TarArmed != state.TarArmed)
            OnPropertiesChanged(nameof(TarText), nameof(IsTarStatusVisible), nameof(IsRecordingEnabled), nameof(HasRecordingFault), nameof(CanRecord));
        if (previous.RecordingPlayback != state.RecordingPlayback)
            OnPropertiesChanged(nameof(PlaybackText), nameof(IsPlaybackStatusVisible));
        if (previous.OutputRoute != state.OutputRoute)
            OnPropertyChanged(nameof(RouteText));
        if (previous.EffectiveMuteReason != state.EffectiveMuteReason)
            OnPropertiesChanged(nameof(MuteText), nameof(IsMuteStatusVisible), nameof(ShowMuteDetails), nameof(CallerDetailText));
        if (!previous.Patches.SequenceEqual(state.Patches))
            OnPropertiesChanged(nameof(PatchText), nameof(IsPatchStatusVisible));
        if (previous.PendingOperation != state.PendingOperation || previous.Fault != state.Fault)
            OnPropertiesChanged(nameof(DiagnosticText), nameof(HasDiagnostic));
        SnapshotApplied?.Invoke(this, EventArgs.Empty);
    }

    internal void ApplyMeter(ChannelMeterSample sample)
    {
        if (Math.Abs(meterRms - sample.Rms) < 0.01 &&
            Math.Abs(meterPeak - sample.Peak) < 0.01)
        {
            return;
        }

        meterRms = sample.Rms;
        meterPeak = sample.Peak;
        OnPropertyChanged(nameof(Meter));
    }

    private static ChannelControlSnapshot EmptyState(ChannelId id)
        => new(
            id,
            DvmConsole.Core.Runtime.ChannelRuntimeState.Idle,
            "Idle",
            "--",
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            null,
            false,
            null,
            1,
            0,
            null,
            TargetAuthorityState.Pending,
            null,
            false,
            false,
            false,
            [],
            null,
            null);

    private void OnPropertiesChanged(params string[] names)
    {
        foreach (string name in names)
            OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
