using Avalonia.Media;
using DvmConsole.Core.Diagnostics;
using DvmConsole.Core.Runtime;
using DvmConsole.Core.Settings;
using DvmConsole.FneClient;
using DvmConsole.Media;
using System.ComponentModel;

namespace DvmConsole.Desktop;

public sealed class SystemViewModel : IFneTrafficEndpoint, INotifyPropertyChanged, IAsyncDisposable
{
    private readonly FneConnection connection;
    private readonly FneConnectionOptions options;
    private string connectionStatus = "Disconnected";
    private readonly object keyRequestSync = new();
    private readonly HashSet<(byte AlgorithmId, ushort KeyId)> requestedP25Keys = [];
    private readonly FneTrafficStatistics trafficStatistics = new();
    private readonly RxJitterBufferModeViewModel[] rxJitterBufferModes;
    private ReceiveJitterBufferTelemetry jitterBufferTelemetry;
    private bool restoringJitterBuffer;
    private long nonCallDmrTerminatorCount;
    private long droppedSystemTrafficCount;
    private int trafficDiagnosticsDirty;
    private bool verboseLoggingEnabled;
    private bool isSelected;
    private ZoneViewModel? selectedZone;

    public SystemViewModel(
        FneConnectionOptions options,
        string name,
        string endpoint,
        IEnumerable<ChannelViewModel>? channels = null,
        IEnumerable<ZoneViewModel>? zones = null,
        int accentIndex = 0)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        connection = new FneConnection(this.options);
        verboseLoggingEnabled = options.EnableVerboseLogging;
        Name = name;
        Endpoint = endpoint;
        Channels = channels?.ToArray() ?? [];
        Zones = zones?.ToArray() ?? [];
        rxJitterBufferModes = CreateJitterBufferModes(new RxJitterBufferSetting());
        foreach (RxJitterBufferModeViewModel mode in rxJitterBufferModes)
            mode.PropertyChanged += HandleJitterBufferModePropertyChanged;
        StatusAccentBrush = SystemAccentPalette.GetBrush(accentIndex);
        selectedZone = Zones.Count > 0 ? Zones[0] : null;
        foreach (ZoneViewModel zone in Zones)
            zone.SetReceiveActivityResolver(() =>
                zone.Channels.Any(channel => channel.IsReceivePresentationActive));
        foreach (ChannelViewModel channel in Channels)
        {
            channel.SetReceivePresentationOwnerResolver(() => Channels.FirstOrDefault(candidate =>
                SameResource(channel, candidate) && candidate.HasLocalReceivePresentation));
            channel.PropertyChanged += HandleChannelPropertyChanged;
        }
        connection.StatusChanged += HandleConnectionStatus;
        connection.LogReceived += HandleLogReceived;
        connection.TrafficReceived += HandleTrafficReceived;
        connection.KeyResponseReceived += HandleKeyResponse;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<FneConnectionStatus>? StatusChanged;
    public event EventHandler<FneLogEntry>? LogReceived;
    public event EventHandler<FneTrafficFrame>? TrafficReceived;
    public event EventHandler<FneKeyResponse>? KeyResponseReceived;
    internal event EventHandler? JitterBufferChanged;
    public string Name { get; }
    public string Endpoint { get; }
    public IReadOnlyList<ChannelViewModel> Channels { get; }
    public IReadOnlyList<ZoneViewModel> Zones { get; }
    public ZoneViewModel? SelectedZone
    {
        get => selectedZone;
        set
        {
            if (ReferenceEquals(selectedZone, value) || (value is not null && !Zones.Contains(value)))
                return;
            selectedZone = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedZone)));
        }
    }
    public uint? SourceId => options.SourceId;
    public string Identity => options.Identity;
    public bool IsConnected => connection.Status.State == FneConnectionState.Connected;
    public bool IsConnectionActive => connection.Status.State is not (FneConnectionState.Disconnected or FneConnectionState.Faulted);
    public bool IsSelected => isSelected;
    public bool IsReceiving => Channels.Any(channel => channel.IsReceivePresentationActive);
    public double ActivityBarOpacity => IsReceiving ? 1.0 : 0.12;
    public string RecordingConfigurationHeader
        => $"{Name} · {Channels.Count(channel => channel.IsRecordingEnabled)} of {Channels.Count} TAR enabled";
    public IBrush StatusAccentBrush { get; }
    public string StatusGlyph => IsConnected ? "●" : "○";
    public string ConnectionPillText => connection.Status.State.ToString().ToUpperInvariant();
    public string ConnectionActionText => IsConnectionActive ? $"Disconnect {Name}" : $"Start {Name}";
    public string ConnectionButtonText => IsConnectionActive ? "Disconnect" : "Connect";
    public IBrush ConnectionBrush => new SolidColorBrush(Color.Parse(connection.Status.State switch
    {
        FneConnectionState.Connected => "#00BE5A",
        FneConnectionState.Starting or
        FneConnectionState.WaitingForLogin or
        FneConnectionState.Authenticating or
        FneConnectionState.Configuring or
        FneConnectionState.Stopping => "#E5A93C",
        FneConnectionState.Faulted => "#E05252",
        _ => "#8794A1"
    }));
    public string SystemTabText => $"{Name} {(ConnectionStatus.StartsWith("Connected:", StringComparison.OrdinalIgnoreCase) ? "●" : "○")}";
    public string TrafficTotalsText => trafficStatistics.TotalsText;
    public string StreamTrafficText => trafficStatistics.StreamText;
    public string ConnectionHealthText
    {
        get
        {
            long nonCallTerminators = Interlocked.Read(ref nonCallDmrTerminatorCount);
            long backlogDrops = Interlocked.Read(ref droppedSystemTrafficCount);
            if (nonCallTerminators == 0 && backlogDrops == 0)
                return "Local receive health · no discarded control traffic or UI backlog drops";

            var details = new List<string>(2);
            if (nonCallTerminators > 0)
                details.Add($"non-call DMR terminators {nonCallTerminators:N0}");
            if (backlogDrops > 0)
                details.Add($"UI backlog drops {backlogDrops:N0}");
            return $"Local receive health · {string.Join(" · ", details)}";
        }
    }
    public IReadOnlyList<RxJitterBufferModeViewModel> RxJitterBufferModes => rxJitterBufferModes;
    public string JitterBufferSummaryText
        => $"Applied: P25 {rxJitterBufferModes[0].SummaryText} · " +
           $"DMR {rxJitterBufferModes[1].SummaryText} · " +
           $"NXDN {rxJitterBufferModes[2].SummaryText}";
    public string AdaptiveJitterLearnedText => jitterBufferTelemetry.LearnedText;
    public string JitterBufferEffectivenessText => jitterBufferTelemetry.EffectivenessText;
    public string ConnectionStatus
    {
        get => connectionStatus;
        private set
        {
            if (connectionStatus == value)
                return;
            connectionStatus = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionStatus)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsConnected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsConnectionActive)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionPillText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionActionText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionButtonText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SystemTabText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusGlyph)));
        }
    }

    internal void SetSelected(bool selected)
    {
        if (isSelected == selected)
            return;
        isSelected = selected;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ResetPacketDiagnostics();
        await connection.StartOrReconnectAsync(cancellationToken).ConfigureAwait(false);
    }
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await connection.StopAsync(cancellationToken).ConfigureAwait(false);
        lock (keyRequestSync)
            requestedP25Keys.Clear();
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);
        await StartAsync(cancellationToken).ConfigureAwait(false);
    }
    public uint CreateStreamId() => connection.CreateStreamId();
    public void SendTraffic(FneTrafficProtocol protocol, ReadOnlySpan<byte> payload, ushort packetSequence, uint streamId)
    {
        connection.SendTraffic(protocol, payload, packetSequence, streamId);
        trafficStatistics.ObserveSend(payload.Length);
        Volatile.Write(ref trafficDiagnosticsDirty, 1);
        if (!Volatile.Read(ref verboseLoggingEnabled))
            return;

        LogReceived?.Invoke(this, new FneLogEntry(
            Name,
            DebugLogSeverity.Debug,
            $"FNE TX {protocol.ToString().ToUpperInvariant()} vocoder packet; seq {packetSequence}, " +
            $"stream {streamId}, {payload.Length} bytes.",
            DateTimeOffset.Now));
    }

    internal void SetVerboseLogging(bool enabled)
    {
        Volatile.Write(ref verboseLoggingEnabled, enabled);
        connection.SetVerboseLogging(enabled);
    }

    public void RequestP25Key(byte algorithmId, ushort keyId)
    {
        lock (keyRequestSync)
        {
            if (!requestedP25Keys.Add((algorithmId, keyId)))
                return;
        }

        try
        {
            connection.RequestP25Key(algorithmId, keyId);
        }
        catch
        {
            lock (keyRequestSync)
                requestedP25Keys.Remove((algorithmId, keyId));
            throw;
        }
    }

    public void SendP25SubscriberCommand(P25SubscriberCommand command, uint destinationId)
        => connection.SendP25SubscriberCommand(command, destinationId);

    public void ApplyStatus(FneConnectionStatus status)
    {
        ConnectionStatus = $"{status.State}: {status.Message}";
    }

    internal void RecordTraffic(FneTrafficFrame traffic, bool publishDiagnostics = true)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        trafficStatistics.ObserveReceive(traffic);
        Volatile.Write(ref trafficDiagnosticsDirty, 1);
        if (publishDiagnostics)
            PublishTrafficDiagnostics();
    }

    internal void PublishTrafficDiagnostics()
    {
        if (Interlocked.Exchange(ref trafficDiagnosticsDirty, 0) == 0)
            return;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TrafficTotalsText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StreamTrafficText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionHealthText)));
    }

    internal void RecordNonCallDmrTerminator()
    {
        SaturatingAdd(ref nonCallDmrTerminatorCount, 1);
        Volatile.Write(ref trafficDiagnosticsDirty, 1);
    }

    internal void RecordDroppedSystemTraffic(long count)
    {
        if (count > 0)
        {
            SaturatingAdd(ref droppedSystemTrafficCount, count);
            Volatile.Write(ref trafficDiagnosticsDirty, 1);
        }
    }

    internal RxJitterBufferSetting GetConfiguredJitterBuffer()
    {
        var configured = new RxJitterBufferSetting();
        foreach (RxJitterBufferModeViewModel mode in rxJitterBufferModes)
        {
            switch (mode.Protocol)
            {
                case RxJitterBufferProtocol.P25:
                    configured.P25Milliseconds = mode.FixedMilliseconds;
                    configured.P25Adaptive = mode.IsAdaptive;
                    break;
                case RxJitterBufferProtocol.Dmr:
                    configured.DmrMilliseconds = mode.FixedMilliseconds;
                    configured.DmrAdaptive = mode.IsAdaptive;
                    break;
                case RxJitterBufferProtocol.Nxdn:
                    configured.NxdnMilliseconds = mode.FixedMilliseconds;
                    configured.NxdnAdaptive = mode.IsAdaptive;
                    break;
            }
        }
        return RxJitterBufferSetting.Normalize(configured);
    }

    internal void RestoreJitterBuffer(RxJitterBufferSetting settings)
    {
        RxJitterBufferSetting normalized = RxJitterBufferSetting.Normalize(settings);
        restoringJitterBuffer = true;
        try
        {
            foreach (RxJitterBufferModeViewModel mode in rxJitterBufferModes)
            {
                (int milliseconds, bool adaptive) = mode.Protocol switch
                {
                    RxJitterBufferProtocol.P25 => (normalized.P25Milliseconds, normalized.P25Adaptive),
                    RxJitterBufferProtocol.Dmr => (normalized.DmrMilliseconds, normalized.DmrAdaptive),
                    RxJitterBufferProtocol.Nxdn => (normalized.NxdnMilliseconds, normalized.NxdnAdaptive),
                    _ => throw new InvalidOperationException($"Unsupported RX jitter buffer protocol {mode.Protocol}.")
                };
                mode.Restore(milliseconds, adaptive);
            }
        }
        finally
        {
            restoringJitterBuffer = false;
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(JitterBufferSummaryText)));
    }

    internal void UpdateJitterBufferTelemetry(ReceiveJitterBufferTelemetry telemetry)
    {
        if (jitterBufferTelemetry == telemetry)
            return;

        jitterBufferTelemetry = telemetry;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AdaptiveJitterLearnedText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(JitterBufferEffectivenessText)));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (RxJitterBufferModeViewModel mode in rxJitterBufferModes)
            mode.PropertyChanged -= HandleJitterBufferModePropertyChanged;
        foreach (ChannelViewModel channel in Channels)
            channel.PropertyChanged -= HandleChannelPropertyChanged;
        connection.StatusChanged -= HandleConnectionStatus;
        connection.LogReceived -= HandleLogReceived;
        connection.TrafficReceived -= HandleTrafficReceived;
        connection.KeyResponseReceived -= HandleKeyResponse;
        await connection.DisposeAsync().ConfigureAwait(false);
    }

    private void HandleConnectionStatus(object? sender, FneConnectionStatus status)
    {
        if (status.State != FneConnectionState.Connected)
        {
            lock (keyRequestSync)
                requestedP25Keys.Clear();
        }
        StatusChanged?.Invoke(this, status);
    }

    private void HandleLogReceived(object? sender, FneLogEntry entry)
    {
        LogReceived?.Invoke(this, entry);
    }

    private void HandleTrafficReceived(object? sender, FneTrafficFrame traffic)
    {
        TrafficReceived?.Invoke(this, traffic);
    }

    private void ResetPacketDiagnostics()
    {
        trafficStatistics.Reset();
        Interlocked.Exchange(ref nonCallDmrTerminatorCount, 0);
        Interlocked.Exchange(ref droppedSystemTrafficCount, 0);
        Volatile.Write(ref trafficDiagnosticsDirty, 1);
        PublishTrafficDiagnostics();
    }

    private static RxJitterBufferModeViewModel[] CreateJitterBufferModes(
        RxJitterBufferSetting settings)
        =>
        [
            new RxJitterBufferModeViewModel(
                RxJitterBufferProtocol.P25,
                "P25",
                RxJitterBufferSetting.P25OptionsMilliseconds,
                settings.P25Milliseconds,
                settings.P25Adaptive,
                packetMilliseconds: 180,
                singularUnit: "LDU",
                pluralUnit: "LDUs"),
            new RxJitterBufferModeViewModel(
                RxJitterBufferProtocol.Dmr,
                "DMR",
                RxJitterBufferSetting.DmrOptionsMilliseconds,
                settings.DmrMilliseconds,
                settings.DmrAdaptive,
                packetMilliseconds: 60),
            new RxJitterBufferModeViewModel(
                RxJitterBufferProtocol.Nxdn,
                "NXDN",
                RxJitterBufferSetting.NxdnOptionsMilliseconds,
                settings.NxdnMilliseconds,
                settings.NxdnAdaptive,
                packetMilliseconds: 80)
        ];

    private static void SaturatingAdd(ref long target, long increment)
    {
        while (true)
        {
            long current = Interlocked.Read(ref target);
            long next = current > long.MaxValue - increment
                ? long.MaxValue
                : current + increment;
            if (Interlocked.CompareExchange(ref target, next, current) == current)
                return;
        }
    }

    private void HandleKeyResponse(object? sender, FneKeyResponse response)
    {
        KeyResponseReceived?.Invoke(this, response);
    }

    private void HandleJitterBufferModePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (restoringJitterBuffer || e.PropertyName != nameof(RxJitterBufferModeViewModel.SelectedOption))
            return;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(JitterBufferSummaryText)));
        JitterBufferChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HandleChannelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChannelViewModel.IsReceivePresentationActive))
            RefreshReceiveActivity();

        if (sender is ChannelViewModel changed &&
            e.PropertyName is nameof(ChannelViewModel.State) or
                nameof(ChannelViewModel.IsReceivePresentationActive))
        {
            foreach (ChannelViewModel channel in Channels.Where(candidate => SameResource(changed, candidate)))
                channel.RefreshReceivePresentation();
        }

        if (e.PropertyName == nameof(ChannelViewModel.IsRecordingEnabled))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecordingConfigurationHeader)));
        }
    }

    private void RefreshReceiveActivity()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReceiving)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActivityBarOpacity)));
        foreach (ZoneViewModel zone in Zones)
            zone.RefreshReceiveActivity();
    }

    private static bool SameResource(ChannelViewModel left, ChannelViewModel right)
        => ChannelReceiveIdentity.AreEquivalent(left, right);
}
