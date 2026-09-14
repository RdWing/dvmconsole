// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;

namespace DvmConsole.Application;

internal sealed class ReceivePipelineTimingReporter
{
    private static readonly TimeSpan WarningThreshold = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan InterArrivalWarningThreshold = TimeSpan.FromMilliseconds(400);
    private readonly object sync = new();
    private readonly TimeSpan minimumInterval;
    private readonly Dictionary<ChannelId, DateTimeOffset> lastPublishedAt = [];

    public ReceivePipelineTimingReporter(TimeSpan minimumInterval)
    {
        if (minimumInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumInterval));
        this.minimumInterval = minimumInterval;
    }

    public bool ShouldPublish(
        ChannelId channel,
        ReceiveWorkItemTiming latest,
        DateTimeOffset now)
    {
        TimeSpan unexpectedQueueDelay = latest.HasQueueDelayBreakdown
            ? latest.WorkerBacklogDuration
            : RemoveJitterTargetDelay(
                latest.QueueDelay,
                latest.JitterBufferTargetDelay);
        TimeSpan unexpectedEndToEndDelay = RemoveIntentionalJitterDelay(
            latest.EndToEndDelay,
            latest);
        if (latest.TransportInterArrivalDelay < InterArrivalWarningThreshold &&
            latest.TransportToApplicationBoundaryDelay < WarningThreshold &&
            latest.InterArrivalDelay < InterArrivalWarningThreshold &&
            unexpectedEndToEndDelay < WarningThreshold &&
            unexpectedQueueDelay < WarningThreshold &&
            latest.ProcessingDuration < WarningThreshold)
        {
            return false;
        }

        lock (sync)
        {
            if (lastPublishedAt.TryGetValue(channel, out DateTimeOffset last) &&
                now - last < minimumInterval)
            {
                return false;
            }

            lastPublishedAt[channel] = now;
            return true;
        }
    }

    private static TimeSpan RemoveJitterTargetDelay(
        TimeSpan observed,
        TimeSpan jitterTargetDelay)
        => observed > jitterTargetDelay
            ? observed - jitterTargetDelay
            : TimeSpan.Zero;

    private static TimeSpan RemoveIntentionalJitterDelay(
        TimeSpan observed,
        ReceiveWorkItemTiming timing)
        => RemoveJitterTargetDelay(
            observed,
            timing.HasQueueDelayBreakdown
                ? timing.JitterBufferHoldDuration + timing.OrderedDrainHoldDuration
                : timing.JitterBufferTargetDelay);

    public void Reset(ChannelId channel)
    {
        lock (sync)
            lastPublishedAt.Remove(channel);
    }
}
