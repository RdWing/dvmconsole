// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

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
    private readonly Func<KeyboardPttKey, IGlobalKeyboardPttBindingSource> createGlobalSource;
    private IGlobalKeyboardPttBindingSource? globalSource;
    private bool started;
    private bool disposed;

    public KeyboardPttBinding(KeyboardPttKey activationKey, bool toggleMode)
        : this(
            activationKey,
            toggleMode,
            static key => new GlobalKeyboardPttBindingSource(key))
    {
    }

    internal KeyboardPttBinding(
        KeyboardPttKey activationKey,
        bool toggleMode,
        Func<KeyboardPttKey, IGlobalKeyboardPttBindingSource> createGlobalSource)
    {
        this.createGlobalSource = createGlobalSource ??
            throw new ArgumentNullException(nameof(createGlobalSource));
        windowSource = new KeyboardPttSource(activationKey)
        {
            ToggleMode = toggleMode
        };
        windowSource.StateChanged += ForwardStateChanged;
    }

    public event EventHandler<KeyboardPttStateChange>? StateChanged;
    public event EventHandler<Exception>? CaptureFailed;

    private void ForwardCaptureFailed(object? sender, Exception failure) => CaptureFailed?.Invoke(this, failure);

    public KeyboardPttKey ActivationKey => windowSource.ActivationKey;

    public bool IsPressed => globalSource?.IsPressed ?? windowSource.IsPressed;

    public bool IsPressedFrom(KeyboardPttInputOrigin origin)
        => origin == KeyboardPttInputOrigin.OsGlobal && globalSource is not null
            ? globalSource.IsPressed
            : windowSource.IsPressed;

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
        if (globalSource is not null)
        {
            await globalSource.StartAsync(cancellationToken).ConfigureAwait(false);
            started = true;
            return new KeyboardPttStartResult(KeyboardPttAvailability.OsGlobal);
        }
        if (GlobalKeyboardPttSource.IsPlatformSupported)
        {
            IGlobalKeyboardPttBindingSource candidate = createGlobalSource(ActivationKey);
            candidate.ToggleMode = ToggleMode;
            candidate.InputSuppressed = windowSource.InputSuppressed;
            candidate.StateChanged += ForwardStateChanged;
            candidate.CaptureFailed += ForwardCaptureFailed;
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
                candidate.CaptureFailed -= ForwardCaptureFailed;
                await candidate.DisposeAsync().ConfigureAwait(false);
                globalCaptureError = exception;
            }
            catch
            {
                candidate.StateChanged -= ForwardStateChanged;
                candidate.CaptureFailed -= ForwardCaptureFailed;
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

    public bool HandleKeyDown(KeyboardPttKey key)
        => globalSource is not null
            ? key == ActivationKey
            : windowSource.HandleKeyDown(key);

    public bool HandleKeyUp(KeyboardPttKey key)
        => globalSource is not null
            ? key == ActivationKey
            : windowSource.HandleKeyUp(key);

    public void ReleaseToggleLatch(KeyboardPttInputOrigin origin)
    {
        if (origin == KeyboardPttInputOrigin.OsGlobal && globalSource is not null)
            globalSource.ReleaseToggleLatch();
        else
            windowSource.ReleaseToggleLatch();
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (!started)
            return;

        ReleaseAllToggleLatches();
        if (globalSource is not null)
            await globalSource.StopAsync(cancellationToken).ConfigureAwait(false);
        else
            await windowSource.StopAsync(cancellationToken).ConfigureAwait(false);
        started = false;
    }

    public void ReleaseAllToggleLatches()
    {
        windowSource.ReleaseToggleLatch();
        globalSource?.ReleaseToggleLatch();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;

        windowSource.StateChanged -= ForwardStateChanged;
        await windowSource.DisposeAsync().ConfigureAwait(false);
        if (globalSource is not null)
        {
            globalSource.StateChanged -= ForwardStateChanged;
            globalSource.CaptureFailed -= ForwardCaptureFailed;
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
                sender is IGlobalKeyboardPttBindingSource
                    ? KeyboardPttInputOrigin.OsGlobal
                    : KeyboardPttInputOrigin.WindowLocal));

    private static bool IsGlobalCaptureFailure(Exception exception)
        => exception is PlatformNotSupportedException or
            UnauthorizedAccessException or
            DllNotFoundException or
            EntryPointNotFoundException or
            BadImageFormatException or
            InvalidOperationException or
            TimeoutException or
            Win32Exception;
}

internal interface IGlobalKeyboardPttBindingSource : IAsyncDisposable
{
    event EventHandler<bool>? StateChanged;
    event EventHandler<Exception>? CaptureFailed;
    bool IsPressed { get; }
    bool ToggleMode { get; set; }
    bool InputSuppressed { get; set; }
    ValueTask StartAsync(CancellationToken cancellationToken = default);
    ValueTask StopAsync(CancellationToken cancellationToken = default);
    void ReleaseToggleLatch();
}

internal sealed class GlobalKeyboardPttBindingSource : IGlobalKeyboardPttBindingSource
{
    private readonly GlobalKeyboardPttSource inner;

    public GlobalKeyboardPttBindingSource(KeyboardPttKey activationKey)
    {
        inner = new GlobalKeyboardPttSource(activationKey);
        inner.StateChanged += HandleStateChanged;
        inner.CaptureFailed += HandleCaptureFailed;
    }

    public event EventHandler<bool>? StateChanged;
    public event EventHandler<Exception>? CaptureFailed;

    public bool IsPressed => inner.IsPressed;

    public bool ToggleMode
    {
        get => inner.ToggleMode;
        set => inner.ToggleMode = value;
    }

    public bool InputSuppressed
    {
        get => inner.InputSuppressed;
        set => inner.InputSuppressed = value;
    }

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
        => inner.StartAsync(cancellationToken);

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
        => inner.StopAsync(cancellationToken);

    public void ReleaseToggleLatch()
        => inner.ReleaseToggleLatch();

    public async ValueTask DisposeAsync()
    {
        inner.StateChanged -= HandleStateChanged;
        inner.CaptureFailed -= HandleCaptureFailed;
        await inner.DisposeAsync().ConfigureAwait(false);
    }

    private void HandleCaptureFailed(Exception failure)
    {
        DesktopCrashLog.Write("Global PTT capture lost; reactivate the keyboard binding", failure);
        CaptureFailed?.Invoke(this, failure);
    }

    private void HandleStateChanged(object? sender, bool pressed)
        => StateChanged?.Invoke(this, pressed);
}
