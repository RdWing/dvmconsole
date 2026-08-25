namespace DvmConsole.Audio;

public enum KeyboardPttKey
{
    None,
    Space,
    F1,
    F2,
    F3,
    F4,
    F5,
    F6,
    F7,
    F8,
    F9,
    F10,
    F11,
    F12,
    F13,
    F14,
    F15,
    F16,
    F17,
    F18,
    F19
}

// Lifecycle-bound keyboard PTT adapter. The host maps its platform key
// events to <see cref="HandleKeyDown"/> and <see cref="HandleKeyUp"/>;
// this class keeps key-repeat and unrelated-key behavior deterministic.
public sealed class KeyboardPttSource : IPttSource
{
    private readonly KeyboardPttKey activationKey;
    private bool started;
    private bool disposed;
    private bool activationKeyDown;

    public KeyboardPttSource(KeyboardPttKey activationKey = KeyboardPttKey.Space)
    {
        this.activationKey = activationKey;
    }

    public event EventHandler<bool>? StateChanged;
    public bool IsPressed { get; private set; }
    public KeyboardPttKey ActivationKey => activationKey;
    public bool ToggleMode { get; set; }
    public bool InputSuppressed
    {
        get => inputSuppressed;
        set
        {
            if (inputSuppressed == value)
                return;
            inputSuppressed = value;
            if (inputSuppressed)
            {
                activationKeyDown = false;
                SetPressed(false);
            }
        }
    }

    private bool inputSuppressed;

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        started = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (started)
        {
            SetPressed(false);
            activationKeyDown = false;
            started = false;
        }
        return ValueTask.CompletedTask;
    }

    public bool HandleKeyDown(KeyboardPttKey key)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!started || key != activationKey || inputSuppressed)
            return false;

        if (ToggleMode)
        {
            if (activationKeyDown)
                return true;
            activationKeyDown = true;
            SetPressed(!IsPressed);
        }
        else
        {
            SetPressed(true);
        }

        return true;
    }

    public bool HandleKeyUp(KeyboardPttKey key)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!started || key != activationKey || inputSuppressed)
            return false;

        if (ToggleMode)
        {
            activationKeyDown = false;
        }
        else
        {
            SetPressed(false);
        }

        return true;
    }

    public ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            if (started)
                SetPressed(false);
            activationKeyDown = false;
            started = false;
            disposed = true;
        }
        return ValueTask.CompletedTask;
    }

    private void SetPressed(bool pressed)
    {
        if (IsPressed == pressed)
            return;

        IsPressed = pressed;
        StateChanged?.Invoke(this, pressed);
    }
}
