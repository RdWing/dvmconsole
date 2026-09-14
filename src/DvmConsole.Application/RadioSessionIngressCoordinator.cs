// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Diagnostics;

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
            if (session is IRadioSubscriberAcknowledgementSource acknowledgements)
                acknowledgements.SubscriberAcknowledged += HandleSubscriberAcknowledged;
            if (session is IRadioLogSource logs) logs.LogPublished += HandleLogPublished;
            if (session is IRadioP25KeyEndpoint keys) keys.P25KeyReceived += HandleP25KeyReceived;
            if (session is IRadioConnectionStateNotifications connection)
                connection.ConnectionStateChanged += HandleConnectionStateChanged;
        }
    }

    public event EventHandler<RadioTrafficRecord>? TrafficReceived;

    public event EventHandler<TalkgroupAuthorityRecord>? AuthorityChanged;

    public event EventHandler<RadioConnectionSnapshot>? ConnectionChanged;

    public event EventHandler<RadioP25KeyResponse>? P25KeyReceived;

    public event EventHandler<ConsoleSubscriberAcknowledgement>? SubscriberAcknowledged;

    public event EventHandler<DebugLogEntry>? LogPublished;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        foreach (IRadioSession session in sessions.Values)
        {
            session.TrafficReceived -= HandleTrafficReceived;
            session.AuthorityChanged -= HandleAuthorityChanged;
            if (session is IRadioSubscriberAcknowledgementSource acknowledgements)
                acknowledgements.SubscriberAcknowledged -= HandleSubscriberAcknowledged;
            if (session is IRadioLogSource logs) logs.LogPublished -= HandleLogPublished;
            if (session is IRadioP25KeyEndpoint keys) keys.P25KeyReceived -= HandleP25KeyReceived;
            if (session is IRadioConnectionStateNotifications connection)
                connection.ConnectionStateChanged -= HandleConnectionStateChanged;
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

    private void HandleSubscriberAcknowledged(object? sender, ConsoleSubscriberAcknowledgement response)
    {
        if (IsRegisteredSender(sender, response.System))
            SubscriberAcknowledged?.Invoke(sender, response);
    }

    private void HandleLogPublished(object? sender, DebugLogEntry entry)
    {
        if (sender is IRadioSession session && IsRegisteredSender(sender, session.SystemId))
            LogPublished?.Invoke(sender, entry);
    }

    private void HandleP25KeyReceived(object? sender, RadioP25KeyResponse response)
    {
        if (IsRegisteredSender(sender, response.SystemId))
            P25KeyReceived?.Invoke(sender, response);
    }

    private void HandleConnectionStateChanged(object? sender, EventArgs args)
    {
        if (sender is not IRadioSession session || !IsRegisteredSender(sender, session.SystemId) ||
            sender is not IRadioConnectionStateSource source)
            return;
        RadioConnectionSnapshot snapshot = source.ConnectionState;
        if (snapshot.SystemId != session.SystemId)
            return;
        ConnectionChanged?.Invoke(session, snapshot);
    }

    private bool IsRegisteredSender(object? sender, SystemId systemId)
        => Volatile.Read(ref disposed) == 0 &&
            sender is IRadioSession session &&
            sessions.TryGetValue(systemId, out IRadioSession? registered) &&
            ReferenceEquals(registered, session) &&
            session.SystemId == systemId;
}
