using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

// Converts verbose connection status updates into one audible connected edge
// and one audible disconnected edge per system.
public sealed class ConnectionChimeTracker
{
    private readonly Dictionary<string, bool> connectedStates = new(StringComparer.OrdinalIgnoreCase);

    public bool ShouldPlay(string systemName, FneConnectionState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemName);
        if (state == FneConnectionState.Connected)
        {
            bool changed = !connectedStates.TryGetValue(systemName, out bool wasConnected) || !wasConnected;
            connectedStates[systemName] = true;
            return changed;
        }

        if (state is not (FneConnectionState.Disconnected or FneConnectionState.Faulted))
            return false;

        bool shouldPlay = connectedStates.TryGetValue(systemName, out bool wasPreviouslyConnected) && wasPreviouslyConnected;
        connectedStates[systemName] = false;
        return shouldPlay;
    }
}
