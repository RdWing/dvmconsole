// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;

namespace DvmConsole.Application;

public sealed record ConsoleRadioSessionBinding(RadioSystemDescriptor System, IRadioSessionFactory Factory);

/// <summary>Owns the radio endpoints of one validated console session.</summary>
public sealed class ConsoleRadioSessions : IAsyncDisposable
{
    private readonly ConsoleSessionServices services;

    private ConsoleRadioSessions(ConsoleSessionServices services, ImmutableDictionary<SystemId, IRadioSession> sessions)
    {
        this.services = services;
        Sessions = sessions;
    }

    public ImmutableDictionary<SystemId, IRadioSession> Sessions { get; }

    public static ValueTask<ConsoleRadioSessions> CreateAsync(ConsoleSessionState state,
        ConsoleRadioSessionPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return CreateAsync(state, plan.Systems.Select(system => new ConsoleRadioSessionBinding(system, plan)),
            cancellationToken);
    }

    public static ValueTask<ConsoleRadioSessions> CreateAsync(
        ConsoleSessionState state,
        IEnumerable<ConsoleRadioSessionBinding> bindings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(bindings);
        cancellationToken.ThrowIfCancellationRequested();
        // Materialize and validate every binding before a factory can open an endpoint.
        var prepared = bindings.Select(binding =>
        {
            ArgumentNullException.ThrowIfNull(binding);
            ArgumentNullException.ThrowIfNull(binding.System);
            ArgumentNullException.ThrowIfNull(binding.Factory);
            return binding with
            {
                System = binding.System with
                {
                    ConnectionParameters = binding.System.ConnectionParameters.ToImmutableDictionary()
                }
            };
        }).ToArray();
        var expected = state.Topology.Systems.Select(system => system.Id).ToHashSet();
        if (prepared.Length != expected.Count ||
            prepared.Select(binding => binding.System.Id).Distinct().Count() != expected.Count ||
            prepared.Any(binding => !expected.Contains(binding.System.Id)))
            throw new ArgumentException("Radio bindings must match every validated system exactly once.", nameof(bindings));

        var services = new ConsoleSessionServices();
        return ConsoleSessionConstruction.CreateAsync(services, async token =>
        {
            var sessions = ImmutableDictionary.CreateBuilder<SystemId, IRadioSession>();
            foreach (ConsoleRadioSessionBinding binding in prepared)
            {
                token.ThrowIfCancellationRequested();
                IRadioSession session = await binding.Factory.CreateAsync(binding.System, token).ConfigureAwait(false);
                ArgumentNullException.ThrowIfNull(session);
                // Take ownership before inspecting a late or mismatched result.
                services.Connection.OwnAsync("radio-session", session);
                token.ThrowIfCancellationRequested();
                if (session.SystemId != binding.System.Id)
                    throw new InvalidOperationException("A radio factory returned a session for a different system.");
                sessions.Add(binding.System.Id, session);
            }
            return new ConsoleRadioSessions(services, sessions.ToImmutable());
        }, cancellationToken);
    }

    public ValueTask DisposeAsync() => services.DisposeAsync();
}
