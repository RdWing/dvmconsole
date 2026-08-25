using DvmConsole.Audio;
using DvmConsole.Vocoder;

namespace DvmConsole.Media;

// Binds one PCM capture device to one explicit DMR transmit call. The host
// owns PTT policy and transport; this class only controls capture/call order
// and converts callback samples into DMR voice packets.
public sealed class DmrTransmitCaptureSession : ITransmitCaptureSession
{
    private readonly TransmitCaptureLifecycle lifecycle;

    public DmrTransmitCaptureSession(
        IAudioCapture capture,
        IVocoderSession vocoder,
        uint sourceId,
        uint destinationId,
        byte slot,
        uint streamId,
        Action<ReadOnlyMemory<byte>, ushort, uint> send,
        DmrPrivacyOptions? privacy = null)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var call = new DmrTxCallSession(
            sourceId,
            destinationId,
            slot,
            streamId,
            vocoder ?? throw new ArgumentNullException(nameof(vocoder)),
            send ?? throw new ArgumentNullException(nameof(send)),
            privacy: privacy);
        lifecycle = new TransmitCaptureLifecycle(
            capture,
            new DelegateTransmitCall(call.Start, samples => call.Process(samples), call.End, call.Dispose),
            "The DMR capture session has faulted.",
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
