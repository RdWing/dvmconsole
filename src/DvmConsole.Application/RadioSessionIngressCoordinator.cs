namespace DvmConsole.Application;

/// <summary>
/// Owns radio-session ingress subscriptions and publishes only records whose
/// stable system identity matches the registered session. Protocol adapters
/// remain responsible for translating their native events into these records.
/// </summary>
public sealed class RadioSessionIngressCoordinator : IDisposable
{
    private readonly IReadOnlyDictionary<SystemId, IRadioSession> sessions;
    private int disposed;

    public RadioSessionIngressCoordinator(IEnumerable<IRadioSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        this.sessions = sessions.ToDictionary(session => session.SystemId);
        foreach (IRadioSession session in this.sessions.Values)
        {
            session.TrafficReceived += HandleTrafficReceived;
            session.AuthorityChanged += HandleAuthorityChanged;
        }
    }

    public event EventHandler<RadioTrafficRecord>? TrafficReceived;

    public event EventHandler<TalkgroupAuthorityRecord>? AuthorityChanged;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        foreach (IRadioSession session in sessions.Values)
        {
            session.TrafficReceived -= HandleTrafficReceived;
            session.AuthorityChanged -= HandleAuthorityChanged;
        }
    }

    private void HandleTrafficReceived(object? sender, RadioTrafficRecord traffic)
    {
        if (!IsRegisteredSender(sender, traffic.SystemId))
            return;

        TrafficReceived?.Invoke(this, traffic);
    }

    private void HandleAuthorityChanged(object? sender, TalkgroupAuthorityRecord authority)
    {
        if (!IsRegisteredSender(sender, authority.SystemId))
            return;

        AuthorityChanged?.Invoke(this, authority);
    }

    private bool IsRegisteredSender(object? sender, SystemId systemId)
        => Volatile.Read(ref disposed) == 0 &&
            sender is IRadioSession session &&
            sessions.TryGetValue(systemId, out IRadioSession? registered) &&
            ReferenceEquals(registered, session) &&
            session.SystemId == systemId;
}
