using Avalonia.Media;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Runtime;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Operations;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;

namespace DvmConsole.Desktop;

public sealed class ChannelViewModel : INotifyPropertyChanged
{
    private static readonly IBrush NormalPeakMarkerBrush =
        new SolidColorBrush(Color.Parse("#F5F7FA"));
    private static readonly IBrush YellowPeakMarkerBrush =
        new SolidColorBrush(Color.Parse("#F2B134"));
    private static readonly IBrush RedPeakMarkerBrush =
        new SolidColorBrush(Color.Parse("#E5484D"));

    private readonly ChannelConfiguration configuration;
    private readonly ChannelRuntime runtime;
    private readonly ChannelDefinition sessionDefinition;
    private readonly IP25KeyResolver? p25KeyResolver;
    private readonly IDmrKeyResolver? dmrKeyResolver;
    private readonly INxdnKeyResolver? nxdnKeyResolver;
    private readonly RadioAliasIndex aliases;
    private ImmutableHashSet<uint> projectedReceiveStreams = ImmutableHashSet<uint>.Empty;
    private Func<ChannelViewModel, Task>? startAudio;
    private Func<ChannelViewModel, Task>? stopAudio;
    private Func<ChannelViewModel, Task>? startTransmit;
    private Func<ChannelViewModel, Task>? stopTransmit;
    private Func<ChannelViewModel?>? receivePresentationOwnerResolver;
    private bool audioEnabled;
    private bool audioSuspended;
    private bool audioBusy;
    private bool transmitEnabled;
    private bool transmitSelected;
    private bool pageSelected;
    private bool alertSelected;
    private bool transmitBusy;
    private bool transmitEncrypted;
    private bool recordingEnabled;
    private string lastCallerText = "--";
    private double audioLevel;
    private double audioPeakLevel;
    private double volume = 1.0;
    private double stereoBalance;
    private long ignoredLatePacketCount;
    private long droppedReceiveFrameCount;
    private long receiveAudioMeterStreamId;
    private uint? receivePlaybackSourceId;
    private uint? receivePlaybackStreamId;
    private string ignoredSubscriberIdsText = string.Empty;
    private string outputDeviceIdText = string.Empty;
    private IReadOnlyList<AudioDeviceOptionViewModel> outputDeviceOptions = [];
    private double widgetX;
    private double widgetY;
    private bool darkMode;

