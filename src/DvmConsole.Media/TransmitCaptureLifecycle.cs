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
    private readonly ITransmitCall call;
    private readonly string faultedMessage;
    private readonly Action<Exception> publishFault;
    private readonly TransmitFramePacer framePacer;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly object sync = new();
    private bool running;
    private bool activated;
    private bool faulted;
    private bool stopped;
    private bool disposeRequested;
    private bool disposed;
    private int faultStopStarted;
    private Task? disposeTask;

    public TransmitCaptureLifecycle(
        IAudioCapture capture,
        ITransmitCall call,
        string faultedMessage,
        Action<Exception> publishFault,
        Func<CancellationToken, ValueTask>? waitForNextFrame = null)
    {
        this.capture = capture ?? throw new ArgumentNullException(nameof(capture));
        this.call = call ?? throw new ArgumentNullException(nameof(call));
        this.faultedMessage = faultedMessage ?? throw new ArgumentNullException(nameof(faultedMessage));
        this.publishFault = publishFault ?? throw new ArgumentNullException(nameof(publishFault));
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

            capture.SamplesAvailable += HandleSamplesAvailable;
            try
            {
                await capture.StartAsync(cancellationToken).ConfigureAwait(false);
                lock (sync)
                    running = true;
            }
            catch
            {
                capture.SamplesAvailable -= HandleSamplesAvailable;
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
            await StopCoreAsync(sendTerminator: !faulted, cancellationToken).ConfigureAwait(false);
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
            await StopCoreAsync(sendTerminator: !faulted, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            capture.SamplesAvailable -= HandleSamplesAvailable;
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
                    disposed = true;
                lifecycleGate.Release();
            }
        }

        if (failure is not null)
            throw failure;
    }

    private async Task StopCoreAsync(bool sendTerminator, CancellationToken cancellationToken)
    {
        bool wasRunning;
        bool wasActivated;
        lock (sync)
        {
            wasRunning = running;
            wasActivated = activated;
            running = false;
            activated = false;
            stopped = true;
        }

        if (!wasRunning)
            return;

        capture.SamplesAvailable -= HandleSamplesAvailable;
        Exception? stopFailure = null;
        try
        {
            await capture.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            stopFailure = exception;
        }

        framePacer.Complete();
        await framePacer.Completion.ConfigureAwait(false);

        if (sendTerminator && wasActivated && framePacer.Failure is null)
        {
            try
            {
                await call.EndAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                stopFailure ??= exception;
            }
        }

        if (stopFailure is not null)
            throw stopFailure;
    }

    private void HandleSamplesAvailable(object? sender, PcmSamplesEventArgs args)
    {
        lock (sync)
        {
            if (!running || !activated)
                return;

            if (!framePacer.Enqueue(args.Samples.Span))
                return;
        }
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
