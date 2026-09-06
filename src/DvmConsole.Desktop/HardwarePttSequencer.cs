// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;

namespace DvmConsole.Desktop;

internal interface IHardwarePttInputState
{
    bool IsInputPressed(PttTargetScope scope, PttActivationSource source);
    void ReleaseKeyboardToggleLatch(PttTargetScope scope, PttActivationSource source);
    void ReleaseAllKeyboardToggleLatches();
}

internal interface IHardwarePttSequencePort
{
    bool InputsAllowed { get; }
    bool ToggleMode { get; }
    bool HasActiveTransmission { get; }
    IReadOnlyList<ChannelId> GetSelectedTargets(PttTargetScope scope);
    void ReportNoTargets(PttTargetScope scope);
    void ObserveActivation(PttActivationSource source);
    Task StartTargetsAsync(IReadOnlyList<ChannelId> targets);
    Task StopActiveAsync();
}

/// <summary>Serializes input edges with microphone/cue startup and rechecks released input afterward.</summary>
internal sealed class HardwarePttSequencer(
    SemaphoreSlim gate,
    IHardwarePttSequencePort port,
    IHardwarePttInputState input,
    PttActivationArbiter ownership)
{
    public async Task HandleAsync(bool pressed, PttTargetScope scope, PttActivationSource source)
    {
        if (!port.InputsAllowed)
            return;
        await gate.WaitAsync();
        try
        {
            if (!port.InputsAllowed)
                return;
            if (!pressed)
            {
                if (ownership.ShouldReleaseFromInput(scope, source))
                    await port.StopActiveAsync();
                return;
            }
            if (ownership.ShouldReleaseFromKeyboard(port.ToggleMode, port.HasActiveTransmission, source))
            {
                input.ReleaseKeyboardToggleLatch(scope, source);
                await port.StopActiveAsync();
                return;
            }
            port.ObserveActivation(source);
            IReadOnlyList<ChannelId> targets = port.GetSelectedTargets(scope);
            if (targets.Count == 0)
            {
                port.ReportNoTargets(scope);
                return;
            }
            if (port.HasActiveTransmission)
                return;
            await port.StartTargetsAsync(targets);
            if (port.HasActiveTransmission)
                ownership.RecordStarted(source, scope);
            else
            {
                input.ReleaseAllKeyboardToggleLatches();
                ownership.Clear();
            }
            if (!input.IsInputPressed(scope, source) && port.HasActiveTransmission)
                await port.StopActiveAsync();
        }
        finally { gate.Release(); }
    }
}
