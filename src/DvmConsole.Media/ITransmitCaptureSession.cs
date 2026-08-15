namespace DvmConsole.Media;

/// <summary>
/// Common host boundary for protocol-specific capture sessions. The session
/// owns capture/call ordering; the desktop layer owns channel policy.
/// </summary>
public interface ITransmitCaptureSession : IAsyncDisposable
{
    event EventHandler<Exception>? Faulted;
    bool IsRunning { get; }
    ValueTask StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
