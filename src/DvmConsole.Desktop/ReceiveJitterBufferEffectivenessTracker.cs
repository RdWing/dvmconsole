namespace DvmConsole.Desktop;

internal readonly record struct ReceiveJitterBufferEffectiveness(
    long RestoredDelayedPackets,
    long DeadlineMissedPackets);

// Retains effectiveness counters for one FNE connection lifetime. Stream
// diagnostics may expire at call end, but the operator summary should continue
// to describe completed calls until that connection is reset.
internal sealed class ReceiveJitterBufferEffectivenessTracker
{
    private readonly object sync = new();
    private readonly Dictionary<string, ReceiveJitterBufferEffectiveness> connections =
        new(StringComparer.OrdinalIgnoreCase);

    public void Observe(string connectionName, ReceiveWorkItemTiming timing)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        if (!timing.JitterBufferReorderedPacket && timing.JitterBufferDeadlineMissedPackets <= 0)
            return;

        lock (sync)
        {
            connections.TryGetValue(connectionName, out ReceiveJitterBufferEffectiveness current);
            long deadlineMisses = Math.Max(0, timing.JitterBufferDeadlineMissedPackets);
            connections[connectionName] = new ReceiveJitterBufferEffectiveness(
                AddSaturating(current.RestoredDelayedPackets, timing.JitterBufferReorderedPacket ? 1 : 0),
                AddSaturating(current.DeadlineMissedPackets, deadlineMisses));
        }
    }

    public ReceiveJitterBufferEffectiveness GetSnapshot(string connectionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        lock (sync)
            return connections.TryGetValue(connectionName, out ReceiveJitterBufferEffectiveness current)
                ? current
                : default;
    }

    public void Reset(string connectionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        lock (sync)
            connections.Remove(connectionName);
    }

    private static long AddSaturating(long left, long right)
        => left >= long.MaxValue - right ? long.MaxValue : left + right;
}
