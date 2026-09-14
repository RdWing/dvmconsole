// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using DvmConsole.Application;
using DvmConsole.Core.Runtime;

namespace DvmConsole.Desktop;

public sealed partial class ChannelViewModel
{
    private ChannelOperatorSnapshot presentedOperator;
    private TargetAuthorityState presentedAuthority;
    private IUiDispatcher? stateDispatcher;
    private CoalescedUiAction? stateRefresh;
    private CoalescedUiAction? meterRefresh;
    private int statePresentationDetached;
    private int runtimePresentationPending;
    private ReceivePlaybackIdentity? presentedPlayback;
    private bool presentedReceiveEncrypted;

    internal void ConfigureStatePresentation(IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        stateRefresh?.Dispose();
        meterRefresh?.Dispose();
        stateDispatcher = dispatcher;
        stateRefresh = new CoalescedUiAction(dispatcher, PublishOperatorPresentation);
        meterRefresh = new CoalescedUiAction(dispatcher, PublishMeterPresentation);
    }

    internal void DetachSessionState()
    {
        if (Interlocked.Exchange(ref statePresentationDetached, 1) != 0) return;
        stateRefresh?.Dispose();
        meterRefresh?.Dispose();
        SessionState.Meter.Changed -= HandleMeterChanged;
        OperatorState.Changed -= HandleOperatorStateChanged;
        SessionState.AuthorityChanged -= HandleAuthorityChanged;
        SessionState.Receive.Changed -= HandleReceiveStateChanged;
        runtime.PropertyChanged -= HandleRuntimePropertyChanged;
    }

    private void HandleMeterChanged(object? sender, ChannelAudioMeterLevels levels)
    {
        if (Volatile.Read(ref statePresentationDetached) != 0) return;
        if (stateDispatcher is null || stateDispatcher.CheckAccess()) PublishMeterPresentation();
        else meterRefresh!.Schedule();
    }

