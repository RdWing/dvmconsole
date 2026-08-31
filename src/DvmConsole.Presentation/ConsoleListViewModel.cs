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
    private readonly object meterSync = new();
    private readonly Dictionary<ChannelId, ChannelMeterSample> pendingMeters = [];
    private bool meterDispatchScheduled;
    private int disposed;

    public ConsoleListViewModel(IConsoleApplicationSession session, ChannelPttController ptt)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.ptt = ptt ?? throw new ArgumentNullException(nameof(ptt));
        Items = new ObservableCollection<ChannelListItemViewModel>();
        BuildItems(session.Topology, session.Snapshot);
        session.SnapshotChanged += HandleSnapshotChanged;
        session.MeterSampled += HandleMeterSampled;
    }

    public ObservableCollection<ChannelListItemViewModel> Items { get; }

    public ValueTask PressPttAsync(ChannelId channelId, CancellationToken cancellationToken = default)
        => ptt.PressAsync(channelId, cancellationToken);

    public ValueTask ReleasePttAsync(ChannelId channelId, CancellationToken cancellationToken = default)
        => ptt.ReleaseAsync(channelId, cancellationToken);

    public ValueTask TogglePttAsync(ChannelId channelId, CancellationToken cancellationToken = default)
        => ptt.ToggleAsync(channelId, cancellationToken);

    public ValueTask ReleaseAllPttAsync(CancellationToken cancellationToken = default)
        => ptt.ReleaseAllAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        session.SnapshotChanged -= HandleSnapshotChanged;
        session.MeterSampled -= HandleMeterSampled;
        lock (meterSync)
        {
            pendingMeters.Clear();
            meterDispatchScheduled = false;
        }
        await ptt.ReleaseAllAsync(CancellationToken.None);
    }

    private void HandleSnapshotChanged(object? sender, ConsoleSnapshotChangedEventArgs args)
        => RunOnUiThread(() => ApplySnapshot(args.Current));

    private void HandleMeterSampled(object? sender, ChannelMeterSample sample)
    {
        lock (meterSync)
        {
            if (Volatile.Read(ref disposed) != 0)
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
        ChannelMeterSample[] samples;
        lock (meterSync)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                pendingMeters.Clear();
                meterDispatchScheduled = false;
                return;
            }

            samples = pendingMeters.Values.ToArray();
            pendingMeters.Clear();
            meterDispatchScheduled = false;
        }

        foreach (ChannelMeterSample sample in samples)
        {
            if (itemsById.TryGetValue(sample.ChannelId, out ChannelListItemViewModel? item))
                item.ApplyMeter(sample);
        }
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

    private void ApplySnapshot(ConsoleRuntimeSnapshot snapshot)
    {
        foreach ((ChannelId id, ChannelListItemViewModel item) in itemsById)
        {
            if (snapshot.Channels.TryGetValue(id, out ChannelControlSnapshot? state))
                item.ApplyState(state);
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

public sealed class ChannelListItemViewModel : INotifyPropertyChanged
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
    public string StateText => state.StateText;
    public string LastCallerText => state.LastCaller.Length == 0 ? "Last: --" : $"Last: {state.LastCaller}";
    public bool ReceiveEnabled => state.ReceiveEnabled;
    public bool ReceiveActive => state.ReceiveActive;
    public string ReceiveButtonText => state.ReceiveEnabled ? "RX ON" : "RX";
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

        state = replacement;
        OnPropertiesChanged(
            nameof(StateText),
            nameof(LastCallerText),
            nameof(ReceiveEnabled),
            nameof(ReceiveActive),
            nameof(ReceiveButtonText),
            nameof(IsTransmitting),
            nameof(IsPttEnabled),
            nameof(CanSelectTransmitTargets),
            nameof(PttText),
            nameof(IsTransmitSelected),
            nameof(IsPageSelected),
            nameof(IsAlertSelected),
            nameof(IsTransmitEncrypted),
            nameof(CanToggleEncryption),
            nameof(VolumeText),
            nameof(VolumeSliderValue),
            nameof(ReceiveEncryptionText),
            nameof(HasReceiveEncryptionObservation),
            nameof(TransmitEncryptionText),
            nameof(AuthorityText),
            nameof(HasAuthorityFailure),
            nameof(TarText),
            nameof(IsTarStatusVisible),
            nameof(PlaybackText),
            nameof(IsPlaybackStatusVisible),
            nameof(RouteText),
            nameof(MuteText),
            nameof(IsMuteStatusVisible),
            nameof(PatchText),
            nameof(IsPatchStatusVisible),
            nameof(DiagnosticText),
            nameof(HasDiagnostic));
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
        OnPropertiesChanged(nameof(MeterRmsWidth), nameof(MeterPeakX), nameof(IsMeterPeakVisible));
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
