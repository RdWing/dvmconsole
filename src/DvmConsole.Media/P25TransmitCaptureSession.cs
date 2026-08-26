using DvmConsole.Audio;
using DvmConsole.Vocoder;

namespace DvmConsole.Media;

// Binds one PCM capture device to one explicit P25 transmit call. Optional
// encryption is provided by the host after resolving codeplug key material.
public sealed class P25TransmitCaptureSession : ITransmitCaptureSession
{
    private readonly TransmitCaptureLifecycle lifecycle;

    public P25TransmitCaptureSession(
        IAudioCapture capture,
        IVocoderSession vocoder,
        uint sourceId,
        uint destinationId,
        uint streamId,
        Action<ReadOnlyMemory<byte>, ushort, uint> send,
        P25TxEncryptionOptions? encryption = null)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var call = new P25TxCallSession(
            sourceId,
            destinationId,
            streamId,
            vocoder ?? throw new ArgumentNullException(nameof(vocoder)),
            send ?? throw new ArgumentNullException(nameof(send)),
            encryption);
        lifecycle = new TransmitCaptureLifecycle(
            capture,
            new DelegateTransmitCall(
                call.Start,
                samples => call.Process(samples),
                _ => call.EndAsync(CancellationToken.None),
                call.Dispose),
            "The P25 capture session has faulted.",
            exception => Faulted?.Invoke(this, exception));
    }

    public event EventHandler<Exception>? Faulted;

    public bool IsRunning => lifecycle.IsRunning;
    public bool IsActivated => lifecycle.IsActivated;

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
        => lifecycle.StartAsync(cancellationToken);

    public void Activate() => lifecycle.Activate();

    public Task StopAsync(CancellationToken cancellationToken = default)
        => lifecycle.StopAsync(cancellationToken);

    public ValueTask DisposeAsync() => lifecycle.DisposeAsync();
}
