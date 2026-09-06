// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Operations;
using System.ComponentModel;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel
{
    private readonly RuntimeHealthController runtimeHealth;
    private readonly PttActivationArbiter pttActivationArbiter = new();

    public string PttInputSourceText => runtimeHealth.PttInputSourceText;

    public IReadOnlyList<ChannelViewModel> RuntimeActiveTransmitChannels
        => ResolveChannels(transmitCoordinator.ActiveChannels);

    public string MicrophoneInputSourceText
    {
        get
        {
            if (SelectedAudioInputDevice is AudioDeviceOptionViewModel selected)
                return selected.DisplayName;
            return string.IsNullOrWhiteSpace(AudioInputDeviceIdText)
                ? "system default microphone"
                : "configured microphone";
        }
    }

    internal RuntimeHealthSnapshot CaptureRuntimeHealthSnapshot()
    {
        if (networkDisabledDemo && demoRuntimeHealthSnapshot is RuntimeHealthSnapshot demoSnapshot)
            return demoSnapshot with { CapturedAt = DateTimeOffset.UtcNow };

        return runtimeHealth.Capture(new RuntimeHealthCaptureInput(
            DateTimeOffset.UtcNow,
            receiveAudioWork.CaptureHealth(),
            patchSourceReceiveWork.CaptureHealth(),
            transmitCoordinator.MicrophoneHealth,
            transmitCoordinator.QueueHealth,
            patchForwarding.CaptureQueueHealth(),
            transmitCoordinator.IsMicrophoneAudioSuppressed));
    }

    private void ObserveRuntimeReceiveTiming(ReceiveWorkItemTiming timing)
        => runtimeHealth.ObserveReceiveTiming(timing);

    private void ObserveRecordingCatalogHealth(RecordingCatalogScanResult scan)
        => runtimeHealth.ObserveRecordingCatalog(scan);

    private void ObservePttActivationSource(PttActivationSource source)
    {
        if (runtimeHealth.ObservePttActivationSource(source))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PttInputSourceText)));
    }

    private void ObserveTransmitHealthError(Exception exception)
        => runtimeHealth.ObserveTransmitError(exception);

    private void ObserveRouteRecovery(TimeSpan duration, string result)
        => runtimeHealth.ObserveRouteRecovery(duration, result);
}
