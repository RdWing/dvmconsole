// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>Applies confirmed transmit lifecycle edges to shared controls, TAR and history.</summary>
internal sealed class ConsoleTransmitState(
    ConsoleChannelMediaDirectory channels, ConsoleCallHistory history, ITransmitRecordingSink? recordings)
{
    // The caller owns command serialization and any host ingress lock. Observers
    // run after each state edge, preserving the host's publication order.
    public void Starting(IReadOnlyList<ChannelId> channels, Action<ChannelId>? changed = null)
    {
        foreach (var channel in channels)
        {
            Starting(channel);
            changed?.Invoke(channel);
        }
    }

    public void Stopping(IReadOnlyList<TransmitStream> streams, Action<ChannelId>? changed = null)
    {
        foreach (var stream in streams)
        {
            Stopping(stream.ChannelId);
            changed?.Invoke(stream.ChannelId);
        }
    }

    public void StartFailed(IReadOnlyList<ChannelId> channels, Action<ChannelId>? changed = null)
    {
        foreach (var channel in channels)
        {
            StopChannel(channel, confirmed: true);
            changed?.Invoke(channel);
        }
    }

    public void Started(IReadOnlyList<TransmitTarget> targets, IReadOnlyList<ChannelId> active,
        Func<ChannelId, uint> streamId, Func<DateTimeOffset> now,
        Action<TransmitTarget, uint, ConsoleCallHistoryRecord>? started = null)
    {
        foreach (var channel in active)
        {
            var target = targets.First(candidate => candidate.Channel.Id == channel);
            uint stream = streamId(channel);
            var record = Started(target, stream, now());
            started?.Invoke(target, stream, record);
        }
    }

    /// <summary>Finalizes only confirmed streams; unresolved TX and TAR remain available for retry.</summary>
    public int Stopped(IReadOnlyList<ChannelId> channels, IReadOnlyList<TransmitStream> streams,
        IReadOnlySet<ChannelId> unresolved, Func<DateTimeOffset> now,
        Action<ChannelId, bool>? changed = null, Action<TransmitStream, CallId?>? completed = null)
    {
        foreach (var channel in channels)
        {
            bool confirmed = !unresolved.Contains(channel);
            StopChannel(channel, confirmed);
            changed?.Invoke(channel, confirmed);
        }
        int confirmedCount = 0;
        foreach (var stream in streams)
        {
            if (unresolved.Contains(stream.ChannelId)) continue;
            var call = CompleteCall(stream, now());
            confirmedCount++;
            completed?.Invoke(stream, call);
        }
        return confirmedCount;
    }

    public void Starting(ChannelId channel)
        => channels.State(channel).Operator.SetTransmitTransition(starting: true, stopping: false);

    public void Stopping(ChannelId channel)
        => channels.State(channel).Operator.SetTransmitTransition(starting: false, stopping: true);

    public ConsoleCallHistoryRecord Started(TransmitTarget target, uint streamId, DateTimeOffset now)
    {
        channels.State(target.Channel.Id).SetTransmitEnabled(true, streamId);
        return history.BeginTransmit(now, target, streamId);
    }

    public void StopChannel(ChannelId channel, bool confirmed)
    {
        var state = channels.State(channel);
        if (!confirmed)
        {
            state.Operator.SetTransmitTransition(starting: false, stopping: false);
            return;
        }
        state.SetTransmitEnabled(false);
        recordings?.StopTransmit(channels.DescribeRecording(channel));
    }

    public CallId? CompleteCall(TransmitStream stream, DateTimeOffset now)
    {
        var definition = channels.State(stream.ChannelId).Runtime.Definition;
        CallId? id = history.FindActiveTransmit(definition.SystemName,
            ChannelProtocolMediaMapper.ToTrafficProtocol(definition.Protocol), stream.StreamId,
            definition.Name, definition.DestinationId);
        if (id is { } call) history.Complete(call, now);
        return id;
    }

    public void ObserveSamples(ChannelId channel, uint streamId, uint sourceId, ReadOnlySpan<short> samples)
    {
        if (channels.State(channel).Operator.Snapshot.RecordingEnabled)
            recordings?.WriteTransmitSamples(channels.DescribeRecording(channel), streamId, sourceId, samples);
    }
}