    public ChannelViewModel(
        ChannelConfiguration configuration,
        IP25KeyResolver? p25KeyResolver = null,
        IEnumerable<RadioAlias>? aliases = null,
        IDmrKeyResolver? dmrKeyResolver = null,
        INxdnKeyResolver? nxdnKeyResolver = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        this.configuration = configuration;
        this.p25KeyResolver = p25KeyResolver;
        this.dmrKeyResolver = dmrKeyResolver;
        this.nxdnKeyResolver = nxdnKeyResolver;
        this.aliases = aliases as RadioAliasIndex ?? new RadioAliasIndex(aliases);
        runtime = new ChannelRuntime(ChannelRuntimeDefinition.FromConfiguration(configuration));
        sessionDefinition = ChannelDefinition.FromRuntime(
            runtime.Definition,
            $"{runtime.Definition.SystemName}\u001F{runtime.Definition.Name}");
        transmitEncrypted = runtime.Definition.IsEncrypted;
        runtime.PropertyChanged += HandleRuntimePropertyChanged;
        AudioCommand = new AsyncRelayCommand(() => Task.CompletedTask, () => false);
        PttCommand = new AsyncRelayCommand(() => Task.CompletedTask, () => false);
        EncryptionCommand = new AsyncRelayCommand(ToggleEncryptionAsync, () => CanToggleEncryption && !transmitBusy && !audioBusy);
        RecordingCommand = new AsyncRelayCommand(ToggleRecordingAsync, () => CanRecord);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<bool>? TransmitEncryptionChanged;
    public event EventHandler<bool>? RecordingStateChanged;
    public event EventHandler<double>? VolumeChanged;
    public event EventHandler<double>? StereoBalanceChanged;

    public string Name => runtime.Definition.Name;
    public string SettingsKey => $"{runtime.Definition.SystemName}\u001F{runtime.Definition.Name}";
    public string ModeText => runtime.Definition.Mode.ToUpperInvariant();
    public string TalkgroupText => $"TG {runtime.Definition.DestinationId} - {ModeText}";
    public string DestinationText => $"{runtime.Definition.SystemName} / TGID {runtime.Definition.DestinationId}";
    public string LastCallerText => lastCallerText;
    public string LastCallerDisplayText => $"Last: {lastCallerText}";
    public double AudioLevel => audioLevel;
    public double AudioFillWidth => AudioMeterWidth * audioLevel / 100;
    public double AudioPeakLevel => audioPeakLevel;
    public double AudioPeakMarkerX => Math.Clamp(
        (AudioMeterWidth * audioPeakLevel / 100) - 1,
        0,
        AudioMeterWidth - 2);
    public IBrush AudioPeakMarkerBrush => audioPeakLevel >= ChannelAudioMeter.RedThresholdDisplayLevel
        ? RedPeakMarkerBrush
        : audioPeakLevel >= ChannelAudioMeter.YellowThresholdDisplayLevel
            ? YellowPeakMarkerBrush
            : NormalPeakMarkerBrush;
    public bool IsAudioPeakVisible => audioPeakLevel > 0;
    public double CardWidth => (configuration.CardSize ?? "normal").Trim().ToLowerInvariant() switch
    {
        "small" => 180,
        "large" => 330,
        _ => 235
    };
    public double CardContentWidth => CardWidth - 12;
    public double AudioMeterWidth => CardWidth - (CardWidth == 180 ? 20 : 12);
    public double WidgetX => widgetX;
    public double WidgetY => widgetY;
    public IBrush CardBackgroundBrush => runtime.State == ChannelRuntimeState.Transmitting
        ? new SolidColorBrush(Color.Parse("#0B6B9C"))
        : IsReceivePresentationActive
            ? new SolidColorBrush(Color.Parse("#008A3A"))
            : audioEnabled
                ? new SolidColorBrush(Color.Parse(darkMode ? "#1B2B22" : "#E2F3E8"))
                : new SolidColorBrush(Color.Parse(darkMode ? "#151D26" : "#FFFFFF"));
    public IBrush CardBorderBrush => runtime.State == ChannelRuntimeState.Transmitting
        ? new SolidColorBrush(Color.Parse("#2497D3"))
        : IsReceivePresentationActive
            ? new SolidColorBrush(Color.Parse("#00C86A"))
            : audioEnabled
                ? new SolidColorBrush(Color.Parse("#4E8060"))
                : CreateBrush(configuration.ResourceColor, darkMode ? "#2A3A4B" : "#9BA8B5");
    public IBrush CardTextBrush => new SolidColorBrush(Color.Parse(
        IsReceivePresentationActive || runtime.State == ChannelRuntimeState.Transmitting
            ? "#FFFFFF"
            : darkMode ? "#DCE3EB" : "#18212B"));
    public string StateText
    {
        get
        {
            if (runtime.State == ChannelRuntimeState.Transmitting)
                return runtime.StateText;

            if (audioSuspended)
                return "RX muted during console transmit";

            ChannelViewModel? owner = ReceivePresentationOwner;
            if (owner?.PresentationSourceId is uint sourceId)
            {
                string alias = AliasFileLoader.FindAlias(aliases, sourceId);
                if (!string.IsNullOrWhiteSpace(alias))
                    return $"Receiving from {alias} ({sourceId}) (stream {owner.PresentationStreamId})";
                return $"Receiving from {sourceId} (stream {owner.PresentationStreamId})";
            }

            if (!audioEnabled && runtime.State == ChannelRuntimeState.Receiving)
                return "Receive disabled";

            return runtime.StateText;
        }
    }
    public ChannelRuntimeState State => runtime.State;
    public uint? SourceId => runtime.SourceId;
    public uint? StreamId => runtime.StreamId;
    public ChannelRuntimeDefinition Definition => runtime.Definition;
    public ChannelSessionId SessionId => sessionDefinition.SessionId;
    public ChannelDefinition SessionDefinition => sessionDefinition;
    public bool IsAudioEnabled => audioEnabled;
    public bool IsAudioSuspended => audioSuspended;
    public bool IsReceivePresentationActive => ReceivePresentationOwner is not null;
    public string AudioButtonText => audioSuspended ? "RX muted" : audioEnabled ? "Stop audio" : "Listen";
    public bool IsTransmitting => transmitEnabled;
    public bool IsTransmitSelected => transmitSelected;
    public bool IsPageSelected => pageSelected;
    public bool IsAlertSelected => alertSelected;
    public bool IsTransmitEncrypted => transmitEncrypted;
    public bool IsRecordingEnabled => recordingEnabled;
    public string RecordButtonText => "TAR";
    public string RecordingConfigurationButtonText => recordingEnabled ? "Disable TAR" : "Enable TAR";
    public double Volume
    {
        get => volume;
        set => SetVolume(value, raiseChanged: true);
    }
    public double VolumeSliderValue
    {
        get => NeutralSliderMath.VolumeGainToPosition(volume);
        set => SetVolume(NeutralSliderMath.VolumePositionToGain(value), raiseChanged: true);
    }
    public double StereoBalance
    {
        get => stereoBalance;
        set => SetStereoBalance(value, raiseChanged: true);
    }
    public long IgnoredLatePacketCount => Interlocked.Read(ref ignoredLatePacketCount);
    public long DroppedReceiveFrameCount => Interlocked.Read(ref droppedReceiveFrameCount);

    internal bool HasLocalReceivePresentation =>
        audioEnabled &&
        !audioSuspended &&
        (runtime.State == ChannelRuntimeState.Receiving || receivePlaybackStreamId is not null);

    private ChannelViewModel? ReceivePresentationOwner => !audioEnabled || audioSuspended
        ? null
        : HasLocalReceivePresentation
            ? this
            : receivePresentationOwnerResolver?.Invoke();

    internal bool IsTrackingReceiveStream(uint streamId)
        => Volatile.Read(ref projectedReceiveStreams).Contains(streamId);

    internal string ResolveSubscriberAlias(uint sourceId)
        => AliasFileLoader.FindAlias(aliases, sourceId);

    private uint? PresentationSourceId => receivePlaybackStreamId is not null
        ? receivePlaybackSourceId
        : runtime.SourceId;

    private uint? PresentationStreamId => receivePlaybackStreamId ?? runtime.StreamId;

    internal void RecordIgnoredLatePacket()
        => Interlocked.Increment(ref ignoredLatePacketCount);

    internal void RecordDroppedReceiveFrame()
        => Interlocked.Increment(ref droppedReceiveFrameCount);
    public string StereoBalanceText => stereoBalance switch
    {
        <= -0.9999 => "Left",
        >= 0.9999 => "Right",
        > -0.0001 and < 0.0001 => "Center",
        < 0 => $"{-stereoBalance:P0} left",
        _ => $"{stereoBalance:P0} right"
    };
    public string OutputDeviceIdText
    {
        get => outputDeviceIdText;
        set
        {
            string normalized = value ?? string.Empty;
            if (outputDeviceIdText == normalized)
                return;
            outputDeviceIdText = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OutputDeviceIdText)));
        }
    }
    public IReadOnlyList<AudioDeviceOptionViewModel> OutputDeviceOptions => outputDeviceOptions;
    public AudioDeviceOptionViewModel? SelectedOutputDevice
    {
        get => ResolveOutputDevice();
        set
        {
            if (value is not null)
                OutputDeviceIdText = value.Id;
        }
    }
    public string IgnoredSubscriberIdsText
    {
        get => ignoredSubscriberIdsText;
        set
        {
            if (ignoredSubscriberIdsText == value)
                return;
            ignoredSubscriberIdsText = value ?? string.Empty;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IgnoredSubscriberIdsText)));
        }
    }
    public bool CanRecord => CanListen;
    public bool CanToggleEncryption =>
        ChannelProtocolMediaMapper.RequiresVocoder(runtime.Definition.Protocol) &&
        runtime.Definition.IsEncrypted &&
        runtime.Definition.SelectableEncryption &&
        (transmitEncrypted || CanResolveConfiguredKey());
    public string EncryptionStatusText => !runtime.Definition.IsEncrypted
        ? "Clear"
        : CanResolveConfiguredKey()
            ? "Key available"
            : "Key unavailable";
    public string EncryptionButtonText => transmitEncrypted ? "SECURE" : "CLEAR";
    public bool CanListen => runtime.Definition.Protocol switch
    {
        ChannelProtocol.Dmr or ChannelProtocol.P25 or ChannelProtocol.Nxdn => true,
        ChannelProtocol.Analog => !runtime.Definition.IsEncrypted,
        _ => false
    };
    public bool CanTransmit =>
        !runtime.Definition.RxOnly &&
        runtime.Definition.Protocol switch
        {
            ChannelProtocol.Dmr or ChannelProtocol.P25 or ChannelProtocol.Nxdn =>
                !transmitEncrypted || CanResolveConfiguredKey(),
            ChannelProtocol.Analog => !runtime.Definition.IsEncrypted,
            _ => false
        };
    public bool IsPttControlEnabled =>
        CanTransmit &&
        (transmitEnabled || !IsReceivePresentationActive);
    public string PttButtonText => transmitEnabled ? "Release" : "PTT";

    private bool CanResolveConfiguredKey()
    {
        return runtime.Definition.Protocol switch
        {
            ChannelProtocol.P25 => p25KeyResolver?.CanResolve(
                runtime.Definition.SystemName,
                runtime.Definition.EncryptionAlgorithm,
                runtime.Definition.EncryptionKeyId) == true,
            ChannelProtocol.Dmr => dmrKeyResolver?.CanResolve(
                runtime.Definition.SystemName,
                runtime.Definition.EncryptionAlgorithm,
                runtime.Definition.EncryptionKeyId) == true,
            ChannelProtocol.Nxdn => nxdnKeyResolver?.CanResolve(
                runtime.Definition.SystemName,
                runtime.Definition.EncryptionAlgorithm,
                runtime.Definition.EncryptionKeyId) == true,
            _ => false
        };
    }

    public string TransmitSelectionText => "TX";
    public string PageSelectionText => "PAGE";
    public string AlertSelectionText => "ALERT";
    public IBrush TransmitSelectionBrush => new SolidColorBrush(Color.Parse(
        transmitSelected
            ? darkMode ? "#694BB0" : "#D7C9F2"
            : darkMode ? "#242938" : "#E8EDF3"));
    public IBrush TransmitSelectionBorderBrush => new SolidColorBrush(Color.Parse(
        transmitSelected
            ? darkMode ? "#B69AF4" : "#7655B8"
            : darkMode ? "#3A4555" : "#8996A3"));
    public IBrush PageSelectionBrush => new SolidColorBrush(Color.Parse(
        pageSelected
            ? darkMode ? "#A15B2A" : "#F2D1B8"
            : darkMode ? "#242938" : "#E8EDF3"));
    public IBrush PageSelectionBorderBrush => new SolidColorBrush(Color.Parse(
        pageSelected
            ? darkMode ? "#F0A15C" : "#A95C26"
            : darkMode ? "#3A4555" : "#8996A3"));
    public IBrush AlertSelectionBrush => new SolidColorBrush(Color.Parse(
        alertSelected
            ? darkMode ? "#8A3D68" : "#F0C7DE"
            : darkMode ? "#242938" : "#E8EDF3"));
    public IBrush AlertSelectionBorderBrush => new SolidColorBrush(Color.Parse(
        alertSelected
            ? darkMode ? "#E58BBC" : "#A84479"
            : darkMode ? "#3A4555" : "#8996A3"));
    public IBrush RecordingSelectionBrush => new SolidColorBrush(Color.Parse(
        recordingEnabled
            ? darkMode ? "#8A3A3A" : "#F2CCCC"
            : darkMode ? "#242938" : "#E8EDF3"));
    public IBrush RecordingSelectionBorderBrush => new SolidColorBrush(Color.Parse(
        recordingEnabled
            ? darkMode ? "#E58A8A" : "#A84343"
            : darkMode ? "#3A4555" : "#8996A3"));
    public IBrush EncryptionSelectionBrush => new SolidColorBrush(Color.Parse(
        transmitEncrypted
            ? "#B45309"
            : darkMode ? "#242938" : "#E8EDF3"));
    public IBrush EncryptionSelectionBorderBrush => new SolidColorBrush(Color.Parse(
        transmitEncrypted
            ? "#F59E0B"
            : darkMode ? "#3A4555" : "#8996A3"));
    public IBrush EncryptionSelectionTextBrush => new SolidColorBrush(Color.Parse(
        transmitEncrypted
            ? "#FFFFFF"
            : darkMode ? "#DCE3EB" : "#18212B"));
    public ICommand AudioCommand { get; private set; }
    public ICommand PttCommand { get; private set; }
    public ICommand EncryptionCommand { get; }
    public ICommand RecordingCommand { get; }

    public void ConfigureAudio(
        Func<ChannelViewModel, Task> start,
        Func<ChannelViewModel, Task> stop)
    {
        startAudio = start ?? throw new ArgumentNullException(nameof(start));
        stopAudio = stop ?? throw new ArgumentNullException(nameof(stop));
        AudioCommand = new AsyncRelayCommand(ToggleAudioAsync, () => CanListen && !audioBusy);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AudioCommand)));
    }

    public void ConfigureTransmit(
        Func<ChannelViewModel, Task> start,
        Func<ChannelViewModel, Task> stop)
    {
        startTransmit = start ?? throw new ArgumentNullException(nameof(start));
        stopTransmit = stop ?? throw new ArgumentNullException(nameof(stop));
        PttCommand = new AsyncRelayCommand(ToggleTransmitAsync, () => IsPttControlEnabled && !transmitBusy);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PttCommand)));
        (EncryptionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    public void RefreshEncryptionState()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanTransmit)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPttControlEnabled)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanToggleEncryption)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncryptionStatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncryptionButtonText)));
        NotifyEncryptionAppearanceChanged();
        (PttCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (EncryptionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    public void RestoreTransmitEncryption(bool encrypted)
    {
        if (!runtime.Definition.IsEncrypted || !runtime.Definition.SelectableEncryption)
            return;

        transmitEncrypted = encrypted;
        NotifySelectableEncryptionStateChanged();
    }

    public void SetRecordingEnabled(bool enabled)
        => SetRecordingEnabledCore(enabled, raiseStateChanged: true);

    public void RestoreRecordingEnabled(bool enabled)
        => SetRecordingEnabledCore(enabled, raiseStateChanged: false);

    private void SetRecordingEnabledCore(bool enabled, bool raiseStateChanged)
    {
        if (recordingEnabled == enabled)
            return;

        recordingEnabled = enabled;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRecordingEnabled)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecordButtonText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecordingConfigurationButtonText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecordingSelectionBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecordingSelectionBorderBrush)));
        if (raiseStateChanged)
            RecordingStateChanged?.Invoke(this, enabled);
        (RecordingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    public void RestoreVolume(double value)
        => SetVolume(value, raiseChanged: false);

    public void RestoreStereoBalance(double value)
        => SetStereoBalance(value, raiseChanged: false);

    public void RestoreOutputDeviceId(string? deviceId)
        => OutputDeviceIdText = deviceId?.Trim() ?? string.Empty;

    public void SetOutputDeviceOptions(IReadOnlyList<AudioDeviceOptionViewModel> options)
    {
        outputDeviceOptions = options ?? [];
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OutputDeviceOptions)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedOutputDevice)));
    }

    public void RefreshOutputDeviceSelection()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedOutputDevice)));

    private AudioDeviceOptionViewModel? ResolveOutputDevice()
    {
        return outputDeviceOptions.FirstOrDefault(device =>
                   !string.IsNullOrWhiteSpace(OutputDeviceIdText) &&
                   device.Id.Equals(OutputDeviceIdText, StringComparison.OrdinalIgnoreCase)) ??
               outputDeviceOptions.FirstOrDefault(device => device.IsDefault) ??
               (outputDeviceOptions.Count > 0 ? outputDeviceOptions[0] : null);
    }

    private void SetVolume(double value, bool raiseChanged)
    {
        double normalized = double.IsFinite(value) ? Math.Clamp(value, 0, 4) : 1.0;
        if (Math.Abs(volume - normalized) < 0.0001)
            return;

        volume = normalized;
        if (raiseChanged)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Volume)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VolumeSliderValue)));
            VolumeChanged?.Invoke(this, normalized);
        }
    }

    private void SetStereoBalance(double value, bool raiseChanged)
    {
        double normalized = double.IsFinite(value) ? Math.Clamp(value, -1, 1) : 0;
        if (Math.Abs(stereoBalance - normalized) < 0.0001)
            return;

        stereoBalance = normalized;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StereoBalance)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StereoBalanceText)));
        if (raiseChanged)
            StereoBalanceChanged?.Invoke(this, normalized);
    }

    public void SetIgnoredSubscriberIds(IEnumerable<uint> subscriberIds)
    {
        ArgumentNullException.ThrowIfNull(subscriberIds);
        IgnoredSubscriberIdsText = string.Join(", ", subscriberIds.Where(id => id != 0).Distinct().OrderBy(id => id));
    }

    internal void SetReceivePresentationOwnerResolver(Func<ChannelViewModel?> resolver)
    {
        receivePresentationOwnerResolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        RefreshReceivePresentation();
    }

    internal void MarkReceivePlaybackActive(uint sourceId, uint streamId)
    {
        if (!audioEnabled || audioSuspended || streamId == 0)
            return;
        MarkReceiveAudioMeterActive(streamId);
        if (receivePlaybackSourceId == sourceId && receivePlaybackStreamId == streamId)
            return;
        if (receivePlaybackStreamId is not null)
            return;

        receivePlaybackSourceId = sourceId;
        receivePlaybackStreamId = streamId;
        NotifyReceivePresentationChanged();
    }

    internal void MarkReceivePlaybackEnded(uint streamId)
    {
        MarkReceiveAudioMeterEnded(streamId);
        if (receivePlaybackStreamId != streamId)
            return;

        receivePlaybackSourceId = null;
        receivePlaybackStreamId = null;
        NotifyReceivePresentationChanged();
    }

    // The decoder path can lead the UI-thread lifecycle pass during a traffic
    // burst. Track its first audible stream without raising properties from a
    // worker thread so early meter samples remain eligible for the next UI
    // refresh instead of being discarded.
    internal void MarkReceiveAudioMeterActive(uint streamId)
    {
        if (streamId == 0)
            return;
        Interlocked.CompareExchange(ref receiveAudioMeterStreamId, streamId, 0);
    }

    internal void MarkReceiveAudioMeterEnded(uint streamId)
    {
        if (streamId == 0)
            return;
        Interlocked.CompareExchange(ref receiveAudioMeterStreamId, 0, streamId);
    }

    internal void RefreshReceivePresentation()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StateText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardBackgroundBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardBorderBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardTextBrush)));
    }

    private void NotifyReceivePresentationChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReceivePresentationActive)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPttControlEnabled)));
        (PttCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        RefreshReceivePresentation();
    }

    private void ClearReceivePlayback()
    {
        Interlocked.Exchange(ref receiveAudioMeterStreamId, 0);
        if (receivePlaybackStreamId is null)
            return;
        receivePlaybackSourceId = null;
        receivePlaybackStreamId = null;
    }

    public void SetAudioEnabled(bool enabled)
    {
        bool suspensionChanged = audioSuspended;
        audioSuspended = false;
        if (audioEnabled == enabled && !suspensionChanged)
            return;
        audioEnabled = enabled;
        if (!enabled)
            ClearReceivePlayback();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAudioEnabled)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAudioSuspended)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AudioButtonText)));
        NotifyReceivePresentationChanged();
        if (!enabled)
            SetAudioLevel(0);
    }

    public void SetAudioSuspended(bool suspended)
    {
        if (!audioEnabled || audioSuspended == suspended)
            return;
        audioSuspended = suspended;
        if (suspended)
            ClearReceivePlayback();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAudioSuspended)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AudioButtonText)));
        NotifyReceivePresentationChanged();
        if (suspended)
            SetAudioLevel(0);
    }

    public void SetAudioLevel(
        double value,
        ChannelAudioDirection? direction = null,
        uint? streamId = null,
        double? peakValue = null)
    {
        double normalized = double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;
        double normalizedPeak = peakValue is double peak && double.IsFinite(peak)
            ? Math.Clamp(peak, 0, 100)
            : normalized;
        long fastReceiveStreamId = Interlocked.Read(ref receiveAudioMeterStreamId);
        if (streamId is uint expectedStreamId)
        {
            bool streamMatches = PresentationStreamId == expectedStreamId ||
                (direction == ChannelAudioDirection.Receive &&
                 fastReceiveStreamId == expectedStreamId);
            if (!streamMatches)
                return;
        }
        bool fastReceiveActive = direction == ChannelAudioDirection.Receive &&
            streamId is uint receiveStreamId &&
            fastReceiveStreamId == receiveStreamId;
        if ((direction == ChannelAudioDirection.Receive &&
             (!audioEnabled || audioSuspended ||
              (!IsReceivePresentationActive && !fastReceiveActive))) ||
            (direction == ChannelAudioDirection.Transmit && runtime.State != ChannelRuntimeState.Transmitting))
        {
            normalized = 0;
            normalizedPeak = 0;
        }
        ApplyAudioLevel(normalized, normalizedPeak);
    }

    // Receive meter samples observed at the mixer boundary are already known
    // to be audible on this channel. Their logical episode lane can outlive
    // the physical stream ID currently projected by the card, so applying the
    // physical-ID filter again would hide valid presented audio after a stream
    // handoff.
    internal void SetPresentedReceiveAudioLevel(double value, double? peakValue = null)
    {
        double normalized = double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;
        double normalizedPeak = peakValue is double peak && double.IsFinite(peak)
            ? Math.Clamp(peak, 0, 100)
            : normalized;
        bool receiveActive = IsReceivePresentationActive ||
            Interlocked.Read(ref receiveAudioMeterStreamId) != 0;
        if (!audioEnabled || audioSuspended || !receiveActive)
        {
            normalized = 0;
            normalizedPeak = 0;
        }
        ApplyAudioLevel(normalized, normalizedPeak);
    }

    private void ApplyAudioLevel(double normalized, double normalizedPeak)
    {
        bool levelChanged = normalized == 0
            ? audioLevel != 0
            : Math.Abs(audioLevel - normalized) >= 0.25;
        bool peakChanged = normalizedPeak == 0
            ? audioPeakLevel != 0
            : Math.Abs(audioPeakLevel - normalizedPeak) >= 0.25;
        if (!levelChanged && !peakChanged)
            return;

        if (levelChanged)
        {
            audioLevel = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AudioLevel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AudioFillWidth)));
        }

        if (peakChanged)
        {
            audioPeakLevel = normalizedPeak;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AudioPeakLevel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AudioPeakMarkerX)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AudioPeakMarkerBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAudioPeakVisible)));
        }
    }

    public void SetTransmitEnabled(bool enabled, uint streamId = 0)
    {
        if (enabled)
        {
            if (streamId == 0)
                throw new ArgumentOutOfRangeException(nameof(streamId));
            runtime.MarkTransmitting(streamId);
        }
        else
        {
            runtime.MarkIdle();
        }

        if (transmitEnabled == enabled)
        {
            (EncryptionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            return;
        }
        transmitEnabled = enabled;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTransmitting)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPttControlEnabled)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PttButtonText)));
        (PttCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (EncryptionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    public void SetTransmitSelected(bool selected)
    {
        if (transmitSelected == selected)
            return;
        transmitSelected = selected;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTransmitSelected)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TransmitSelectionText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TransmitSelectionBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TransmitSelectionBorderBrush)));
    }

    public void SetPageSelected(bool selected)
    {
        if (pageSelected == selected)
            return;
        pageSelected = selected;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPageSelected)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PageSelectionText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PageSelectionBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PageSelectionBorderBrush)));
    }

    public void SetAlertSelected(bool selected)
    {
        if (alertSelected == selected)
            return;
        alertSelected = selected;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAlertSelected)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AlertSelectionText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AlertSelectionBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AlertSelectionBorderBrush)));
    }

    public void RestoreTransmitSelection(bool selected) => SetTransmitSelected(selected);

    public void SetWidgetPosition(double x, double y)
    {
        double nextX = double.IsFinite(x) ? Math.Clamp(x, 0, 10_000) : 0;
        double nextY = double.IsFinite(y) ? Math.Clamp(y, 0, 10_000) : 0;
        if (Math.Abs(widgetX - nextX) >= 0.01)
        {
            widgetX = nextX;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WidgetX)));
        }
        if (Math.Abs(widgetY - nextY) >= 0.01)
        {
            widgetY = nextY;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WidgetY)));
        }
    }

    public void SetDarkMode(bool enabled)
    {
        if (darkMode == enabled)
            return;
        darkMode = enabled;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardBackgroundBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardBorderBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardTextBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TransmitSelectionBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TransmitSelectionBorderBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PageSelectionBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PageSelectionBorderBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AlertSelectionBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AlertSelectionBorderBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecordingSelectionBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecordingSelectionBorderBrush)));
        NotifyEncryptionAppearanceChanged();
    }

    private void NotifyEncryptionAppearanceChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncryptionSelectionBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncryptionSelectionBorderBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncryptionSelectionTextBrush)));
    }

    public bool TryApplyTraffic(string systemName, FneTrafficFrame traffic)
        => ApplyTraffic(systemName, traffic, DateTimeOffset.UtcNow).Matched;

    internal ChannelTrafficApplyResult ApplyTraffic(
        string systemName,
        FneTrafficFrame traffic,
        DateTimeOffset now)
    {
        if (!CanProjectTraffic(
                systemName,
                traffic,
                IsTrackingReceiveStream(traffic.StreamId)))
            return ChannelTrafficApplyResult.NoMatch;

        ReceiveRouteProjectionDecision projection =
            ChannelReceiveProjectionCompatibility.Observe(this, traffic, now);
        return ProjectTraffic(traffic, now, projection);
    }

    internal ChannelTrafficApplyResult ApplyTraffic(
        string systemName,
        FneTrafficFrame traffic,
        DateTimeOffset now,
        ReceiveIngressRouteDecision ingressDecision)
    {
        if (!CanProjectTraffic(
                systemName,
                traffic,
                ingressDecision.ActiveStreamIds.Contains(traffic.StreamId)) ||
            ingressDecision.RouteKey != SessionDefinition.RouteKey)
        {
            return ChannelTrafficApplyResult.NoMatch;
        }

        return ProjectTraffic(traffic, now, ingressDecision.PacketDecision);
    }

    private bool CanProjectTraffic(
        string systemName,
        FneTrafficFrame traffic,
        bool isTrackedReceiveStream)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemName);
        ArgumentNullException.ThrowIfNull(traffic);

        if (!runtime.Definition.SystemName.Equals(systemName, StringComparison.OrdinalIgnoreCase) ||
            !MatchesProtocol(traffic.Protocol) ||
            traffic.StreamId == 0)
        {
            return false;
        }

        if (runtime.State == ChannelRuntimeState.Transmitting)
            return false;

        if (ReceiveTrafficClassifier.IsTerminator(traffic))
            return true;

        if (ReceiveTrafficClassifier.IsDmrPrivacyHeader(traffic))
        {
            return isTrackedReceiveStream &&
                   runtime.Definition.DestinationId == traffic.DestinationId &&
                   runtime.Definition.Slot == traffic.Slot;
        }

        if (traffic.DestinationId != runtime.Definition.DestinationId)
            return false;

        bool isDmrVoiceLcHeader = ReceiveTrafficClassifier.IsDefinitiveStart(traffic);
        return (MatchesVoiceTraffic(traffic) || isDmrVoiceLcHeader) &&
               traffic.SourceId != 0;
    }

    private ChannelTrafficApplyResult ProjectTraffic(
        FneTrafficFrame traffic,
        DateTimeOffset now,
        ReceiveRouteProjectionDecision projection)
    {
        Volatile.Write(ref projectedReceiveStreams, projection.ActiveStreamIds);
        ReceiveStreamDecision decision = projection.StreamDecision;

        if (ReceiveTrafficClassifier.IsTerminator(traffic))
        {
            if (decision.Transition != ReceiveStreamTransition.TerminationPending)
                return decision.Transition == ReceiveStreamTransition.IgnoredLate
                    ? ToApplyResult(decision)
                    : ChannelTrafficApplyResult.NoMatch;

            if (runtime.StreamId == traffic.StreamId)
                runtime.MarkIdle(now);
            return ToApplyResult(decision);
        }

        if (ReceiveTrafficClassifier.IsDmrPrivacyHeader(traffic))
        {
            if (decision.Transition is (ReceiveStreamTransition.Continued or ReceiveStreamTransition.Resumed) &&
                runtime.StreamId == traffic.StreamId)
            {
                runtime.MarkReceiving(traffic.SourceId, traffic.StreamId, now);
            }
            return ToApplyResult(decision);
        }

        if (decision.Transition != ReceiveStreamTransition.IgnoredLate &&
            (decision.Transition != ReceiveStreamTransition.Colliding ||
             runtime.State != ChannelRuntimeState.Receiving))
        {
            runtime.MarkReceiving(traffic.SourceId, traffic.StreamId, now);
        }
        return ToApplyResult(decision);
    }

    public bool TryExpireReceiveState(DateTimeOffset now, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        return AdvanceReceiveLifecycle(now).Transition is
            ReceiveStreamTransition.GraceExpired or
            ReceiveStreamTransition.TerminationExpired;
    }

    internal ChannelTrafficApplyResult AdvanceReceiveLifecycle(DateTimeOffset now)
    {
        ReceiveRouteProjectionDecision projection =
            ChannelReceiveProjectionCompatibility.Advance(this, now);
        return ProjectReceiveLifecycleDecision(projection, now);
    }

    internal ChannelTrafficApplyResult ProjectReceiveLifecycleDecision(
        ReceiveRouteProjectionDecision projection,
        DateTimeOffset now)
    {
        Volatile.Write(ref projectedReceiveStreams, projection.ActiveStreamIds);
        ReceiveStreamDecision decision = projection.StreamDecision;
        if (decision.Transition is
            ReceiveStreamTransition.GraceExpired or
            ReceiveStreamTransition.TerminationExpired)
        {
            if (decision.EndedStreamId is uint endedStreamId)
            {
                if (runtime.StreamId == endedStreamId)
                    runtime.MarkIdle(now);
                MarkReceivePlaybackEnded(endedStreamId);
            }
        }
        return ToApplyResult(decision);
    }

    private static ChannelTrafficApplyResult ToApplyResult(ReceiveStreamDecision decision)
        => new(
            Matched: decision.Transition is not ReceiveStreamTransition.None,
            decision.Transition,
            decision.ActiveStreamId,
            decision.EndedStreamId,
            decision.EndedAt);

    private bool MatchesVoiceTraffic(FneTrafficFrame traffic)
    {
        return ReceiveTrafficClassifier.CarriesVoicePayload(traffic) &&
               (runtime.Definition.Protocol != ChannelProtocol.Dmr || traffic.Slot == runtime.Definition.Slot);
    }

    private bool MatchesProtocol(FneTrafficProtocol protocol)
        => protocol == FneTrafficProtocolMapper.FromChannelProtocol(runtime.Definition.Protocol);

    private async Task ToggleAudioAsync()
    {
        if (startAudio is null || stopAudio is null)
            return;

        audioBusy = true;
        (AudioCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        try
        {
            if (audioEnabled)
                await stopAudio(this);
            else
                await startAudio(this);
        }
        finally
        {
            audioBusy = false;
            (AudioCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private async Task ToggleTransmitAsync()
    {
        if (startTransmit is null || stopTransmit is null)
            return;

        transmitBusy = true;
        (PttCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        try
        {
            if (transmitEnabled)
                await stopTransmit(this);
            else
                await startTransmit(this);
        }
        finally
        {
            transmitBusy = false;
            (PttCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private Task ToggleEncryptionAsync()
    {
        if (!CanToggleEncryption || transmitEnabled)
            return Task.CompletedTask;

        transmitEncrypted = !transmitEncrypted;
        NotifySelectableEncryptionStateChanged();
        TransmitEncryptionChanged?.Invoke(this, transmitEncrypted);
        return Task.CompletedTask;
    }

    private void NotifySelectableEncryptionStateChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTransmitEncrypted)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanTransmit)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPttControlEnabled)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanToggleEncryption)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncryptionButtonText)));
        NotifyEncryptionAppearanceChanged();
        (PttCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (EncryptionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private Task ToggleRecordingAsync()
    {
        if (!CanRecord && !recordingEnabled)
            return Task.CompletedTask;

        SetRecordingEnabled(!recordingEnabled);
        return Task.CompletedTask;
    }

    private void HandleRuntimePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ChannelRuntime.LastActivity))
            return;
        PropertyChanged?.Invoke(this, args);

        bool callerChanged = args.PropertyName is nameof(ChannelRuntime.State) or nameof(ChannelRuntime.SourceId);
        if (callerChanged && runtime.State == ChannelRuntimeState.Receiving && runtime.SourceId is uint sourceId)
        {
            string alias = AliasFileLoader.FindAlias(aliases, sourceId).Trim();
            lastCallerText = string.IsNullOrWhiteSpace(alias)
                ? sourceId.ToString(CultureInfo.InvariantCulture)
                : alias;
        }
        else if (args.PropertyName == nameof(ChannelRuntime.State) &&
            runtime.State is not (ChannelRuntimeState.Receiving or ChannelRuntimeState.Transmitting))
        {
            SetAudioLevel(0);
        }

        if (callerChanged)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastCallerText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastCallerDisplayText)));
        }

        if (args.PropertyName == nameof(ChannelRuntime.State))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReceivePresentationActive)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPttControlEnabled)));
            (PttCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            RefreshReceivePresentation();
        }
    }

    private static IBrush CreateBrush(string? color, string fallback)
    {
        try
        {
            return new SolidColorBrush(Color.Parse(
                string.IsNullOrWhiteSpace(color) ? fallback : color.Trim()));
        }
        catch (FormatException)
        {
            return new SolidColorBrush(Color.Parse(fallback));
        }
    }
}
