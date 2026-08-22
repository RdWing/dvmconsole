using DvmConsole.Audio;
using System.Buffers;
using System.Diagnostics;

namespace DvmConsole.Media;

internal readonly record struct MixerPresentationNotification(
    Action<ReadOnlyMemory<short>, TimeSpan> Observer,
    ReadOnlyMemory<short> Samples);

internal delegate bool TryTakeMixedFrame(
    out ReadOnlyMemory<short> frame,
    out MixerPresentationNotification[] notifications,
    out int notificationCount);

internal sealed class AudioOutputPump : IDisposable
{
    private readonly IAudioPlayback output;
    private readonly TimeSpan interval;
    private readonly Func<int> getFramesNeeded;
    private readonly Func<TimeSpan> getPresentationDelay;
    private readonly Func<bool> shouldCoalesceFirstFrame;
    private readonly TryTakeMixedFrame tryTakeFrame;
    private readonly Action markOutputPrimed;
    private readonly Action<TimeSpan> observeLateness;
    private readonly Action<Exception> reportFailure;
    private readonly CancellationTokenSource cancellation = new();
    private readonly SemaphoreSlim dataAvailable = new(0, 1);
    private readonly TaskCompletionSource completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;
    private long pendingSignalTimestamp;
    private bool disposed;

    public AudioOutputPump(
        IAudioPlayback output,
        TimeSpan interval,
        Func<int> getFramesNeeded,
        Func<TimeSpan> getPresentationDelay,
        Func<bool> shouldCoalesceFirstFrame,
        TryTakeMixedFrame tryTakeFrame,
        Action markOutputPrimed,
        Action<TimeSpan> observeLateness,
        Action<Exception> reportFailure)
    {
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        this.interval = interval;
        this.getFramesNeeded = getFramesNeeded ?? throw new ArgumentNullException(nameof(getFramesNeeded));
        this.getPresentationDelay = getPresentationDelay ?? throw new ArgumentNullException(nameof(getPresentationDelay));
        this.shouldCoalesceFirstFrame = shouldCoalesceFirstFrame ?? throw new ArgumentNullException(nameof(shouldCoalesceFirstFrame));
        this.tryTakeFrame = tryTakeFrame ?? throw new ArgumentNullException(nameof(tryTakeFrame));
        this.markOutputPrimed = markOutputPrimed ?? throw new ArgumentNullException(nameof(markOutputPrimed));
        this.observeLateness = observeLateness ?? throw new ArgumentNullException(nameof(observeLateness));
        this.reportFailure = reportFailure ?? throw new ArgumentNullException(nameof(reportFailure));

        thread = new Thread(() => Run(cancellation.Token))
        {
            IsBackground = true,
            Name = "DVM Console RX mixer"
        };
        try
        {
            thread.Priority = ThreadPriority.AboveNormal;
        }
        catch (PlatformNotSupportedException)
        {
            // The dedicated thread still avoids thread-pool continuation stalls.
        }
        thread.Start();
    }

    public Task Completion => completion.Task;

    public void Signal()
    {
        Interlocked.CompareExchange(
            ref pendingSignalTimestamp,
            Stopwatch.GetTimestamp(),
            comparand: 0);
        try
        {
            dataAvailable.Release();
        }
        catch (SemaphoreFullException)
        {
            // One pending wake is sufficient; the pump drains to its target.
        }
    }

    public void Cancel() => cancellation.Cancel();

    public void Dispose()
    {
        if (disposed)
            return;

        dataAvailable.Dispose();
        cancellation.Dispose();
        disposed = true;
    }

    private void Run(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                long waitStarted = Stopwatch.GetTimestamp();
                bool signaled = dataAvailable.Wait(interval, cancellationToken);
                long now = Stopwatch.GetTimestamp();
                TimeSpan lateness = TimeSpan.Zero;
                if (signaled)
                {
                    long signalTimestamp = Interlocked.Exchange(ref pendingSignalTimestamp, 0);
                    if (signalTimestamp > 0)
                        lateness = Stopwatch.GetElapsedTime(signalTimestamp, now);
                }
                else
                {
                    TimeSpan elapsed = Stopwatch.GetElapsedTime(waitStarted, now);
                    lateness = elapsed - interval;
                }

                observeLateness(lateness);

                if (signaled && shouldCoalesceFirstFrame() &&
                    cancellationToken.WaitHandle.WaitOne(interval))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                int framesToWrite = getFramesNeeded();
                for (int index = 0; index < framesToWrite; index++)
                {
                    TimeSpan presentationDelay = getPresentationDelay();
                    if (!tryTakeFrame(
                            out ReadOnlyMemory<short> frame,
                            out MixerPresentationNotification[] notifications,
                            out int notificationCount))
                    {
                        break;
                    }

                    try
                    {
                        output.WriteAsync(frame, cancellationToken).GetAwaiter().GetResult();
                        markOutputPrimed();
                        NotifyPresentations(notifications, notificationCount, presentationDelay);
                    }
                    finally
                    {
                        if (notificationCount > 0)
                        {
                            ArrayPool<MixerPresentationNotification>.Shared.Return(
                                notifications,
                                clearArray: true);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during mixer shutdown.
        }
        catch (Exception exception)
        {
            reportFailure(exception);
        }
        finally
        {
            completion.TrySetResult();
        }
    }

    private static void NotifyPresentations(
        MixerPresentationNotification[] notifications,
        int count,
        TimeSpan presentationDelay)
    {
        for (int index = 0; index < count; index++)
        {
            MixerPresentationNotification notification = notifications[index];
            try
            {
                notification.Observer(notification.Samples, presentationDelay);
            }
            catch
            {
                // Presentation observers are diagnostic/UI consumers and must
                // never stop the real-time mixer thread.
            }
        }
    }
}
