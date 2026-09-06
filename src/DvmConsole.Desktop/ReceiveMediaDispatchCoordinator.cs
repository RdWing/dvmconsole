// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

internal interface IReceiveMediaPresentation
{
    void RecordDroppedFrame(ChannelId channel);
    void PublishDiagnostics(ChannelId channel, uint streamId, DateTimeOffset now);
    void MarkAudioMeter(ChannelId channel, uint streamId, bool ended);
    void MarkPlaybackActive(ChannelId channel, uint sourceId, uint streamId);
    void PublishFinalJitterSummary(ChannelId channel, uint streamId);
    void ReportCleanupFailure(ChannelId channel, uint streamId, Exception failure);
}

/// <summary>
/// Enqueues media without UI dispatch and orders physical-stream cleanup after
/// queued packets. The existing queues and session runtime retain worker and
/// decoder ownership; logical TAR completion remains episode-owned.
/// </summary>
internal sealed class ReceiveMediaDispatchCoordinator(
    IReceiveMediaWork work,
    IReceiveMediaPresentation presentation,
    TimeProvider? clock = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    public bool TryEnqueueAudio(ChannelId channel, FneTrafficFrame traffic, long ingressTimestamp)
    {
        if (work.IsDisposing)
            return false;
        bool accepted = work.EnqueueAudio(
            channel, traffic, ingressTimestamp > 0 ? ingressTimestamp : clock.GetTimestamp(), out bool droppedFrame);
        if (droppedFrame)
            presentation.RecordDroppedFrame(channel);
        if (!accepted || droppedFrame)
            presentation.PublishDiagnostics(channel, traffic.StreamId, clock.GetUtcNow());
        return accepted;
    }

    public void EnqueueAudio(ChannelId channel, FneTrafficFrame traffic, long ingressTimestamp)
    {
        if (TryEnqueueAudio(channel, traffic, ingressTimestamp))
            PresentAcceptedAudio(channel, traffic, markPlayback: true);
    }

    public void PresentAcceptedAudio(ChannelId channel, FneTrafficFrame traffic, bool markPlayback)
    {
        bool ended = ReceiveTrafficClassifier.IsTerminator(traffic);
        presentation.MarkAudioMeter(channel, traffic.StreamId, ended);
        if (!ended && markPlayback)
            presentation.MarkPlaybackActive(channel, traffic.SourceId, traffic.StreamId);
    }

    public bool EnqueuePatch(ChannelId channel, FneTrafficFrame traffic, long? ingressTimestamp = null)
        => work.EnqueuePatch(channel, traffic, ingressTimestamp);

    public void StopPatchSource(ChannelId channel, uint streamId) => work.StopPatchSource(channel, streamId);

    public async Task FinalizeStreamAsync(ChannelId channel, uint streamId, DateTimeOffset endedAt)
    {
        try
        {
            await work.RunAfterStreamAsync(channel, streamId, async cancellationToken =>
            {
                try
                {
                    await work.CompleteStreamAsync(channel, streamId, endedAt, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    presentation.PublishFinalJitterSummary(channel, streamId);
                }
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (work.IsDisposing)
        {
            // Application shutdown already owns receive-session cleanup.
        }
        catch (Exception exception)
        {
            presentation.ReportCleanupFailure(channel, streamId, exception);
        }
    }
}
