// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Threading;

namespace DvmConsole.Application;

/// <summary>Host policy and presentation around shared, synchronous radio-state decisions.</summary>
internal interface IConsoleRadioLifecyclePort
{
    bool IsStopping { get; }
    void ChannelChanged(ChannelId channel);
    void AuthorityChanged(TalkgroupAuthorityRecord record, IReadOnlyList<ChannelId> unavailable, int stoppedPatchTargets);
    void ConnectionChanged(IRadioSession radio, RadioConnectionSnapshot snapshot, ConsoleConnectionChange transition);
    void KeyStateChanged(IRadioSession radio);
    void KeyReceived(IRadioSession radio, RadioP25KeyResponse response, bool accepted, string message);
    void PublishStatus(string message);
}

/// <summary>
/// Applies radio authority, connection-scoped keys and subscriber acknowledgements.
/// Hosts retain manual-input ownership, execution policy and presentation; none of
/// these callbacks restore transmission or replay interrupted traffic.
/// </summary>
internal sealed class ConsoleRadioLifecycleController
{
    private readonly ConsoleOperationalRuntime runtime;
    private readonly ConsoleSessionState state;
    private readonly IReadOnlyDictionary<SystemId, IRadioSession> radios;
    private readonly P25KeyRetrievalCoordinator? keys;
    private readonly IReadOnlyDictionary<SystemId, P25KeyRequestPort> keyRequests;
    private readonly ConsoleSubscriberCommandDispatcher subscribers;
    private readonly IConsoleRadioLifecyclePort host;
    private readonly IReadOnlyDictionary<SystemId, IReadOnlyList<(byte AlgorithmId, ushort KeyId)>> configuredKeys;

    public ConsoleRadioLifecycleController(ConsoleOperationalRuntime runtime, ConsoleSessionState state,
        IReadOnlyDictionary<SystemId, IRadioSession> radios, P25KeyRetrievalCoordinator? keys,
        IReadOnlyDictionary<SystemId, P25KeyRequestPort> keyRequests,
        ConsoleSubscriberCommandDispatcher subscribers, IConsoleRadioLifecyclePort host)
    {
        this.runtime = runtime;
        this.state = state;
        this.radios = radios;
        this.keys = keys;
        this.keyRequests = keyRequests;
        this.subscribers = subscribers;
        this.host = host;
        configuredKeys = state.Topology.Systems.ToDictionary(system => system.Id,
            system => P25ConfiguredKeyRequests.Resolve(state.Topology.Channels
                .Where(channel => channel.SystemId == system.Id)
                .Select(channel => state.Channels[channel.Id].Runtime.Definition)));
    }

    public void ApplyAuthority(TalkgroupAuthorityRecord record)
    {
        if (host.IsStopping || !radios.ContainsKey(record.SystemId)) return;
        IReadOnlyList<ChannelId> unavailable = runtime.Authority.Apply(record, host.ChannelChanged);
        if (unavailable.Count == 0) return;
        int stoppedPatches = runtime.Patches.Forwarding.StopUnavailableTargets(unavailable);
        if (runtime.Transmit.GeneratedOperation is { } tones)
            TaskObservation.Observe(tones.CancelTargetsAsync(unavailable));
        host.AuthorityChanged(record, unavailable, stoppedPatches);
    }

    public void OnConnection(object? sender, RadioConnectionSnapshot snapshot)
    {
        if (IsCurrent(sender, snapshot.SystemId)) ApplyConnection(snapshot);
    }

    // Trusted host-generated failures may have no transport event sender.
    public void ApplyConnection(RadioConnectionSnapshot snapshot)
    {
        if (state.Terminal.IsClosed || !radios.TryGetValue(snapshot.SystemId, out var radio)) return;
        var transition = runtime.ObserveConnectionState(snapshot.SystemId, radio.Name, snapshot.State);
        bool connected = snapshot.State == RadioConnectionState.Connected && radio.IsConnected;
        UpdateKeys(radio, connected);
        if (!connected) subscribers.Interrupt(radio.SystemId);
        if (!host.IsStopping) host.ConnectionChanged(radio, snapshot, transition);
    }

    private void UpdateKeys(IRadioSession radio, bool connected)
    {
        if (keys is null || !keyRequests.TryGetValue(radio.SystemId, out var requests)) return;
        if (!connected || host.IsStopping)
        {
            keys.Cancel(radio.Name);
            state.KeyRequests[radio.SystemId].Clear();
            host.KeyStateChanged(radio);
            return;
        }
        TaskObservation.Observe(keys.Schedule(radio.Name, configuredKeys[radio.SystemId],
            () => !host.IsStopping && radio.IsConnected, requests,
            failure => { if (!host.IsStopping) host.PublishStatus($"{radio.Name}: P25 key request unavailable — {failure.Message}"); }));
    }

    public void OnKey(object? sender, RadioP25KeyResponse response)
    {
        if (IsCurrent(sender, response.SystemId)) ApplyKey(response);
    }

    public void ApplyKey(RadioP25KeyResponse response)
    {
        if (host.IsStopping || keys is null || !radios.TryGetValue(response.SystemId, out var radio)) return;
        try
        {
            if (!keys.TryApply(radio.Name, response.AlgorithmId, response.KeyId, response.KeyMaterial.Span,
                () => state.KeyRequests[radio.SystemId].ObserveResponse(response.AlgorithmId, response.KeyId))) return;
            host.KeyReceived(radio, response, true, $"{radio.Name}: P25 key 0x{response.KeyId:X4} received through FNE/KMM.");
        }
        catch (ArgumentException failure)
        {
            host.KeyReceived(radio, response, false,
                $"{radio.Name}: rejected P25 KMM key 0x{response.KeyId:X4} — {failure.Message}");
        }
    }

    public void OnSubscriberAcknowledged(object? sender, ConsoleSubscriberAcknowledgement response)
    {
        if (host.IsStopping || !IsCurrent(sender, response.System) ||
            !radios[response.System].IsConnected) return;
        if (subscribers.Acknowledge(response) is { } result) host.PublishStatus(result.StatusText);
    }

    private bool IsCurrent(object? sender, SystemId system)
        => sender is IRadioSession radio && radio.SystemId == system &&
            radios.TryGetValue(system, out var current) && ReferenceEquals(current, radio);
}
