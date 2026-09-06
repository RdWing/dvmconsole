// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Media;
using DvmConsole.Application;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Runtime;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Operations;
using DvmConsole.Presentation;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace DvmConsole.Desktop;

public sealed class WidgetPositionChangedEventArgs(
    double x,
    double y,
    bool isFinal) : EventArgs
{
    public double X { get; } = x;
    public double Y { get; } = y;
    public bool IsFinal { get; } = isFinal;
}

public sealed class ChannelViewModel :
    IChannelCardViewModel,
    IChannelAudioRouteViewModel,
    IRecorderChannelViewModel,
    IPatchMemberChannelViewModel,
    INotifyPropertyChanged
{
    public event EventHandler<WidgetPositionChangedEventArgs>? WidgetPositionChanged;
    public static implicit operator DvmConsole.Application.ChannelId(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return new DvmConsole.Application.ChannelId(channel.SessionId);
    }

    public static implicit operator DvmConsole.Application.ChannelRecordingDescriptor(
        ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return new DvmConsole.Application.ChannelRecordingDescriptor(
            new DvmConsole.Application.ChannelId(channel.SessionId),
            channel.Definition,
            channel.IsRecordingEnabled,
            channel.IsTransmitEncrypted,
            channel.StreamId,
            channel.SourceId);
    }

    public static implicit operator DvmConsole.Application.ReceiveChannelDescriptor(
        ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return new DvmConsole.Application.ReceiveChannelDescriptor(
            new DvmConsole.Application.ChannelId(channel.SessionId),
            channel.Definition);
    }

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
    private bool transmitStarting;
    private bool transmitStopping;
    private bool transmitSelected;
    private bool pageSelected;
    private bool alertSelected;
    private bool transmitBusy;
    private bool transmitEncrypted;
    private bool hasCallPriority;
    private FneTalkgroupAvailability talkgroupAvailability = FneTalkgroupAvailability.Pending;
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
    private uint receiveEncryptionStreamId;
    private TrafficEncryptionObservationState receiveEncryptionState = new();
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
    internal event Action<ChannelViewModel, ChannelSelectionChange>? SelectionChanged;
    public event EventHandler<double>? VolumeChanged;
    public event EventHandler<double>? StereoBalanceChanged;

    public string Name => runtime.Definition.Name;
    public DvmConsole.Application.ChannelId Id => new(SessionId);
    public string RoutingKey => PatchMemberResolver.FromChannel(this).Key;
    public string SettingsKey => $"{runtime.Definition.SystemName}\u001F{runtime.Definition.Name}";
    public string SystemName => runtime.Definition.SystemName;
    public uint DestinationId => runtime.Definition.DestinationId;
    public string ModeText => runtime.Definition.Mode.ToUpperInvariant();
    public string TalkgroupText => $"TG {runtime.Definition.DestinationId} - {ModeText}";
    public string DestinationText => $"{runtime.Definition.SystemName} / TGID {runtime.Definition.DestinationId}";
    public string LastCallerText => lastCallerText;
    public string LastCallerDisplayText => $"Last: {lastCallerText}";
    public double AudioLevel => audioLevel;
    public AudioMeterState AudioMeter => new(audioLevel, audioPeakLevel);
    public double AudioPeakLevel => audioPeakLevel;
    public double CardWidth => ResolveCardWidth(configuration.CardSize);
    internal static double ResolveCardWidth(string? cardSize)
        => (cardSize ?? "normal").Trim().ToLowerInvariant() switch
        {
            "small" => 180,
            "large" => 330,
            _ => 235
        };
    public double CardContentWidth => CardWidth - 12;
    public double AudioMeterWidth => CardWidth - (CardWidth == 180 ? 20 : 12);
    public double WidgetX => widgetX;
    public double WidgetY => widgetY;
    public IBrush CardBackgroundBrush => transmitStarting || runtime.State == ChannelRuntimeState.Transmitting
        ? SolidBrushCache.Get("#0B6B9C")
        : IsReceivePresentationActive
            ? SolidBrushCache.Get("#008A3A")
            : audioEnabled
                ? SolidBrushCache.Get(darkMode ? "#1B2B22" : "#E2F3E8")
                : SolidBrushCache.Get(darkMode ? "#151D26" : "#FFFFFF");
    public IBrush CardBorderBrush => transmitStarting
        ? SolidBrushCache.Get("#D99920")
        : runtime.State == ChannelRuntimeState.Transmitting
        ? SolidBrushCache.Get("#2497D3")
        : IsReceivePresentationActive
            ? SolidBrushCache.Get("#00C86A")
            : audioEnabled
                ? SolidBrushCache.Get("#4E8060")
                : CreateBrush(configuration.ResourceColor, darkMode ? "#2A3A4B" : "#9BA8B5");
    public IBrush CardTextBrush => SolidBrushCache.Get(
        IsReceivePresentationActive || transmitStarting || runtime.State == ChannelRuntimeState.Transmitting
            ? "#FFFFFF"
            : darkMode ? "#DCE3EB" : "#18212B");
    public string StateText
    {
        get
        {
            if (transmitStarting)
                return "Starting PTT…";
            if (transmitStopping)
                return "Releasing PTT…";
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
    public string ReceiveAutomationName => $"{Name}; receive {(audioEnabled ? "enabled" : "disabled")}";
    public bool IsAudioSuspended => audioSuspended;
    public bool IsReceivePresentationActive => ReceivePresentationOwner is not null;
    public string AudioButtonText => audioSuspended ? "RX muted" : audioEnabled ? "Stop audio" : "Listen";
    public bool IsTransmitting => transmitEnabled;
    public bool IsTransmitStarting => transmitStarting;
    public bool IsTransmitStopping => transmitStopping;
    public bool IsTransmitSelected => transmitSelected;
    public bool IsPageSelected => pageSelected;
    public bool IsAlertSelected => alertSelected;
    public bool IsTransmitEncrypted => transmitEncrypted;
    public bool HasCallPriority => hasCallPriority;
    public bool ObservedReceiveEncrypted => receiveEncryptionState.Encryption.IsSecure;
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
    public string VolumeAutomationName => $"Volume for {Name}";
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
    System.Collections.IEnumerable IChannelAudioRouteViewModel.OutputDeviceOptions
        => OutputDeviceOptions;
    IAudioDeviceOptionViewModel? IChannelAudioRouteViewModel.SelectedOutputDevice
    {
        get => SelectedOutputDevice;
        set => SelectedOutputDevice = value as AudioDeviceOptionViewModel;
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
    internal bool TransmitKeyAvailable =>
        !runtime.Definition.IsEncrypted || CanResolveConfiguredKey();
    public string EncryptionStatusText => !runtime.Definition.IsEncrypted
        ? "Clear"
        : CanResolveConfiguredKey()
            ? "Key available"
            : "Key unavailable";
    public string EncryptionButtonText => transmitEncrypted ? "SECURE" : "CLEAR";
    public string EncryptionAutomationName => transmitEncrypted
        ? $"Use clear transmit on {Name}"
        : $"Use secure transmit on {Name}";
    public string EncryptionAutomationHelpText => CanToggleEncryption
        ? $"Current transmit mode is {EncryptionButtonText.ToLowerInvariant()}."
        : $"Encryption selection is unavailable. {EncryptionStatusText}.";
    public bool CanListen => runtime.Definition.Protocol switch
    {
        ChannelProtocol.Dmr or ChannelProtocol.P25 or ChannelProtocol.Nxdn => true,
        ChannelProtocol.Analog => !runtime.Definition.IsEncrypted,
        _ => false
    };
    internal bool CanTransmitByConfiguration =>
        !runtime.Definition.RxOnly &&
        runtime.Definition.Protocol switch
        {
            ChannelProtocol.Dmr or ChannelProtocol.P25 or ChannelProtocol.Nxdn =>
                !transmitEncrypted || CanResolveConfiguredKey(),
            ChannelProtocol.Analog => !runtime.Definition.IsEncrypted,
            _ => false
        };
    public bool CanTransmit =>
        CanTransmitByConfiguration &&
        talkgroupAvailability != FneTalkgroupAvailability.Unavailable;
    public bool IsPttControlEnabled =>
        transmitEnabled ||
        (CanTransmit && (hasCallPriority || !IsReceivePresentationActive));
    public FneTalkgroupAvailability TalkgroupAvailability => talkgroupAvailability;
    public bool IsTalkgroupUnavailable =>
        talkgroupAvailability == FneTalkgroupAvailability.Unavailable;
    public string TalkgroupUnavailableReason => runtime.Definition.Protocol == ChannelProtocol.Dmr
        ? $"the FNE does not allow TG {runtime.Definition.DestinationId} on TS{runtime.Definition.Slot + 1}"
        : $"the FNE does not allow TG {runtime.Definition.DestinationId}";
    internal string ConfigurationTransmitUnavailableReason => runtime.Definition.RxOnly
        ? "the channel is receive-only"
        : transmitEncrypted && !CanResolveConfiguredKey()
            ? "its encryption key is unavailable"
            : "the channel is not available for transmit";
    public string TransmitUnavailableReason => IsTalkgroupUnavailable
        ? TalkgroupUnavailableReason
        : ConfigurationTransmitUnavailableReason;
    public string PttButtonText => transmitEnabled ? "Release" : "PTT";
    public string PttAutomationName => transmitEnabled
        ? $"Release push to talk on {Name}"
        : $"Push to talk on {Name}";
    public string PttAutomationHelpText => IsPttControlEnabled
        ? transmitEnabled ? "Stop the active transmission." : "Start transmitting on this channel."
        : $"Push to talk is unavailable because {TransmitUnavailableReason}.";

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
    public string TransmitSelectionAutomationName => IsTransmitSelected
        ? $"Remove {Name} from multi-select transmit"
        : $"Include {Name} in multi-select transmit";
    public string PageSelectionText => "PAGE";
    public string PageSelectionAutomationName => IsPageSelected
        ? $"Remove {Name} from paging targets"
        : $"Include {Name} in paging targets";
    public string AlertSelectionText => "ALERT";
    public string AlertSelectionAutomationName => IsAlertSelected
        ? $"Remove {Name} from alert targets"
        : $"Include {Name} in alert targets";
    public string RecordingAutomationName => recordingEnabled
        ? $"Disable Talkgroup Audio Recording for {Name}"
        : $"Enable Talkgroup Audio Recording for {Name}";
    public string RecordingAutomationHelpText => CanRecord
        ? "Toggle recording for this channel."
        : "Recording is unavailable for this channel.";
    public IBrush TransmitSelectionBrush => SolidBrushCache.Get(
        transmitSelected
            ? darkMode ? "#694BB0" : "#D7C9F2"
            : darkMode ? "#242938" : "#E8EDF3");
    public IBrush TransmitSelectionBorderBrush => SolidBrushCache.Get(
        transmitSelected
            ? darkMode ? "#B69AF4" : "#7655B8"
            : darkMode ? "#3A4555" : "#8996A3");
    public IBrush PageSelectionBrush => SolidBrushCache.Get(
        pageSelected
            ? darkMode ? "#A15B2A" : "#F2D1B8"
            : darkMode ? "#242938" : "#E8EDF3");
    public IBrush PageSelectionBorderBrush => SolidBrushCache.Get(
        pageSelected
            ? darkMode ? "#F0A15C" : "#A95C26"
            : darkMode ? "#3A4555" : "#8996A3");
    public IBrush AlertSelectionBrush => SolidBrushCache.Get(
        alertSelected
            ? darkMode ? "#8A3D68" : "#F0C7DE"
            : darkMode ? "#242938" : "#E8EDF3");
    public IBrush AlertSelectionBorderBrush => SolidBrushCache.Get(
        alertSelected
            ? darkMode ? "#E58BBC" : "#A84479"
            : darkMode ? "#3A4555" : "#8996A3");
    public IBrush RecordingSelectionBrush => SolidBrushCache.Get(
        recordingEnabled
            ? darkMode ? "#8A3A3A" : "#F2CCCC"
            : darkMode ? "#242938" : "#E8EDF3");
    public IBrush RecordingSelectionBorderBrush => SolidBrushCache.Get(
        recordingEnabled
            ? darkMode ? "#E58A8A" : "#A84343"
            : darkMode ? "#3A4555" : "#8996A3");
    public IBrush EncryptionSelectionBrush => SolidBrushCache.Get(
        transmitEncrypted
            ? "#B45309"
            : darkMode ? "#242938" : "#E8EDF3");
    public IBrush EncryptionSelectionBorderBrush => SolidBrushCache.Get(
        transmitEncrypted
            ? "#F59E0B"
            : darkMode ? "#3A4555" : "#8996A3");
    public IBrush EncryptionSelectionTextBrush => SolidBrushCache.Get(
        transmitEncrypted
            ? "#FFFFFF"
            : darkMode ? "#DCE3EB" : "#18212B");
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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TransmitUnavailableReason)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanTransmit)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPttControlEnabled)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanToggleEncryption)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncryptionStatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncryptionButtonText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncryptionAutomationName)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncryptionAutomationHelpText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PttAutomationHelpText)));
        NotifyEncryptionAppearanceChanged();
        (PttCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (EncryptionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    internal void ApplyTalkgroupAvailability(FneTalkgroupAvailability availability)
    {
        if (talkgroupAvailability == availability)
            return;

        talkgroupAvailability = availability;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TalkgroupAvailability)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTalkgroupUnavailable)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TransmitUnavailableReason)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanTransmit)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPttControlEnabled)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PttAutomationHelpText)));
        (PttCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    public void RestoreTransmitEncryption(bool encrypted)
    {
        if (!runtime.Definition.IsEncrypted || !runtime.Definition.SelectableEncryption)
            return;

        transmitEncrypted = encrypted;
        NotifySelectableEncryptionStateChanged();
    }

    public void SetRecordingEnabled(bool enabled, [CallerMemberName] string origin = "")
        => SetRecordingEnabledCore(enabled, raiseStateChanged: true, origin);

    public void RestoreRecordingEnabled(bool enabled, [CallerMemberName] string origin = "")
        => SetRecordingEnabledCore(enabled, raiseStateChanged: false, $"settings restore: {origin}");

    private void SetRecordingEnabledCore(bool enabled, bool raiseStateChanged, string origin)
    {
        if (recordingEnabled == enabled)
            return;

        bool previous = recordingEnabled;
        recordingEnabled = enabled;
        ObserveSelectionChange(ChannelSelectionKind.Recording, previous, enabled, origin);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRecordingEnabled)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecordButtonText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecordingAutomationName)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecordingAutomationHelpText)));
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

    public void SetAudioEnabled(bool enabled, [CallerMemberName] string origin = "")
    {
        bool suspensionChanged = audioSuspended;
        audioSuspended = false;
        if (audioEnabled == enabled && !suspensionChanged)
            return;
        bool previous = audioEnabled;
        audioEnabled = enabled;
        if (previous != enabled)
            ObserveSelectionChange(ChannelSelectionKind.Receive, previous, enabled, origin);
        if (!enabled)
            ClearReceivePlayback();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAudioEnabled)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReceiveAutomationName)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAudioSuspended)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AudioButtonText)));
        NotifyReceivePresentationChanged();
        if (!enabled)
            SetAudioLevel(0);
    }

    private void ObserveSelectionChange(ChannelSelectionKind kind, bool previous, bool current, string origin)
    {
        try
        {
            SelectionChanged?.Invoke(this, new(kind, previous, current, origin));
        }
        catch
        {
            // Diagnostic observers cannot prevent a selection or its UI updates.
        }
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
            audioLevel = normalized;

        if (peakChanged)
            audioPeakLevel = normalizedPeak;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AudioMeter)));
    }

    public void SetTransmitEnabled(bool enabled, uint streamId = 0)
    {
        SetTransmitTransition(starting: false, stopping: false);
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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PttAutomationName)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PttAutomationHelpText)));
        (PttCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (EncryptionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    internal void SetTransmitStarting(bool starting)
        => SetTransmitTransition(starting, stopping: false);

    internal void SetTransmitStopping(bool stopping)
        => SetTransmitTransition(starting: false, stopping);

    private void SetTransmitTransition(bool starting, bool stopping)
    {
        bool startingChanged = transmitStarting != starting;
        bool stoppingChanged = transmitStopping != stopping;
        if (!startingChanged && !stoppingChanged)
            return;

        transmitStarting = starting;
        transmitStopping = stopping;
        if (startingChanged)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTransmitStarting)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardBackgroundBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardTextBrush)));
        }
        if (stoppingChanged)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTransmitStopping)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StateText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardBorderBrush)));
    }

    public void SetTransmitSelected(bool selected)
    {
        if (transmitSelected == selected)
            return;
        transmitSelected = selected;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTransmitSelected)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TransmitSelectionAutomationName)));
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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PageSelectionAutomationName)));
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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AlertSelectionAutomationName)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AlertSelectionText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AlertSelectionBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AlertSelectionBorderBrush)));
    }

    public void RestoreTransmitSelection(bool selected) => SetTransmitSelected(selected);

    public void SetWidgetPosition(double x, double y, bool isFinal = false)
    {
        double nextX = double.IsFinite(x) ? Math.Clamp(x, 0, 10_000) : 0;
        double nextY = double.IsFinite(y) ? Math.Clamp(y, 0, 10_000) : 0;
        bool changed = false;
        if (Math.Abs(widgetX - nextX) >= 0.01)
        {
            widgetX = nextX;
            changed = true;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WidgetX)));
        }
        if (Math.Abs(widgetY - nextY) >= 0.01)
        {
            widgetY = nextY;
            changed = true;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WidgetY)));
        }
        if (changed || isFinal)
        {
            WidgetPositionChanged?.Invoke(
                this,
                new WidgetPositionChangedEventArgs(widgetX, widgetY, isFinal));
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
        if (!ReceiveTrafficClassifier.IsTerminator(traffic) && receiveEncryptionStreamId != traffic.StreamId)
        {
            receiveEncryptionStreamId = traffic.StreamId;
            receiveEncryptionState = new TrafficEncryptionObservationState();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ObservedReceiveEncrypted)));
        }
        if (receiveEncryptionState.Observe(traffic))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ObservedReceiveEncrypted)));

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
        (EncryptionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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
            (EncryptionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private async Task ToggleTransmitAsync()
    {
        if (startTransmit is null || stopTransmit is null)
            return;

        transmitBusy = true;
        (PttCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (EncryptionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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
            (EncryptionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private Task ToggleEncryptionAsync()
    {
        SetTransmitEncrypted(!transmitEncrypted);
        return Task.CompletedTask;
    }

    internal void SetTransmitEncrypted(bool encrypted)
    {
        if (transmitEncrypted == encrypted || !CanToggleEncryption || transmitEnabled)
            return;

        transmitEncrypted = encrypted;
        NotifySelectableEncryptionStateChanged();
        TransmitEncryptionChanged?.Invoke(this, transmitEncrypted);
    }

    internal void SetHasCallPriority(bool enabled)
    {
        if (hasCallPriority == enabled)
            return;

        hasCallPriority = enabled;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCallPriority)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPttControlEnabled)));
        (PttCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private void NotifySelectableEncryptionStateChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTransmitEncrypted)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanTransmit)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPttControlEnabled)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanToggleEncryption)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncryptionButtonText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncryptionAutomationName)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncryptionAutomationHelpText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PttAutomationHelpText)));
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
        => SolidBrushCache.Get(color ?? string.Empty, fallback);
}
