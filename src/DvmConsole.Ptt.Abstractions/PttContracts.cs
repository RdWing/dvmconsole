namespace DvmConsole.Ptt;

public sealed record PttInputDescriptor(
    string Id,
    string DisplayName,
    bool IsHardware,
    bool HasSettings = false);

public interface IPttInputSource : IAsyncDisposable
{
    event EventHandler<bool>? StateChanged;
    bool IsPressed { get; }
    ValueTask StartAsync(CancellationToken cancellationToken = default);
    ValueTask StopAsync(CancellationToken cancellationToken = default);
}

// Compatibility name retained while existing desktop coordinators migrate to
// the host-neutral input contract.
public interface IPttSource : IPttInputSource
{
}

public interface IPttInputSourceFactory
{
    PttInputDescriptor Descriptor { get; }
    ValueTask<IPttInputSource> CreateAsync(CancellationToken cancellationToken = default);
}
