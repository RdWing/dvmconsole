using DvmConsole.Ptt;

namespace DvmConsole.Desktop;

// Temporary desktop compatibility adapter for tests and embedding callers
// that still supply the pre-boundary serial-source delegate.
internal sealed class DelegateSerialPttInputSourceFactory(Func<IPttSource> create)
    : IPttInputSourceFactory
{
    private readonly Func<IPttSource> create = create ?? throw new ArgumentNullException(nameof(create));

    public PttInputDescriptor Descriptor { get; } = new(
        "desktop-serial-compatibility",
        "Serial hardware PTT",
        IsHardware: true,
        HasSettings: true);

    public ValueTask<IPttInputSource> CreateAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IPttInputSource>(create());
    }
}
