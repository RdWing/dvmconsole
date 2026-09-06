// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;

namespace DvmConsole.Media;

internal interface ITransmitCall : IDisposable
{
    void Start();
    void Process(ReadOnlySpan<short> samples);
    ValueTask EndAsync(CancellationToken cancellationToken);
}

internal delegate void ProcessTransmitSamples(ReadOnlySpan<short> samples);
internal delegate ValueTask EndTransmitCall(CancellationToken cancellationToken);

internal sealed class DelegateTransmitCall(
    Action start,
    ProcessTransmitSamples process,
    EndTransmitCall end,
    Action dispose) : ITransmitCall
{
    public DelegateTransmitCall(
        Action start,
        ProcessTransmitSamples process,
        Action end,
        Action dispose)
        : this(start, process, cancellationToken => EndSynchronously(end, cancellationToken), dispose)
    {
    }

    public void Start() => start();

    public void Process(ReadOnlySpan<short> samples) => process(samples);

    public ValueTask EndAsync(CancellationToken cancellationToken) => end(cancellationToken);

    public void Dispose() => dispose();

    private static ValueTask EndSynchronously(Action end, CancellationToken cancellationToken)
    {
        end();
        return ValueTask.CompletedTask;
    }
}

internal sealed class TransmitCaptureLifecycle : IAsyncDisposable
{
    private readonly IAudioCapture capture;
    private readonly IBorrowedAudioCapture? borrowedCapture;
    private readonly ITransmitCall call;
    private readonly string faultedMessage;
    private readonly Action<Exception> publishFault;
    private readonly TransmitFramePacer framePacer;
    private readonly bool drainAcceptedFramesWithoutDelayOnStop;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly object sync = new();
    private bool running;
    private bool captureStarted;
    private bool captureStopConfirmed;
    private bool activated;
    private bool faulted;
    private bool stopped;
    private bool framePacerStopStarted;
    private bool framePacerSettled;
    private bool disposeRequested;
    private bool disposed;
    private int faultStopStarted;
    private Task? disposeTask;

    public TransmitCaptureLifecycle(
        IAudioCapture capture,
        ITransmitCall call,
        string faultedMessage,
        Action<Exception> publishFault,
        Func<CancellationToken, ValueTask>? waitForNextFrame = null,
        bool drainAcceptedFramesWithoutDelayOnStop = false)
    {
        this.capture = capture ?? throw new ArgumentNullException(nameof(capture));
        borrowedCapture = capture as IBorrowedAudioCapture;
        this.call = call ?? throw new ArgumentNullException(nameof(call));
        this.faultedMessage = faultedMessage ?? throw new ArgumentNullException(nameof(faultedMessage));
        this.publishFault = publishFault ?? throw new ArgumentNullException(nameof(publishFault));
        this.drainAcceptedFramesWithoutDelayOnStop = drainAcceptedFramesWithoutDelayOnStop;
        framePacer = new TransmitFramePacer(call.Process, HandleFramePacerFault, waitForNextFrame);
    }

    public bool IsRunning
    {
        get
        {
            lock (sync)
                return running;
        }
    }

    public bool IsActivated
    {
        get
        {
            lock (sync)
                return activated;
        }
    }

    public TransmitQueueHealth QueueHealth => framePacer.CaptureHealth();

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            lock (sync)
            {
                if (running)
                    return;
                if (stopped)
                    throw new InvalidOperationException("A stopped transmit capture path cannot be restarted.");
                if (faulted)
                    throw new InvalidOperationException(faultedMessage);
            }

