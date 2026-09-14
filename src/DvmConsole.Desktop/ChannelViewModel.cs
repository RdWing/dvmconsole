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

public sealed partial class ChannelViewModel :
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
        return channel.SessionState.CaptureRecordingDescriptor();
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
    internal ConsoleChannelState SessionState { get; }
    internal ChannelOperatorState OperatorState { get; }
    private readonly ChannelDefinition sessionDefinition;
    private readonly ChannelConfigurationAccess configurationAccess;
    private readonly RadioAliasIndex aliases;
    private Func<ChannelViewModel, Task>? startAudio;
    private Func<ChannelViewModel, Task>? stopAudio;
    private Func<ChannelViewModel, Task>? startTransmit;
    private Func<ChannelViewModel, Task>? stopTransmit;
    private bool audioEnabled => OperatorState.Snapshot.AudioEnabled;
    private bool audioSuspended => OperatorState.Snapshot.AudioSuspended;
    private bool audioBusy;
    private bool transmitEnabled => OperatorState.Snapshot.TransmitEnabled;
    private bool transmitStarting => OperatorState.Snapshot.TransmitStarting;
    private bool transmitStopping => OperatorState.Snapshot.TransmitStopping;
    private bool transmitSelected => OperatorState.Snapshot.TransmitSelected;
    private bool pageSelected => OperatorState.Snapshot.PageSelected;
    private bool alertSelected => OperatorState.Snapshot.AlertSelected;
    private bool transmitBusy;
    private bool transmitEncrypted => OperatorState.Snapshot.TransmitEncrypted;
    private bool hasCallPriority => OperatorState.Snapshot.HasCallPriority;
    private FneTalkgroupAvailability talkgroupAvailability => SessionState.Authority switch
    {
        TargetAuthorityState.Available => FneTalkgroupAvailability.Available,
        TargetAuthorityState.Unavailable => FneTalkgroupAvailability.Unavailable,
        _ => FneTalkgroupAvailability.Pending
    };
    private bool recordingEnabled => OperatorState.Snapshot.RecordingEnabled;
    private double volume => OperatorState.Snapshot.Gain;
    private double stereoBalance => OperatorState.Snapshot.Balance;
    private string ignoredSubscriberIdsText = string.Empty;
    private string outputDeviceIdText => OperatorState.Snapshot.OutputRoute;
    private IReadOnlyList<AudioDeviceOptionViewModel> outputDeviceOptions = [];
    private double widgetX;
    private double widgetY;
    private bool darkMode;

    public ChannelViewModel(
        ChannelConfiguration configuration,
        IP25KeyResolver? p25KeyResolver = null,
        IEnumerable<RadioAlias>? aliases = null,
        IDmrKeyResolver? dmrKeyResolver = null,
        INxdnKeyResolver? nxdnKeyResolver = null,
        ConsoleChannelState? sessionState = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        this.configuration = configuration;
        this.aliases = aliases as RadioAliasIndex ?? new RadioAliasIndex(aliases);
        ChannelRuntimeDefinition definition = ChannelRuntimeDefinition.FromConfiguration(configuration);
        sessionState ??= new ConsoleChannelState(definition);
        if (sessionState.Runtime.Definition != definition)
            throw new ArgumentException("The channel state does not match its configuration.", nameof(sessionState));
        SessionState = sessionState;
        runtime = sessionState.Runtime;
        configurationAccess = new ChannelConfigurationAccess(definition, p25KeyResolver, dmrKeyResolver, nxdnKeyResolver);
        sessionDefinition = sessionState.Identity;
        OperatorState = sessionState.Operator;
        presentedOperator = OperatorState.Snapshot;
        presentedAuthority = SessionState.Authority;
        presentedPlayback = SessionState.Receive.Playback;
        presentedReceiveEncrypted = SessionState.Receive.ObservedEncrypted;
        runtime.PropertyChanged += HandleRuntimePropertyChanged;
        AudioCommand = new AsyncRelayCommand(() => Task.CompletedTask, () => false);
        PttCommand = new AsyncRelayCommand(() => Task.CompletedTask, () => false);
        EncryptionCommand = new AsyncRelayCommand(ToggleEncryptionAsync, () => CanToggleEncryption && !transmitBusy && !audioBusy);
        RecordingCommand = new AsyncRelayCommand(ToggleRecordingAsync, () => CanRecord);
        SessionState.Meter.Changed += HandleMeterChanged;
        OperatorState.Changed += HandleOperatorStateChanged;
        SessionState.AuthorityChanged += HandleAuthorityChanged;
        SessionState.Receive.Changed += HandleReceiveStateChanged;
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
    public string SettingsKey => SessionState.SettingsKey;
    public string SystemName => runtime.Definition.SystemName;
    public uint DestinationId => runtime.Definition.DestinationId;
    public string ModeText => runtime.Definition.Mode.ToUpperInvariant();
    public string TalkgroupText => $"TG {runtime.Definition.DestinationId} - {ModeText}";
    public string DestinationText => $"{runtime.Definition.SystemName} / TGID {runtime.Definition.DestinationId}";
    internal RadioAliasIndex AliasIndex => aliases;
    internal ChannelConfigurationAccess ConfigurationAccess => configurationAccess;
    public string LastCallerText => ConsoleChannelSnapshotProjector.LastCallerText(SessionState, aliases);
    public string LastCallerDisplayText => $"Last: {LastCallerText}";
    public double AudioLevel => SessionState.Meter.Snapshot.Rms;
    public AudioMeterState AudioMeter
    {
        get
        {
            var meter = SessionState.Meter.Snapshot;
            return new(meter.Rms, meter.Peak);
        }
    }
    public double AudioPeakLevel => SessionState.Meter.Snapshot.Peak;
    public double CardWidth => ResolveCardWidth(configuration.CardSize);
    internal string? ConfiguredCardSize => configuration.CardSize;
    internal static double ResolveCardWidth(string? cardSize)
        => DvmConsole.Presentation.ConsoleCardGeometry.ResolveWidth(cardSize);
    public double CardContentWidth => CardWidth - 12;
    public double AudioMeterWidth => DvmConsole.Presentation.ConsoleCardGeometry.MeterWidth(CardWidth);
    public double WidgetX => widgetX;
    public double WidgetY => widgetY;
    private ConsoleCardActivity CardActivity => transmitStarting ? ConsoleCardActivity.TransmitStarting
        : runtime.State == ChannelRuntimeState.Transmitting ? ConsoleCardActivity.Transmitting
        : IsReceivePresentationActive ? ConsoleCardActivity.Receiving
        : audioEnabled ? ConsoleCardActivity.Listening : ConsoleCardActivity.Idle;
    public IBrush CardBackgroundBrush => ConsoleCardPalette.Background(darkMode, CardActivity);
    public IBrush CardBorderBrush => CardActivity == ConsoleCardActivity.Idle
        ? CreateBrush(configuration.ResourceColor, darkMode ? "#2A3A4B" : "#9BA8B5")
        : ConsoleCardPalette.Border(darkMode, CardActivity);
    public IBrush CardTextBrush => ConsoleCardPalette.Text(darkMode, CardActivity);
    public string StateText => ConsoleChannelSnapshotProjector.StateText(SessionState, aliases);
    public ChannelRuntimeState State => runtime.State;
    public uint? SourceId => runtime.SourceId;
    public uint? StreamId => runtime.StreamId;
    public ChannelRuntimeDefinition Definition => runtime.Definition;
    public ChannelSessionId SessionId => sessionDefinition.SessionId;
    public ChannelDefinition SessionDefinition => sessionDefinition;
    public bool IsAudioEnabled => audioEnabled;
    public string ReceiveAutomationName => $"{Name}; receive {(audioEnabled ? "enabled" : "disabled")}";
    public bool IsAudioSuspended => audioSuspended;
    public bool IsReceivePresentationActive => SessionState.ReceivePresentationOwner is not null;
    public string AudioButtonText => audioSuspended ? "RX muted" : audioEnabled ? "Stop audio" : "Listen";
    public bool IsTransmitting => transmitEnabled;
    public bool IsTransmitStarting => transmitStarting;
    public bool IsTransmitStopping => transmitStopping;
    public bool IsTransmitSelected => transmitSelected;
    public bool IsPageSelected => pageSelected;
    public bool IsAlertSelected => alertSelected;
    public bool IsTransmitEncrypted => transmitEncrypted;
    public bool HasCallPriority => hasCallPriority;
    public bool ObservedReceiveEncrypted => SessionState.Receive.ObservedEncrypted;
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
    public long IgnoredLatePacketCount => SessionState.Receive.IgnoredLatePackets;
    public long DroppedReceiveFrameCount => SessionState.Receive.DroppedFrames;

    internal bool IsTrackingReceiveStream(uint streamId)
        => SessionState.Receive.IsTracking(streamId);

    internal string ResolveSubscriberAlias(uint sourceId)
        => AliasFileLoader.FindAlias(aliases, sourceId);

    internal void RecordIgnoredLatePacket()
        => SessionState.Receive.RecordIgnoredLatePacket();

    internal void RecordDroppedReceiveFrame()
        => SessionState.Receive.RecordDroppedFrame();
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
            if (!OperatorState.SetOutputRoute(value))
                return;
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
    public bool CanToggleEncryption => configurationAccess.CanToggleEncryption(transmitEncrypted);
    internal bool TransmitKeyAvailable => configurationAccess.TransmitKeyAvailable;
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
    public bool CanListen => configurationAccess.CanListen;
    internal bool CanTransmitByConfiguration => configurationAccess.CanTransmit(transmitEncrypted);
    public bool CanTransmit =>
        CanTransmitByConfiguration &&
        talkgroupAvailability != FneTalkgroupAvailability.Unavailable;
    public bool IsPttControlEnabled =>
        transmitEnabled ||
        (CanTransmit && (hasCallPriority || !IsReceivePresentationActive));
    public FneTalkgroupAvailability TalkgroupAvailability => talkgroupAvailability;
    public bool IsTalkgroupUnavailable =>
        talkgroupAvailability == FneTalkgroupAvailability.Unavailable;
    public string TalkgroupUnavailableReason => configurationAccess.AuthorityUnavailableReason;
    internal string ConfigurationTransmitUnavailableReason => configurationAccess.TransmitUnavailableReason(transmitEncrypted);
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

    private bool CanResolveConfiguredKey() => configurationAccess.ConfiguredKeyAvailable;

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
    public IBrush TransmitSelectionBrush => ConsoleCardPalette.Selection(darkMode, ConsoleCardSelection.Transmit, transmitSelected);
    public IBrush TransmitSelectionBorderBrush => ConsoleCardPalette.Selection(darkMode, ConsoleCardSelection.Transmit, transmitSelected, border: true);
    public IBrush PageSelectionBrush => ConsoleCardPalette.Selection(darkMode, ConsoleCardSelection.Page, pageSelected);
    public IBrush PageSelectionBorderBrush => ConsoleCardPalette.Selection(darkMode, ConsoleCardSelection.Page, pageSelected, border: true);
    public IBrush AlertSelectionBrush => ConsoleCardPalette.Selection(darkMode, ConsoleCardSelection.Alert, alertSelected);
    public IBrush AlertSelectionBorderBrush => ConsoleCardPalette.Selection(darkMode, ConsoleCardSelection.Alert, alertSelected, border: true);
    public IBrush RecordingSelectionBrush => ConsoleCardPalette.Selection(darkMode, ConsoleCardSelection.Recording, recordingEnabled);
    public IBrush RecordingSelectionBorderBrush => ConsoleCardPalette.Selection(darkMode, ConsoleCardSelection.Recording, recordingEnabled, border: true);
    public IBrush EncryptionSelectionBrush => ConsoleCardPalette.Selection(darkMode, ConsoleCardSelection.Encryption, transmitEncrypted);
    public IBrush EncryptionSelectionBorderBrush => ConsoleCardPalette.Selection(darkMode, ConsoleCardSelection.Encryption, transmitEncrypted, border: true);
    public IBrush EncryptionSelectionTextBrush => ConsoleCardPalette.EncryptionText(darkMode, transmitEncrypted);
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

        SessionState.SetAuthority(availability switch
        {
            FneTalkgroupAvailability.Available => TargetAuthorityState.Available,
            FneTalkgroupAvailability.Unavailable => TargetAuthorityState.Unavailable,
            _ => TargetAuthorityState.Pending
        });
    }

    public void RestoreTransmitEncryption(bool encrypted)
    {
        if (!runtime.Definition.IsEncrypted || !runtime.Definition.SelectableEncryption)
            return;

        OperatorState.SetTransmitEncrypted(encrypted);
    }

    public void SetRecordingEnabled(bool enabled, [CallerMemberName] string origin = "")
        => SetRecordingEnabledCore(enabled, raiseStateChanged: true, origin);

    public void RestoreRecordingEnabled(bool enabled, [CallerMemberName] string origin = "")
        => SetRecordingEnabledCore(enabled, raiseStateChanged: false, $"settings restore: {origin}");

    private void SetRecordingEnabledCore(bool enabled, bool raiseStateChanged, string origin)
    {
        if (recordingEnabled == enabled)
            return;

        bool previous = OperatorState.Snapshot.RecordingEnabled;
        OperatorState.SetRecordingEnabled(enabled);
        ObserveSelectionChange(ChannelSelectionKind.Recording, previous, enabled, origin);
        if (raiseStateChanged)
            RecordingStateChanged?.Invoke(this, enabled);
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
        if (!OperatorState.SetGain(value))
            return;
        double normalized = volume;
        if (raiseChanged)
        {
            VolumeChanged?.Invoke(this, normalized);
        }
    }

    private void SetStereoBalance(double value, bool raiseChanged)
    {
        if (!OperatorState.SetBalance(value))
            return;
        double normalized = stereoBalance;
        if (raiseChanged)
            StereoBalanceChanged?.Invoke(this, normalized);
    }

    public void SetIgnoredSubscriberIds(IEnumerable<uint> subscriberIds)
    {
        ArgumentNullException.ThrowIfNull(subscriberIds);
        SessionState.RecordingSubscribers.Replace(subscriberIds);
        IgnoredSubscriberIdsText = string.Join(", ", SessionState.RecordingSubscribers.IgnoredSubscribers);
    }

    internal void MarkReceivePlaybackActive(uint sourceId, uint streamId)
        => SessionState.TryBeginReceivePlayback(sourceId, streamId);

    internal void MarkReceivePlaybackEnded(uint streamId)
        => SessionState.EndReceivePlayback(streamId);

    // The decoder path can lead the UI-thread lifecycle pass during a traffic
    // burst. Track its first audible stream without raising properties from a
    // worker thread so early meter samples remain eligible for the next UI
    // refresh instead of being discarded.
    internal void MarkReceiveAudioMeterActive(uint streamId)
        => SessionState.MarkReceiveMeter(streamId, ended: false);

    internal void MarkReceiveAudioMeterEnded(uint streamId)
        => SessionState.MarkReceiveMeter(streamId, ended: true);

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


    public void SetAudioEnabled(bool enabled, [CallerMemberName] string origin = "")
    {
        bool previous = IsAudioEnabled;
        if (!SessionState.SetReceiveEnabled(enabled)) return;
        PresentReceiveSelection(previous, enabled, origin);
    }

    internal void PresentReceiveSelection(bool previous, bool enabled, string origin)
    {
        if (previous != enabled)
            ObserveSelectionChange(ChannelSelectionKind.Receive, previous, enabled, origin);
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
        => SessionState.SetAudioSuspended(suspended);

    public void SetAudioLevel(
        double value,
        ChannelAudioDirection? direction = null,
        uint? streamId = null,
        double? peakValue = null)
    {
        if (ChannelAudioMeterProjection.Project(SessionState, value, peakValue, direction, streamId) is { } levels)
            ApplyAudioLevel(levels.Rms, levels.Peak);
    }

    internal void SetPresentedReceiveAudioLevel(double value, double? peakValue = null)
    {
        var levels = ChannelAudioMeterProjection.ProjectPresentedReceive(SessionState, value, peakValue);
        ApplyAudioLevel(levels.Rms, levels.Peak);
    }

    private void ApplyAudioLevel(double normalized, double normalizedPeak)
        => SessionState.Meter.Update(normalized, normalizedPeak, minimumChange: 0.25);

    public void SetTransmitEnabled(bool enabled, uint streamId = 0)
    {
        SetTransmitTransition(starting: false, stopping: false);
        if (!SessionState.SetTransmitEnabled(enabled, streamId))
        {
            (EncryptionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            return;
        }
    }

    internal void SetTransmitStarting(bool starting)
        => SetTransmitTransition(starting, stopping: false);

    internal void SetTransmitStopping(bool stopping)
        => SetTransmitTransition(starting: false, stopping);

    private void SetTransmitTransition(bool starting, bool stopping)
        => OperatorState.SetTransmitTransition(starting, stopping);

    public void SetTransmitSelected(bool selected)
    {
        if (transmitSelected == selected)
            return;
        OperatorState.SetTransmitSelected(selected);
    }

    public void SetPageSelected(bool selected)
    {
        if (pageSelected == selected)
            return;
        OperatorState.SetPageSelected(selected);
    }

    public void SetAlertSelected(bool selected)
    {
        if (alertSelected == selected)
            return;
        OperatorState.SetAlertSelected(selected);
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
        => PresentReceiveProjection(ChannelReceiveProjection.Apply(
            SessionState, systemName, traffic, now, ingressDecision));

    private bool CanProjectTraffic(string systemName, FneTrafficFrame traffic, bool isTrackedReceiveStream)
        => SessionState.Receive.CanProjectTraffic(systemName, traffic, isTrackedReceiveStream);

    private ChannelTrafficApplyResult ProjectTraffic(
        FneTrafficFrame traffic, DateTimeOffset now, ReceiveRouteProjectionDecision projection)
        => PresentReceiveProjection(ChannelReceiveProjection.Apply(SessionState, traffic, now, projection));

    internal ChannelTrafficApplyResult PresentReceiveProjection(ChannelReceiveProjectionResult result)
        => result.Decision is { } matched ? ToApplyResult(matched) : ChannelTrafficApplyResult.NoMatch;

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
        => PresentReceiveProjection(ChannelReceiveProjection.Advance(SessionState, projection, now));

    private static ChannelTrafficApplyResult ToApplyResult(ReceiveStreamDecision decision)
        => new(
            Matched: decision.Transition is not ReceiveStreamTransition.None,
            decision.Transition,
            decision.ActiveStreamId,
            decision.EndedStreamId,
            decision.EndedAt);

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

    private Func<bool, Task>? setTransmitEncryption;

    internal void ConfigureEncryptionCommand(Func<bool, Task> setEncryption)
        => setTransmitEncryption = setEncryption ?? throw new ArgumentNullException(nameof(setEncryption));

    private Task ToggleEncryptionAsync()
    {
        if (setTransmitEncryption is not null) return setTransmitEncryption(!transmitEncrypted);
        SetTransmitEncrypted(!transmitEncrypted);
        return Task.CompletedTask;
    }

    internal void SetTransmitEncrypted(bool encrypted)
    {
        if (!configurationAccess.CanChangeEncryption(OperatorState.Snapshot, encrypted))
            return;

        OperatorState.SetTransmitEncrypted(encrypted);
        TransmitEncryptionChanged?.Invoke(this, transmitEncrypted);
    }

    internal void SetHasCallPriority(bool enabled)
    {
        if (hasCallPriority == enabled)
            return;

        OperatorState.SetHasCallPriority(enabled);
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

    private Func<bool, Task>? setRecordingEnabled;

    internal void ConfigureRecordingCommand(Func<bool, Task> setRecording)
        => setRecordingEnabled = setRecording ?? throw new ArgumentNullException(nameof(setRecording));

    private Task ToggleRecordingAsync()
    {
        if (setRecordingEnabled is not null) return setRecordingEnabled(!recordingEnabled);
        if (!CanRecord && !recordingEnabled)
            return Task.CompletedTask;

        SetRecordingEnabled(!recordingEnabled);
        return Task.CompletedTask;
    }

    private void HandleRuntimePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ChannelRuntime.LastActivity) || Volatile.Read(ref statePresentationDetached) != 0) return;
        if (stateDispatcher is null || stateDispatcher.CheckAccess()) PresentRuntimeProperty(args);
        else
        {
            Interlocked.Exchange(ref runtimePresentationPending, 1);
            RequestStatePresentation();
        }
    }

    private void PresentRuntimeProperty(PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ChannelRuntime.LastActivity) || Volatile.Read(ref statePresentationDetached) != 0)
            return;
        PropertyChanged?.Invoke(this, args);

        bool callerChanged = args.PropertyName is nameof(ChannelRuntime.State) or nameof(ChannelRuntime.SourceId);
        if (args.PropertyName == nameof(ChannelRuntime.State) &&
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
