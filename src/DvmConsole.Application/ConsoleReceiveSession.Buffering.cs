// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using DvmConsole.Core.Runtime;

namespace DvmConsole.Application;

public sealed partial class ConsoleReceiveSession : IConsoleReceiveBufferingSettings
{
    private ImmutableDictionary<SystemId, ConsoleReceiveBufferingOptions> receiveBuffering = ImmutableDictionary<SystemId, ConsoleReceiveBufferingOptions>.Empty;
    private ReceiveBufferingRuntime bufferingRuntime => operationalRuntime.Buffering;
    private IReadOnlyDictionary<ChannelId, SystemId> bufferingSystems = null!;
    public ImmutableDictionary<SystemId, ConsoleReceiveBufferingOptions> ReceiveBuffering => Volatile.Read(ref receiveBuffering);
    public bool CanSaveReceiveBuffering => dependencies.Preferences is IConsoleReceiveBufferingStore;

    private void InitializeReceiveBuffering()
    {
        bufferingSystems = state.Topology.Channels.ToDictionary(channel => channel.Id, channel => channel.SystemId);
        receiveBuffering = state.Topology.Systems.ToImmutableDictionary(system => system.Id, _ => ConsoleReceiveBufferingOptions.Default);
        ApplyBufferingSnapshot(receiveBuffering);
    }

    private async Task RestoreReceiveBufferingAsync(CancellationToken token)
    {
        if (dependencies.Preferences is not IConsoleReceiveBufferingStore store) return;
        var saved = await store.LoadReceiveBufferingAsync(state.Topology.Systems.Select(system => system.Name).ToArray(), token).ConfigureAwait(false);
        var restored = state.Topology.Systems.ToImmutableDictionary(system => system.Id,
            system => saved.GetValueOrDefault(system.Name, ConsoleReceiveBufferingOptions.Default));
        foreach (var options in restored.Values) options.Validate();
        ApplyBufferingSnapshot(restored);
    }

    public ValueTask SetReceiveBufferingAsync(SystemId system, ConsoleReceiveBufferingOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return RunCommandAsync(async token =>
        {
            var store = dependencies.Preferences as IConsoleReceiveBufferingStore
                ?? throw new NotSupportedException("Receive buffering preferences are unavailable.");
            var connection = state.Topology.Systems.FirstOrDefault(candidate => candidate.Id == system)
                ?? throw new ArgumentException("The FNE connection is not part of this console.", nameof(system));
            await store.SaveReceiveBufferingAsync(connection.Name, options, token).ConfigureAwait(false);
            ApplyBufferingSnapshot(ReceiveBuffering.SetItem(system, options));
            bufferingRuntime.Reset(system.Value);
            SetStatus($"{connection.Name} receive buffering saved for new streams.");
        }, cancellationToken);
    }

    private void ApplyBufferingSnapshot(ImmutableDictionary<SystemId, ConsoleReceiveBufferingOptions> snapshot)
    {
        bufferingRuntime.Apply(snapshot.Select(pair => new KeyValuePair<string, ConsoleReceiveBufferingOptions>(pair.Key.Value, pair.Value)));
        Volatile.Write(ref receiveBuffering, snapshot);
    }

    private ReceiveJitterBufferProfile GetReceiveBufferingProfile(ChannelId channel, RadioMediaProtocol protocol)
        => bufferingRuntime.GetProfile(bufferingSystems[channel].Value, protocol);

}
