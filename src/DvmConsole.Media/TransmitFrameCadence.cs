// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Media;

/// <summary>
/// Keeps one outbound media stream on an absolute 20 ms frame-start cadence.
/// A late timer wake is recovered against the next scheduled deadline instead
/// of permanently slowing the stream. Falling a complete frame behind starts a
/// new cadence so recovery can never emit an immediate catch-up burst.
/// </summary>
/// <remarks>
/// Each instance owns the timing state for one transmit stream. Call
/// <see cref="WaitForNextFrameAsync"/> serially; concurrent callers are not
/// supported.
/// </remarks>
public sealed class TransmitFrameCadence
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(20);

    private readonly TimeProvider timeProvider;
    private readonly Func<TimeSpan, CancellationToken, ValueTask> delay;
    private readonly long frameIntervalTimestampUnits;
    private readonly bool delayFirstFrame;
    private long nextFrameStartTimestamp;
    private bool frameStarted;

    public TransmitFrameCadence(TimeProvider? timeProvider = null)
        : this(FrameInterval, timeProvider, delay: null, delayFirstFrame: false)
    {
    }

    public TransmitFrameCadence(TimeSpan frameInterval, TimeProvider? timeProvider = null)
        : this(frameInterval, timeProvider, delay: null, delayFirstFrame: false)
    {
    }

    internal TransmitFrameCadence(
        TimeSpan frameInterval,
        TimeProvider? timeProvider,
        Func<TimeSpan, CancellationToken, ValueTask> delay)
        : this(frameInterval, timeProvider, delay, delayFirstFrame: false)
    {
    }

    /// <summary>
    /// Creates a cadence that waits one frame interval before releasing its
    /// first frame.
    /// </summary>
    public static TransmitFrameCadence StartAfterFrameInterval(
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, ValueTask>? delay = null)
        => new(FrameInterval, timeProvider, delay, delayFirstFrame: true);

    /// <summary>
    /// Creates a cadence for an arbitrary media interval and waits for that
    /// interval before releasing its first frame.
    /// </summary>
    public static TransmitFrameCadence StartAfterInterval(
        TimeSpan interval,
        TimeProvider? timeProvider = null)
        => new(interval, timeProvider, delay: null, delayFirstFrame: true);

    internal TransmitFrameCadence(
        TimeProvider? timeProvider,
        Func<TimeSpan, CancellationToken, ValueTask>? delay,
        bool delayFirstFrame = false)
        : this(FrameInterval, timeProvider, delay, delayFirstFrame)
    {
    }

    private TransmitFrameCadence(
        TimeSpan frameInterval,
        TimeProvider? timeProvider,
        Func<TimeSpan, CancellationToken, ValueTask>? delay,
        bool delayFirstFrame)
    {
        if (frameInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(frameInterval));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.delay = delay ?? DelayAsync;
        this.delayFirstFrame = delayFirstFrame;
        frameIntervalTimestampUnits = Math.Max(
            1,
            checked((long)Math.Round(
                frameInterval.TotalSeconds * this.timeProvider.TimestampFrequency)));
    }

    public async ValueTask WaitForNextFrameAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!frameStarted)
        {
            long now = timeProvider.GetTimestamp();
            nextFrameStartTimestamp = AddFrameInterval(now);
            frameStarted = true;
            if (!delayFirstFrame)
                return;
        }

        long scheduledStart = nextFrameStartTimestamp;
        await DelayUntilAsync(scheduledStart, cancellationToken).ConfigureAwait(false);

        long actualStart = timeProvider.GetTimestamp();
        long followingScheduledStart = AddFrameInterval(scheduledStart);
        nextFrameStartTimestamp = IsAtOrAfter(actualStart, followingScheduledStart)
            ? AddFrameInterval(actualStart)
            : followingScheduledStart;
    }

    private async ValueTask DelayUntilAsync(
        long scheduledStart,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            long now = timeProvider.GetTimestamp();
            TimeSpan remaining = timeProvider.GetElapsedTime(now, scheduledStart);
            if (remaining <= TimeSpan.Zero)
                return;
            await delay(remaining, cancellationToken).ConfigureAwait(false);
        }
    }

    private long AddFrameInterval(long timestamp)
        => unchecked(timestamp + frameIntervalTimestampUnits);

    private bool IsAtOrAfter(long timestamp, long target)
        => timeProvider.GetElapsedTime(target, timestamp) >= TimeSpan.Zero;

    private async ValueTask DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
        => await Task.Delay(duration, timeProvider, cancellationToken).ConfigureAwait(false);
}
