// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>Shared operator wording for portable radio connection transitions.</summary>
internal static class RadioConnectionStatus
{
    public static string Format(RadioConnectionTransition transition) => transition.Kind switch
    {
        RadioConnectionTransitionKind.StartingAll => "Starting FNE connection services...",
        RadioConnectionTransitionKind.StartedAll => "FNE connection services started; waiting for login acknowledgements.",
        RadioConnectionTransitionKind.StoppingAll => "Stopping FNE connection services...",
        RadioConnectionTransitionKind.StoppedAll => "FNE connections stopped.",
        RadioConnectionTransitionKind.StartingSystem => $"Starting {transition.SystemName}...",
        RadioConnectionTransitionKind.StoppingSystem => $"Stopping {transition.SystemName}...",
        RadioConnectionTransitionKind.SystemStopped => $"{transition.SystemName}: disconnected.",
        RadioConnectionTransitionKind.SystemStartFaulted => $"{transition.SystemName}: {transition.Exception?.Message ?? "The radio connection could not start."}",
        RadioConnectionTransitionKind.SystemStopFaulted => $"{transition.SystemName}: disconnect failed — {transition.Exception?.Message}",
        _ => throw new ArgumentOutOfRangeException(nameof(transition))
    };
}
