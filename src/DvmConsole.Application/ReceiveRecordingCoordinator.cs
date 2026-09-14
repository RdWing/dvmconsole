// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;

namespace DvmConsole.Application;

/// <summary>Durable receive capture; implementations own sample copies and finalization.</summary>
public interface IReceiveRecordingSink
{
    void WriteEpisodeSamples(ChannelRecordingDescriptor channel, uint episodeStreamId, uint physicalStreamId,
        uint sourceId, ReadOnlyMemory<short> samples, long? receiveEpisodeId = null);
    void ObserveEpisodeTraffic(ChannelRecordingDescriptor channel, uint episodeStreamId, uint physicalStreamId,
        IRadioMediaFrame traffic, long? receiveEpisodeId = null);
    void StopEpisode(ChannelRecordingDescriptor channel, long receiveEpisodeId);
    void StopChannel(ChannelRecordingDescriptor channel);
}

public readonly record struct ReceiveRecordingState(bool IsRecording, bool IsFinalizing, string? Fault);

/// <summary>Recording status independent of channel presentation and capture commands.</summary>
public interface IReceiveRecordingStateSource
{
    ReceiveRecordingState CaptureState(ChannelId channel);
    IReadOnlyDictionary<ChannelId, ReceiveRecordingState> CaptureStates(IReadOnlyList<ChannelId> channels)
        => channels.ToDictionary(id => id, CaptureState);
}

/// <summary>One session's capture queue over a host-owned recording store.</summary>
public interface IReceiveRecordingSession : IReceiveRecordingSink, IReceiveRecordingStateSource, IAsyncDisposable
{
    bool CanWrite { get; }
    event Action<ChannelId>? StateChanged;
    Task DrainAsync(CancellationToken cancellationToken = default);
    Task CheckpointAsync(CancellationToken cancellationToken = default) => DrainAsync(cancellationToken);
}

/// <summary>Routes decoded media and encryption observations to the armed logical call owner.</summary>
internal sealed class ReceiveRecordingCoordinator(
    IReadOnlyDictionary<ChannelId, ConsoleChannelState> channels,
    ReceiveRecordingTargetIndex targets,
    ReceiveCallEpisodeTracker episodes,
    IReceiveRecordingSink sink,
    Func<ChannelId, ChannelRecordingDescriptor> describe)
{
    public void ObserveDecoded(ChannelId channel, uint stream, uint source, ReadOnlyMemory<short> samples)
    {
        if (targets.Resolve(channel) is not { } target) return;
        ReceiveCallEpisodeSnapshot? episode = ResolveEpisode(channel, stream);
        sink.WriteEpisodeSamples(describe(target), episode?.PrimaryStreamId ?? stream, stream, source, samples, episode?.EpisodeId);
    }

    public void ObserveTraffic(ChannelId channel, IRadioMediaFrame traffic)
    {
        if (targets.Resolve(channel) is not { } target) return;
        ReceiveCallEpisodeSnapshot? episode = ResolveEpisode(channel, traffic.StreamId);
        sink.ObserveEpisodeTraffic(describe(target), episode?.PrimaryStreamId ?? traffic.StreamId,
            traffic.StreamId, traffic, episode?.EpisodeId);
    }

    private ReceiveCallEpisodeSnapshot? ResolveEpisode(ChannelId id, uint stream)
    {
        var definition = channels[id].Runtime.Definition;
        return episodes.TryGet(definition.SystemName, ChannelProtocolMediaMapper.ToTrafficProtocol(definition.Protocol),
            stream, out var episode) ? episode : null;
    }
}
