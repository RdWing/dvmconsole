// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

internal readonly record struct ConsoleConnectionChange(bool Changed, bool LostConnection);

internal sealed partial class ConsoleOperationalRuntime
{
    private readonly object connectionStateSync = new();
    private readonly Dictionary<SystemId, RadioConnectionState> connectionStates = [];
    public ReceiveBufferingRuntime Buffering { get; } = new();
    public ReceiveJitterBufferEffectivenessTracker JitterEffectiveness { get; } = new();

    /// <summary>Ends connection-scoped receive learning before presentation can observe the transition.</summary>
    public ConsoleConnectionChange ObserveConnectionState(SystemId id, string name, RadioConnectionState state)
    {
        lock (connectionStateSync)
        {
            bool known = connectionStates.TryGetValue(id, out RadioConnectionState previous);
            bool changed = !known || previous != state;
            bool lost = known && previous == RadioConnectionState.Connected && state != RadioConnectionState.Connected;
            connectionStates[id] = state;
            if (lost)
            {
                Buffering.Reset(name);
                JitterEffectiveness.Reset(name);
            }
            return new(changed, lost);
        }
    }
}