    private void PublishMeterPresentation()
    {
        if (Volatile.Read(ref statePresentationDetached) == 0)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AudioMeter)));
    }

    private void HandleOperatorStateChanged(object? sender, ChannelOperatorSnapshot snapshot) => RequestStatePresentation();
    private void HandleAuthorityChanged(object? sender, EventArgs args) => RequestStatePresentation();
    private void HandleReceiveStateChanged(object? sender, EventArgs args) => RequestStatePresentation();

    private void RequestStatePresentation()
    {
        if (Volatile.Read(ref statePresentationDetached) != 0) return;
        if (stateDispatcher is null || stateDispatcher.CheckAccess()) PublishOperatorPresentation();
        else stateRefresh!.Schedule();
    }

    private void PublishOperatorPresentation()
    {
        if (Volatile.Read(ref statePresentationDetached) != 0) return;
        ChannelOperatorSnapshot previous = presentedOperator;
        ChannelOperatorSnapshot current = OperatorState.Snapshot;
        TargetAuthorityState authority = SessionState.Authority;
        bool authorityChanged = presentedAuthority != authority;
        ReceivePlaybackIdentity? playback = SessionState.Receive.Playback;
        bool receiveEncrypted = SessionState.Receive.ObservedEncrypted;
        bool playbackChanged = presentedPlayback != playback;
        bool receiveEncryptionChanged = presentedReceiveEncrypted != receiveEncrypted;
        presentedOperator = current;
        presentedAuthority = authority;
        presentedPlayback = playback;
        presentedReceiveEncrypted = receiveEncrypted;
        if (current.Gain != previous.Gain) NotifyStateProperties(GainProperties);
        if (current.Balance != previous.Balance) NotifyStateProperties(BalanceProperties);
        if (current.OutputRoute != previous.OutputRoute) NotifyStateProperties(RouteProperties);
        if (current.TransmitSelected != previous.TransmitSelected) NotifyStateProperties(TransmitSelectionProperties);
        if (current.PageSelected != previous.PageSelected) NotifyStateProperties(PageSelectionProperties);
        if (current.AlertSelected != previous.AlertSelected) NotifyStateProperties(AlertSelectionProperties);
        if (current.RecordingEnabled != previous.RecordingEnabled)
        {
            NotifyStateProperties(RecordingProperties);
            (RecordingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
        if (current.AudioEnabled != previous.AudioEnabled || current.AudioSuspended != previous.AudioSuspended)
        {
            NotifyStateProperties(ReceiveProperties);
            NotifyReceivePresentationChanged();
            if (!current.AudioEnabled || current.AudioSuspended) SetAudioLevel(0);
        }
        if (current.TransmitEnabled != previous.TransmitEnabled)
        {
            NotifyStateProperties(TransmitProperties);
            (PttCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (EncryptionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
        if (current.TransmitStarting != previous.TransmitStarting) NotifyStateProperties(StartingProperties);
        if (current.TransmitStopping != previous.TransmitStopping) NotifyStateProperties(StoppingProperties);
        if (current.TransmitStarting != previous.TransmitStarting || current.TransmitStopping != previous.TransmitStopping)
            NotifyStateProperties(TransitionProperties);
        if (current.TransmitEncrypted != previous.TransmitEncrypted) NotifySelectableEncryptionStateChanged();
        if (current.HasCallPriority != previous.HasCallPriority)
        {
            NotifyStateProperties(PriorityProperties);
            (PttCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
        if (authorityChanged)
        {
            NotifyStateProperties(AuthorityProperties);
            (PttCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
        if (playbackChanged) NotifyReceivePresentationChanged();
        if (receiveEncryptionChanged) NotifyStateProperties(ReceiveEncryptionProperties);
        if (Interlocked.Exchange(ref runtimePresentationPending, 0) != 0)
            foreach (var property in RuntimeProperties) PresentRuntimeProperty(property);
    }

    private void NotifyStateProperties(PropertyChangedEventArgs[] properties)
    {
        foreach (var property in properties) PropertyChanged?.Invoke(this, property);
    }

    // Static groups avoid per-update notification arrays and event-argument allocations.
    private static PropertyChangedEventArgs[] Properties(params string[] names)
        => names.Select(name => new PropertyChangedEventArgs(name)).ToArray();

    private static readonly PropertyChangedEventArgs[] RuntimeProperties = Properties(
        nameof(ChannelRuntime.State), nameof(ChannelRuntime.StateText), nameof(ChannelRuntime.SourceId),
        nameof(ChannelRuntime.StreamId), nameof(ChannelRuntime.FaultMessage));
    private static readonly PropertyChangedEventArgs[] ReceiveEncryptionProperties = Properties(nameof(ObservedReceiveEncrypted));
    private static readonly PropertyChangedEventArgs[] RouteProperties = Properties(
        nameof(OutputDeviceIdText));
    private static readonly PropertyChangedEventArgs[] AuthorityProperties = Properties(
        nameof(TalkgroupAvailability), nameof(IsTalkgroupUnavailable), nameof(TransmitUnavailableReason),
        nameof(CanTransmit), nameof(IsPttControlEnabled), nameof(PttAutomationHelpText));
    private static readonly PropertyChangedEventArgs[] RecordingProperties = Properties(
        nameof(IsRecordingEnabled), nameof(RecordButtonText), nameof(RecordingAutomationName),
        nameof(RecordingAutomationHelpText), nameof(RecordingConfigurationButtonText),
        nameof(RecordingSelectionBrush), nameof(RecordingSelectionBorderBrush));
    private static readonly PropertyChangedEventArgs[] GainProperties = Properties(
        nameof(Volume), nameof(VolumeSliderValue));
    private static readonly PropertyChangedEventArgs[] BalanceProperties = Properties(
        nameof(StereoBalance), nameof(StereoBalanceText));
    private static readonly PropertyChangedEventArgs[] ReceiveProperties = Properties(
        nameof(IsAudioEnabled), nameof(ReceiveAutomationName), nameof(IsAudioSuspended), nameof(AudioButtonText));
    private static readonly PropertyChangedEventArgs[] TransmitProperties = Properties(
        nameof(IsTransmitting), nameof(IsPttControlEnabled), nameof(PttButtonText), nameof(PttAutomationName),
        nameof(PttAutomationHelpText));
    private static readonly PropertyChangedEventArgs[] TransmitSelectionProperties = Properties(
        nameof(IsTransmitSelected), nameof(TransmitSelectionAutomationName), nameof(TransmitSelectionText),
        nameof(TransmitSelectionBrush), nameof(TransmitSelectionBorderBrush));
    private static readonly PropertyChangedEventArgs[] PageSelectionProperties = Properties(
        nameof(IsPageSelected), nameof(PageSelectionAutomationName), nameof(PageSelectionText),
        nameof(PageSelectionBrush), nameof(PageSelectionBorderBrush));
    private static readonly PropertyChangedEventArgs[] AlertSelectionProperties = Properties(
        nameof(IsAlertSelected), nameof(AlertSelectionAutomationName), nameof(AlertSelectionText),
        nameof(AlertSelectionBrush), nameof(AlertSelectionBorderBrush));
    private static readonly PropertyChangedEventArgs[] PriorityProperties = Properties(
        nameof(HasCallPriority), nameof(IsPttControlEnabled));
    private static readonly PropertyChangedEventArgs[] StartingProperties = Properties(
        nameof(IsTransmitStarting), nameof(CardBackgroundBrush), nameof(CardTextBrush));
    private static readonly PropertyChangedEventArgs[] StoppingProperties = Properties(
        nameof(IsTransmitStopping));
    private static readonly PropertyChangedEventArgs[] TransitionProperties = Properties(
        nameof(StateText), nameof(CardBorderBrush));
}
