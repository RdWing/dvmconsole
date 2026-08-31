namespace DvmConsole.Ptt;

public sealed class KeyboardPttInputSourceFactory(
    KeyboardPttKey activationKey,
    bool toggleMode = false) : IPttInputSourceFactory
{
    public PttInputDescriptor Descriptor { get; } = new(
        $"desktop-keyboard-{activationKey.ToString().ToLowerInvariant()}",
        "Focused keyboard PTT",
        IsHardware: false,
        HasSettings: true);

    public ValueTask<IPttInputSource> CreateAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IPttInputSource>(new KeyboardPttSource(activationKey)
        {
            ToggleMode = toggleMode
        });
    }
}

public sealed class GlobalKeyboardPttInputSourceFactory(
    KeyboardPttKey activationKey,
    bool toggleMode = false) : IPttInputSourceFactory
{
    public PttInputDescriptor Descriptor { get; } = new(
        $"desktop-global-keyboard-{activationKey.ToString().ToLowerInvariant()}",
        "OS-global keyboard PTT",
        IsHardware: false,
        HasSettings: true);

    public ValueTask<IPttInputSource> CreateAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IPttInputSource>(new GlobalKeyboardPttSource(activationKey)
        {
            ToggleMode = toggleMode
        });
    }
}

public sealed class SerialPttInputSourceFactory : IPttInputSourceFactory
{
    private readonly string portName;
    private readonly int baudRate;

    public SerialPttInputSourceFactory(string portName, int baudRate = 9_600)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        if (baudRate is < 300 or > 4_000_000)
            throw new ArgumentOutOfRangeException(nameof(baudRate));
        this.portName = portName.Trim();
        this.baudRate = baudRate;
        Descriptor = new PttInputDescriptor(
            $"desktop-serial-{this.portName.ToLowerInvariant()}",
            "Serial hardware PTT",
            IsHardware: true,
            HasSettings: true);
    }

    public PttInputDescriptor Descriptor { get; }

    public ValueTask<IPttInputSource> CreateAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IPttInputSource>(new SerialPttSource(portName, baudRate));
    }
}
