using DvmConsole.Audio;

namespace DvmConsole.Media;

internal interface ITransmitCall : IDisposable
{
    void Start();
    void Process(ReadOnlySpan<short> samples);
    void End();
}

internal delegate void ProcessTransmitSamples(ReadOnlySpan<short> samples);

internal sealed class DelegateTransmitCall(
    Action start,
    ProcessTransmitSamples process,
    Action end,
    Action dispose) : ITransmitCall
{
    public void Start() => start();

    public void Process(ReadOnlySpan<short> samples) => process(samples);

    public void End() => end();

    public void Dispose() => dispose();
}

internal sealed class TransmitCaptureLifecycle : IAsyncDisposable
{
    private readonly IAudioCapture capture;
    private readonly ITransmitCall call;
    private readonly string faultedMessage;
    private readonly Action<Exception> publishFault;
    private readonly object sync = new();
    private bool running;
    private bool faulted;
    private bool disposed;
    private int faultStopStarted;

    public TransmitCaptureLifecycle(
        IAudioCapture capture,
        ITransmitCall call,
        string faultedMessage,
        Action<Exception> publishFault)
    {
        this.capture = capture ?? throw new ArgumentNullException(nameof(capture));
        this.call = call ?? throw new ArgumentNullException(nameof(call));
        this.faultedMessage = faultedMessage ?? throw new ArgumentNullException(nameof(faultedMessage));
        this.publishFault = publishFault ?? throw new ArgumentNullException(nameof(publishFault));
    }

    public bool IsRunning => running;

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
        call.Start();
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
            try
            {
                call.End();
            }
            catch
            {
                // Preserve the capture-start failure for the caller.
            }
            throw;
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
        lock (sync)
        {
            wasRunning = running;
            running = false;
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

        if (sendTerminator)
        {
            try
            {
                call.End();
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
            if (!running)
                return;

            try
            {
                call.Process(args.Samples.Span);
            }
            catch (Exception exception)
            {
                faulted = true;
                publishFault(exception);
                if (Interlocked.Exchange(ref faultStopStarted, 1) == 0)
                    TaskObservation.Observe(StopAfterFaultAsync());
            }
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
