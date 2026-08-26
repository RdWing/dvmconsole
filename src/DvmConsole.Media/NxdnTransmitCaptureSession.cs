using DvmConsole.Audio;
using DvmConsole.Vocoder;

namespace DvmConsole.Media;

public sealed class NxdnTransmitCaptureSession : ITransmitCaptureSession
{
    private readonly TransmitCaptureLifecycle lifecycle;

    public NxdnTransmitCaptureSession(
        IAudioCapture capture,
        IVocoderSession vocoder,
        uint sourceId,
        uint destinationId,
        uint streamId,
        Action<ReadOnlyMemory<byte>, ushort, uint> send,
        bool group = true,
        NxdnPrivacyOptions? privacy = null)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var call = new NxdnTxCallSession(
            sourceId,
            destinationId,
            group,
            streamId,
            vocoder ?? throw new ArgumentNullException(nameof(vocoder)),
            send ?? throw new ArgumentNullException(nameof(send)),
            privacy);
        lifecycle = new TransmitCaptureLifecycle(
            capture,
            new DelegateTransmitCall(
                call.Start,
                samples => call.Process(samples),
                _ => call.EndAsync(CancellationToken.None),
                call.Dispose),
            "The NXDN capture session has faulted.",
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
