// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;

namespace DvmConsole.Application;

/// <summary>Composes receive capture and finalized History attachment over a host-owned store.</summary>
internal sealed class ConsoleRecordingRuntime : IDisposable, IReceiveRecordingTrafficPort
{
    private RecordingHistoryBinding? historyBinding;
    private bool initialized;
    private object observationSync = new();
    private Func<bool> isStopping = static () => false;
    public ReceiveRecordingTargetIndex Targets { get; }
    public ReceiveRecordingCoordinator? Receive { get; private set; }

    public ConsoleRecordingRuntime(IEnumerable<ConsoleChannelState> channels)
        => Targets = new ReceiveRecordingTargetIndex(channels);

    public void Initialize(IReadOnlyDictionary<ChannelId, ConsoleChannelState> channels,
        ReceiveCallEpisodeTracker episodes, ConsoleCallHistory history,
        IReceiveRecordingSink? capture, Func<ChannelId, ChannelRecordingDescriptor> describe,
        Action? historyChanged = null, object? observationSync = null, Func<bool>? isStopping = null)
    {
        if (initialized) throw new InvalidOperationException("Recording runtime is already initialized.");
        initialized = true;
        this.observationSync = observationSync ?? this.observationSync;
        this.isStopping = isStopping ?? this.isStopping;
        if (capture is null) return;
        Receive = new ReceiveRecordingCoordinator(channels, Targets, episodes, capture, describe);
        if (capture is IRecordingCallAttachmentSource attachments)
            historyBinding = new RecordingHistoryBinding(attachments, history, historyChanged);
    }

    public void ObserveDecoded(ChannelId channel, uint stream, uint source, ReadOnlyMemory<short> samples)
    {
        lock (observationSync)
            if (!isStopping()) Receive?.ObserveDecoded(channel, stream, source, samples);
    }

    public void ObserveRecordingTraffic(ChannelId channel, IRadioMediaFrame traffic)
    {
        lock (observationSync)
            if (!isStopping()) Receive?.ObserveTraffic(channel, traffic);
    }

    // The store has separate ordered ownership; retiring this graph only detaches its observer.
    public void Dispose() => historyBinding?.Dispose();
}