            SubscribeToCapture();
            try
            {
                await capture.StartAsync(cancellationToken).ConfigureAwait(false);
                lock (sync)
                {
                    captureStarted = true;
                    captureStopConfirmed = false;
                    running = true;
                }
            }
            catch
            {
                UnsubscribeFromCapture();
                throw;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public void Activate()
    {
        lock (sync)
        {
            if (disposed || disposeRequested)
                throw new ObjectDisposedException(nameof(TransmitCaptureLifecycle));
            if (!running)
                throw new InvalidOperationException("The transmit capture path must be running before activation.");
            if (faulted)
                throw new InvalidOperationException(faultedMessage);
            if (activated)
                return;

            call.Start();
            activated = true;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            await StopCoreAsync(sendTerminator: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            disposeRequested = true;
            return new ValueTask(disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        Exception? failure = null;
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync(sendTerminator: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            UnsubscribeFromCapture();
            framePacer.Complete();
            await framePacer.Completion.ConfigureAwait(false);
            try
            {
                await capture.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }

            try
            {
                call.Dispose();
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
            finally
            {
                lock (sync)
                {
                    running = false;
                    activated = false;
                    stopped = true;
                    disposed = true;
                }
                lifecycleGate.Release();
            }
        }

        if (failure is not null)
            throw failure;
    }

    private async Task StopCoreAsync(bool sendTerminator, CancellationToken cancellationToken)
    {
        bool stopCapture;
        bool stopFramePacer;
        bool endCall;
        lock (sync)
        {
            if (stopped)
                return;
            running = false;
            stopCapture = captureStarted && !captureStopConfirmed;
            stopFramePacer = !framePacerStopStarted;
            framePacerStopStarted = true;
            endCall = sendTerminator && activated;
        }

        UnsubscribeFromCapture();
        var failures = new List<Exception>();
        if (stopCapture)
        {
            try
            {
                await capture.StopAsync(cancellationToken).ConfigureAwait(false);
                lock (sync)
                    captureStopConfirmed = true;
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
        else if (!captureStarted)
        {
            lock (sync)
                captureStopConfirmed = true;
        }

        if (stopFramePacer)
            framePacer.Complete(drainAcceptedFramesWithoutDelayOnStop);
        if (!framePacerSettled)
        {
            try
            {
                await framePacer.Completion.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // The media worker is settled even when it faulted. Report the
                // media failure, but do not confuse it with an unconfirmed
                // terminator or capture stop.
                try
                {
                    publishFault(exception);
                }
                catch
                {
                    // Observers cannot interrupt the stop transition.
                }
            }
            finally
            {
                lock (sync)
                    framePacerSettled = true;
            }
        }

        if (endCall)
        {
            try
            {
                await call.EndAsync(cancellationToken).ConfigureAwait(false);
                lock (sync)
                    activated = false;
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        lock (sync)
            stopped = captureStopConfirmed && framePacerSettled && !activated;

        if (failures.Count == 1)
            throw failures[0];
        if (failures.Count > 1)
            throw new AggregateException("Transmit capture shutdown failed.", failures);
    }

    private void HandleSamplesAvailable(object? sender, PcmSamplesEventArgs args)
        => HandleBorrowedSamplesAvailable(args.Samples.Span);

    private void HandleBorrowedSamplesAvailable(ReadOnlySpan<short> samples)
    {
        lock (sync)
        {
            if (!running || !activated)
                return;

            if (!framePacer.Enqueue(samples))
                return;
        }
    }

    private void SubscribeToCapture()
    {
        if (borrowedCapture is not null)
            borrowedCapture.BorrowedSamplesAvailable += HandleBorrowedSamplesAvailable;
        else
            capture.SamplesAvailable += HandleSamplesAvailable;
    }

    private void UnsubscribeFromCapture()
    {
        if (borrowedCapture is not null)
            borrowedCapture.BorrowedSamplesAvailable -= HandleBorrowedSamplesAvailable;
        else
            capture.SamplesAvailable -= HandleSamplesAvailable;
    }

    private void HandleFramePacerFault(Exception exception)
    {
        lock (sync)
            faulted = true;
        try
        {
            publishFault(exception);
        }
        finally
        {
            if (Interlocked.Exchange(ref faultStopStarted, 1) == 0)
                TaskObservation.Observe(StopAfterFaultAsync());
        }
    }

    private async Task StopAfterFaultAsync()
    {
        try
        {
            await lifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!disposed)
                    await StopCoreAsync(sendTerminator: false, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                lifecycleGate.Release();
            }
        }
        catch (Exception exception)
        {
            publishFault(exception);
        }
    }

    private void ThrowIfUnavailable()
    {
        lock (sync)
        {
            if (disposed || disposeRequested)
                throw new ObjectDisposedException(nameof(TransmitCaptureLifecycle));
        }
    }
}
