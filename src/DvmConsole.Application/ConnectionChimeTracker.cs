// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

// Converts verbose connection status updates into one audible connected edge
// and one audible disconnected edge per system.
public sealed class ConnectionChimeTracker
{
    private readonly Dictionary<string, bool> connectedStates = new(StringComparer.OrdinalIgnoreCase);

    public bool ShouldPlay(string systemName, RadioConnectionState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemName);
        if (state == RadioConnectionState.Connected)
        {
            bool changed = !connectedStates.TryGetValue(systemName, out bool wasConnected) || !wasConnected;
            connectedStates[systemName] = true;
            return changed;
        }

        if (state is not (RadioConnectionState.Disconnected or RadioConnectionState.Faulted))
            return false;

        bool shouldPlay = connectedStates.TryGetValue(systemName, out bool wasPreviouslyConnected) && wasPreviouslyConnected;
        connectedStates[systemName] = false;
        return shouldPlay;
    }
}
