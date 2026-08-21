using Avalonia.Media;
using DvmConsole.Core.Diagnostics;
using DvmConsole.Core.Runtime;
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
    private long receivedPacketCount;
    private long receivedPacketBytes;
    private long sentPacketCount;
    private long sentPacketBytes;
    private long nonCallDmrTerminatorCount;
    private long droppedSystemTrafficCount;
    private string lastPacketText = "No media packets received.";
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
        Name = name;
        Endpoint = endpoint;
        Channels = channels?.ToArray() ?? [];
        Zones = zones?.ToArray() ?? [];
        StatusAccentBrush = SystemAccentPalette.GetBrush(accentIndex);
        selectedZone = Zones.FirstOrDefault();
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
    public string PacketDiagnosticsText
        => $"RX {receivedPacketCount:N0} packets / {receivedPacketBytes:N0} bytes · TX {sentPacketCount:N0} packets / {sentPacketBytes:N0} bytes" +
            (nonCallDmrTerminatorCount > 0
                ? $" · non-call DMR terminators {nonCallDmrTerminatorCount:N0}"
                : string.Empty) +
            (Interlocked.Read(ref droppedSystemTrafficCount) > 0
                ? $" · UI backlog drops {Interlocked.Read(ref droppedSystemTrafficCount):N0}"
                : string.Empty);
    public string LastPacketText => lastPacketText;
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
        sentPacketCount++;
        sentPacketBytes += payload.Length;
        LogReceived?.Invoke(this, new FneLogEntry(
            Name,
            DebugLogSeverity.Debug,
            $"FNE TX {protocol.ToString().ToUpperInvariant()} vocoder packet; seq {packetSequence}, " +
            $"stream {streamId}, {payload.Length} bytes.",
            DateTimeOffset.Now));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PacketDiagnosticsText)));
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
        receivedPacketCount++;
        receivedPacketBytes += traffic.Payload.Length;
        lastPacketText = $"{traffic.Protocol.ToString().ToUpperInvariant()} {traffic.CallType}/{traffic.FrameType} · seq {traffic.PacketSequence} · stream {traffic.StreamId} · {traffic.SourceId}→{traffic.DestinationId}";
        if (publishDiagnostics)
            PublishTrafficDiagnostics();
    }

    internal void PublishTrafficDiagnostics()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PacketDiagnosticsText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastPacketText)));
    }

    internal void RecordNonCallDmrTerminator()
    {
        nonCallDmrTerminatorCount++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PacketDiagnosticsText)));
    }

    internal void RecordDroppedSystemTraffic(long count)
    {
        if (count > 0)
            Interlocked.Add(ref droppedSystemTrafficCount, count);
    }

    public async ValueTask DisposeAsync()
    {
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
        receivedPacketCount = 0;
        receivedPacketBytes = 0;
        sentPacketCount = 0;
        sentPacketBytes = 0;
        nonCallDmrTerminatorCount = 0;
        Interlocked.Exchange(ref droppedSystemTrafficCount, 0);
        lastPacketText = "No media packets received.";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PacketDiagnosticsText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastPacketText)));
    }

    private void HandleKeyResponse(object? sender, FneKeyResponse response)
    {
        KeyResponseReceived?.Invoke(this, response);
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
        => left.Definition.SystemName.Equals(right.Definition.SystemName, StringComparison.OrdinalIgnoreCase) &&
           left.Definition.Mode.Equals(right.Definition.Mode, StringComparison.OrdinalIgnoreCase) &&
           left.Definition.DestinationId == right.Definition.DestinationId &&
           (left.Definition.Mode != "dmr" || left.Definition.Slot == right.Definition.Slot);
}
