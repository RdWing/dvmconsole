using System.ComponentModel;
using DvmConsole.Audio;
using DvmConsole.Ptt;

namespace DvmConsole.Desktop;

internal enum KeyboardPttAvailability
{
    Disabled,
    OsGlobal,
    WindowFallback
}

internal sealed record KeyboardPttStartResult(
    KeyboardPttAvailability Availability,
    Exception? GlobalCaptureError = null);

internal enum KeyboardPttInputOrigin
{
    WindowLocal,
    OsGlobal
}

internal sealed record KeyboardPttStateChange(
    bool Pressed,
    KeyboardPttInputOrigin Origin);

// Owns one keyboard PTT binding and its OS-global/window-local source choice.
// Target selection remains a view-model policy so this lifecycle adapter can
// be reused by bindings with different transmit scopes.
internal sealed class KeyboardPttBinding : IAsyncDisposable
{
    private readonly KeyboardPttSource windowSource;
    private GlobalKeyboardPttSource? globalSource;
    private bool started;
    private bool disposed;

    public KeyboardPttBinding(KeyboardPttKey activationKey, bool toggleMode)
    {
        windowSource = new KeyboardPttSource(activationKey)
        {
            ToggleMode = toggleMode
        };
        windowSource.StateChanged += ForwardStateChanged;
    }

    public event EventHandler<KeyboardPttStateChange>? StateChanged;

    public KeyboardPttKey ActivationKey => windowSource.ActivationKey;

    public bool IsPressed => globalSource?.IsPressed ?? windowSource.IsPressed;

    public bool ToggleMode
    {
        get => windowSource.ToggleMode;
        set
        {
            windowSource.ToggleMode = value;
            if (globalSource is not null)
                globalSource.ToggleMode = value;
        }
    }

    public void SetInputSuppressed(bool suppressed)
    {
        windowSource.InputSuppressed = suppressed;
        if (globalSource is not null)
            globalSource.InputSuppressed = suppressed;
    }

    public async ValueTask<KeyboardPttStartResult> StartAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started)
        {
            return new KeyboardPttStartResult(
                ActivationKey == KeyboardPttKey.None
                    ? KeyboardPttAvailability.Disabled
                    : globalSource is null
                        ? KeyboardPttAvailability.WindowFallback
                        : KeyboardPttAvailability.OsGlobal);
        }

        if (ActivationKey == KeyboardPttKey.None)
        {
            started = true;
            return new KeyboardPttStartResult(KeyboardPttAvailability.Disabled);
        }

        Exception? globalCaptureError = null;
        if (GlobalKeyboardPttSource.IsPlatformSupported)
        {
            var candidate = new GlobalKeyboardPttSource(ActivationKey)
            {
                ToggleMode = ToggleMode,
                InputSuppressed = windowSource.InputSuppressed
            };
            candidate.StateChanged += ForwardStateChanged;
            try
            {
                await candidate.StartAsync(cancellationToken).ConfigureAwait(false);
                globalSource = candidate;
                started = true;
                return new KeyboardPttStartResult(KeyboardPttAvailability.OsGlobal);
            }
            catch (Exception exception) when (IsGlobalCaptureFailure(exception))
            {
                candidate.StateChanged -= ForwardStateChanged;
                await candidate.DisposeAsync().ConfigureAwait(false);
                globalCaptureError = exception;
            }
            catch
            {
                candidate.StateChanged -= ForwardStateChanged;
                await candidate.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        await windowSource.StartAsync(cancellationToken).ConfigureAwait(false);
        started = true;
        return new KeyboardPttStartResult(
            KeyboardPttAvailability.WindowFallback,
            globalCaptureError);
    }

    public bool HandleKeyDown(KeyboardPttKey key) => windowSource.HandleKeyDown(key);

    public bool HandleKeyUp(KeyboardPttKey key) => windowSource.HandleKeyUp(key);

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;

        windowSource.StateChanged -= ForwardStateChanged;
        await windowSource.DisposeAsync().ConfigureAwait(false);
        if (globalSource is not null)
        {
            globalSource.StateChanged -= ForwardStateChanged;
            await globalSource.DisposeAsync().ConfigureAwait(false);
            globalSource = null;
        }

        started = false;
        disposed = true;
    }

    private void ForwardStateChanged(object? sender, bool pressed)
        => StateChanged?.Invoke(
            this,
            new KeyboardPttStateChange(
                pressed,
                sender is GlobalKeyboardPttSource
                    ? KeyboardPttInputOrigin.OsGlobal
                    : KeyboardPttInputOrigin.WindowLocal));

    private static bool IsGlobalCaptureFailure(Exception exception)
        => exception is PlatformNotSupportedException or
            UnauthorizedAccessException or
            InvalidOperationException or
            TimeoutException or
            Win32Exception;
}
