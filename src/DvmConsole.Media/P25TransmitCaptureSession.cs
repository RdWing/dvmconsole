using DvmConsole.Audio;
using DvmConsole.Vocoder;

namespace DvmConsole.Media;

/// <summary>
/// Binds one PCM capture device to one explicit P25 transmit call. Optional
/// encryption is provided by the host after resolving codeplug key material.
/// </summary>
public sealed class P25TransmitCaptureSession : ITransmitCaptureSession
{
    private readonly IAudioCapture capture;
    private readonly P25TxCallSession call;
    private readonly object sync = new();
    private bool running;
    private bool faulted;
    private bool disposed;
    private int faultStopStarted;

    public P25TransmitCaptureSession(
        IAudioCapture capture,
        IVocoderSession vocoder,
        uint sourceId,
        uint destinationId,
        uint streamId,
        Action<ReadOnlyMemory<byte>, ushort, uint> send,
        P25TxEncryptionOptions? encryption = null)
    {
        this.capture = capture ?? throw new ArgumentNullException(nameof(capture));
        call = new P25TxCallSession(
            sourceId,
            destinationId,
            streamId,
            vocoder ?? throw new ArgumentNullException(nameof(vocoder)),
            send ?? throw new ArgumentNullException(nameof(send)),
            encryption);
    }

    public event EventHandler<Exception>? Faulted;
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
                throw new InvalidOperationException("The P25 capture session has faulted.");
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
    {
        return StopCoreAsync(sendTerminator: !faulted, cancellationToken);
    }

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
                Faulted?.Invoke(this, exception);
                if (Interlocked.Exchange(ref faultStopStarted, 1) == 0)
                    _ = StopAfterFaultAsync();
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
            Faulted?.Invoke(this, exception);
        }
    }
}
