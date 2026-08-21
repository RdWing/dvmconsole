namespace DvmConsole.Desktop;

internal sealed class ReceivePipelineTimingReporter
{
    private static readonly TimeSpan WarningThreshold = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan InterArrivalWarningThreshold = TimeSpan.FromMilliseconds(400);
    private readonly object sync = new();
    private readonly TimeSpan minimumInterval;
    private readonly Dictionary<ChannelViewModel, DateTimeOffset> lastPublishedAt = [];

    public ReceivePipelineTimingReporter(TimeSpan minimumInterval)
    {
        if (minimumInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumInterval));
        this.minimumInterval = minimumInterval;
    }

    public bool ShouldPublish(
        ChannelViewModel channel,
        ReceiveWorkItemTiming latest,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(channel);
        TimeSpan unexpectedQueueDelay = RemoveConfiguredJitterDelay(
            latest.QueueDelay,
            latest.ConfiguredJitterBufferDelay);
        TimeSpan unexpectedEndToEndDelay = RemoveConfiguredJitterDelay(
            latest.EndToEndDelay,
            latest.ConfiguredJitterBufferDelay);
        if (latest.TransportInterArrivalDelay < InterArrivalWarningThreshold &&
            latest.TransportToFneBoundaryDelay < WarningThreshold &&
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

    private static TimeSpan RemoveConfiguredJitterDelay(
        TimeSpan observed,
        TimeSpan configuredJitterDelay)
        => observed > configuredJitterDelay
            ? observed - configuredJitterDelay
            : TimeSpan.Zero;

    public void Reset(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        lock (sync)
            lastPublishedAt.Remove(channel);
    }
}
