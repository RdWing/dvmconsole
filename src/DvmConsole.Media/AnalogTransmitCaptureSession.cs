using DvmConsole.Audio;

namespace DvmConsole.Media;

// Binds one PCM capture device to one explicit analog transmit call. The
// host owns PTT policy and transport; this class controls capture/call order.
public sealed class AnalogTransmitCaptureSession : ITransmitCaptureSession
{
    private readonly TransmitCaptureLifecycle lifecycle;

    public AnalogTransmitCaptureSession(
        IAudioCapture capture,
        uint sourceId,
        uint destinationId,
        uint streamId,
        Action<ReadOnlyMemory<byte>, ushort, uint> send,
        bool grantDemand = false)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var call = new AnalogTxAudioSession(
            sourceId,
            destinationId,
            streamId,
            send ?? throw new ArgumentNullException(nameof(send)),
            grantDemand: grantDemand);
        lifecycle = new TransmitCaptureLifecycle(
            capture,
            new DelegateTransmitCall(
                call.Start,
                samples => call.Process(samples),
                call.End,
                call.Dispose),
            "The analog capture session has faulted.",
            exception => Faulted?.Invoke(this, exception));
    }

    public event EventHandler<Exception>? Faulted;

    public bool IsRunning => lifecycle.IsRunning;
    public bool IsActivated => lifecycle.IsActivated;
    public TransmitQueueHealth QueueHealth => lifecycle.QueueHealth;

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
        => lifecycle.StartAsync(cancellationToken);

    public void Activate() => lifecycle.Activate();

    public Task StopAsync(CancellationToken cancellationToken = default)
        => lifecycle.StopAsync(cancellationToken);

    public ValueTask DisposeAsync() => lifecycle.DisposeAsync();
}
