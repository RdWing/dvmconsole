using DvmConsole.Audio;
using System.Buffers;
using System.Diagnostics;

namespace DvmConsole.Media;

internal readonly record struct MixerPresentationNotification(
    MixerLaneBuffer? Channel,
    Action<ReadOnlyMemory<short>, TimeSpan>? Observer,
    ReadOnlyMemory<short> Samples);

internal delegate bool TryTakeMixedFrame(
    out ReadOnlyMemory<short> frame,
    out MixerPresentationNotification[] notifications,
    out int notificationCount);

internal sealed class AudioOutputPump : IDisposable
{
    private readonly IAudioPlayback output;
    private readonly TimeSpan interval;
    private readonly Func<bool> requiresTimedPolling;
    private readonly Func<int> getFramesNeeded;
    private readonly Func<TimeSpan> getPresentationDelay;
    private readonly Func<bool> shouldCoalesceFirstFrame;
    private readonly TryTakeMixedFrame tryTakeFrame;
    private readonly Action markOutputPrimed;
    private readonly Action<TimeSpan> observeLateness;
    private readonly Action<Exception> reportFailure;
    private readonly AudioOutputPumpDiagnosticsTracker diagnostics = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly SemaphoreSlim dataAvailable = new(0, 1);
    private readonly TaskCompletionSource completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;
    private int wakePending;
    private long pendingSignalTimestamp;
    private bool disposed;

    public AudioOutputPump(
        IAudioPlayback output,
        TimeSpan interval,
        Func<bool> requiresTimedPolling,
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
        this.requiresTimedPolling = requiresTimedPolling ??
            throw new ArgumentNullException(nameof(requiresTimedPolling));
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

    public AudioOutputPumpDiagnostics GetDiagnostics() => diagnostics.Snapshot();

    public void Signal()
    {
        diagnostics.RecordSignalRequest();
        if (Interlocked.CompareExchange(ref wakePending, 1, comparand: 0) != 0)
        {
            diagnostics.RecordCoalescedSignalRequest();
            return;
        }

        Interlocked.Exchange(ref pendingSignalTimestamp, Stopwatch.GetTimestamp());
        dataAvailable.Release();
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
                bool timedPolling = requiresTimedPolling();
                long waitStarted = Stopwatch.GetTimestamp();
                bool signaled;
                if (timedPolling)
                {
                    signaled = dataAvailable.Wait(interval, cancellationToken);
                }
                else
                {
                    diagnostics.RecordIdleWait();
                    dataAvailable.Wait(cancellationToken);
                    signaled = true;
                }
                if (signaled)
                    Interlocked.Exchange(ref wakePending, 0);
                diagnostics.RecordWakeup(signaled);
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
                int framesWritten = 0;
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
                        output.WriteAsync(frame, cancellationToken).AsTask().GetAwaiter().GetResult();
                        framesWritten++;
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
                diagnostics.RecordFramesWritten(framesWritten);
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
                notification.Channel?.FrameHandedOff?.Invoke(
                    notification.Samples.Length,
                    presentationDelay);
                notification.Observer?.Invoke(notification.Samples, presentationDelay);
            }
            catch
            {
                // Presentation observers are diagnostic/UI consumers and must
                // never stop the real-time mixer thread.
            }
        }
    }
}
