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
    private readonly object sync = new();
    private bool running;
    private bool activated;
    private bool faulted;
    private bool disposed;
    private int faultStopStarted;

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

    public bool IsRunning => running;
    public bool IsActivated => activated;

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await Task.Yield();
        lock (sync)
        {
            if (running)
                return;
            if (faulted)
                throw new InvalidOperationException(faultedMessage);
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
            running = true;

        capture.SamplesAvailable += HandleSamplesAvailable;
        try
        {
            await capture.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            capture.SamplesAvailable -= HandleSamplesAvailable;
            lock (sync)
                running = false;
            throw;
        }
    }

    public void Activate()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (sync)
        {
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

    public Task StopAsync(CancellationToken cancellationToken = default)
        => StopCoreAsync(sendTerminator: !faulted, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;

        Exception? failure = null;
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

            call.Dispose();
            disposed = true;
        }

        if (failure is not null)
            throw failure;
    }

    private async Task StopCoreAsync(bool sendTerminator, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        bool wasRunning;
        bool wasActivated;
        lock (sync)
        {
            wasRunning = running;
            wasActivated = activated;
            running = false;
            activated = false;
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
            await StopCoreAsync(sendTerminator: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            publishFault(exception);
        }
    }
}
