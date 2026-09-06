// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel : IHardwarePttSequencePort
{
    private void HandleGlobalPttCaptureFailed(object? sender, Exception failure)
        => sessionUiCallbacks.Post(() => TransmitStatusText =
            $"Global PTT capture lost; reactivate the keyboard binding: {failure.Message}");

    bool IHardwarePttSequencePort.InputsAllowed
        => !terminalFence.IsClosed && Volatile.Read(ref sessionInputSuppressed) == 0 &&
           Volatile.Read(ref disposeStarted) == 0;
    bool IHardwarePttSequencePort.ToggleMode => TogglePttMode;
    bool IHardwarePttSequencePort.HasActiveTransmission => transmitCoordinator.ActiveChannel is not null;
    IReadOnlyList<ChannelId> IHardwarePttSequencePort.GetSelectedTargets(PttTargetScope scope)
        => GetSelectedTransmitTargets(scope).Select(channel => new ChannelId(channel.SessionId)).ToArray();
    void IHardwarePttSequencePort.ReportNoTargets(PttTargetScope scope)
        => TransmitStatusText = scope == PttTargetScope.ActiveSystem
            ? $"Choose TX on one or more cards in {SelectedSystemName} before using {ActiveSystemPttKeyText}."
            : $"Choose TX on one or more cards before using {GlobalPttKeyText}.";
    void IHardwarePttSequencePort.ObserveActivation(PttActivationSource source)
        => ObservePttActivationSource(source);
    Task IHardwarePttSequencePort.StartTargetsAsync(IReadOnlyList<ChannelId> targets)
        => StartTransmitAsync(ResolveChannels(targets));
    async Task IHardwarePttSequencePort.StopActiveAsync()
    {
        ChannelViewModel[] active = ResolveChannels(transmitCoordinator.ActiveChannels);
        if (active.Length > 0)
            await StopTransmitAsync(active);
    }
}
