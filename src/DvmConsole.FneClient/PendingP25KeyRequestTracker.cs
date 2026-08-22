namespace DvmConsole.FneClient;

internal sealed class PendingP25KeyRequestTracker
{
    public static TimeSpan ResponseWindow { get; } = TimeSpan.FromMinutes(1);

    private readonly object sync = new();
    private readonly TimeProvider timeProvider;
    private readonly Dictionary<(byte AlgorithmId, ushort KeyId), DateTimeOffset> pending = [];

    public PendingP25KeyRequestTracker(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public DateTimeOffset Register(byte algorithmId, ushort keyId)
    {
        DateTimeOffset expiresAt = timeProvider.GetUtcNow().Add(ResponseWindow);
        lock (sync)
            pending[(algorithmId, keyId)] = expiresAt;
        return expiresAt;
    }

    public bool TryCancel(byte algorithmId, ushort keyId, DateTimeOffset expectedExpiry)
    {
        lock (sync)
        {
            if (!pending.TryGetValue((algorithmId, keyId), out DateTimeOffset actualExpiry) ||
                actualExpiry != expectedExpiry)
            {
                return false;
            }

            return pending.Remove((algorithmId, keyId));
        }
    }

    public bool TryConsume(byte algorithmId, ushort keyId)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        lock (sync)
        {
            return pending.Remove((algorithmId, keyId), out DateTimeOffset expiresAt) &&
                expiresAt >= now;
        }
    }

    public void Clear()
    {
        lock (sync)
            pending.Clear();
    }
}
