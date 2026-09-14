// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;

namespace DvmConsole.Application;

/// <summary>Prepared radio dependencies, usable as the host's single radio factory.</summary>
public sealed class ConsoleRadioSessionPlan : IRadioSessionFactory
{
    private readonly ImmutableDictionary<SystemId, ConsoleRadioSessionBinding> bindings;

    public ConsoleRadioSessionPlan(IEnumerable<ConsoleRadioSessionBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
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
        this.bindings = prepared.ToImmutableDictionary(binding => binding.System.Id);
        Systems = prepared.Select(binding => binding.System).ToImmutableArray();
    }

    public ImmutableArray<RadioSystemDescriptor> Systems { get; }

    public ValueTask<IRadioSession> CreateAsync(RadioSystemDescriptor system,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        cancellationToken.ThrowIfCancellationRequested();
        if (!bindings.TryGetValue(system.Id, out var binding))
            throw new ArgumentException("The system is not part of the prepared radio plan.", nameof(system));
        // Use the frozen descriptor rather than caller-supplied connection overrides.
        return binding.Factory.CreateAsync(binding.System, cancellationToken);
    }
}
