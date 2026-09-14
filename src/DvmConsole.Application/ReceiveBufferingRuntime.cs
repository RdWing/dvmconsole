// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using DvmConsole.Core.Runtime;

namespace DvmConsole.Application;

/// <summary>Owns applied buffering policy and connection-level learning for new receive streams.</summary>
internal sealed class ReceiveBufferingRuntime
{
    private readonly AdaptiveReceiveJitterBufferController adaptive;
    private ImmutableDictionary<string, ConsoleReceiveBufferingOptions> options =
        ImmutableDictionary.Create<string, ConsoleReceiveBufferingOptions>(StringComparer.OrdinalIgnoreCase);

    public ReceiveBufferingRuntime(IMonotonicTimeSource? time = null)
        => adaptive = new(time);

    public void Apply(IEnumerable<KeyValuePair<string, ConsoleReceiveBufferingOptions>> configured)
    {
        var captured = configured.ToImmutableDictionary(pair => pair.Key.Trim(), pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var option in captured.Values) option.Validate();
        Volatile.Write(ref options, captured);
    }

    public ConsoleReceiveBufferingOptions GetOptions(string system)
        => Volatile.Read(ref options).GetValueOrDefault(system.Trim(), ConsoleReceiveBufferingOptions.Default);

    public ReceiveJitterBufferProfile GetProfile(string system, RadioMediaProtocol protocol)
        => adaptive.GetProfile(system.Trim(), protocol,
            ReceiveJitterBufferConfigurationPolicy.GetConfiguration(protocol, GetOptions(system)));

    public void Observe(string system, IRadioMediaFrame frame)
        => adaptive.Observe(system.Trim(), frame,
            frame is IRadioFrameIngressTiming timing ? timing.TransportIngressTimestamp : 0,
            ReceiveJitterBufferConfigurationPolicy.GetConfiguration(frame.Protocol, GetOptions(system)));

    public void Reset(string system) => adaptive.Reset(system.Trim());
}
