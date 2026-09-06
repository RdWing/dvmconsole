// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;
using DvmConsole.Ptt;

namespace DvmConsole.Desktop;

internal sealed record PttSourceStateChange(
    bool Pressed,
    PttTargetScope Scope,
    PttActivationSource Source);

internal enum PttActivationSource
{
    None,
    LocalChannelControl,
    WindowLocalKeyboard,
    OsGlobalKeyboard,
    SerialHardware
}

internal sealed record PttSessionStartResult(
    KeyboardPttStartResult GlobalKeyboard,
    KeyboardPttStartResult ActiveSystemKeyboard,
    Exception? SerialError);

internal enum PttSessionLifecycleState
{
    Stopped,
    Starting,
    Started,
    Stopping,
    StopFailed,
    Disposing,
    Disposed
}

internal sealed class PttSessionController : IAsyncDisposable, IHardwarePttInputState
{
    private readonly PttSettingsViewModel settings;
    private readonly Func<string, int, IPttInputSourceFactory> serialPttFactory;
    private readonly Func<PttTargetScope> getSerialTargetScope;
    private readonly Func<KeyboardPttKey, bool, KeyboardPttBinding> keyboardBindingFactory;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly AsyncDisposal disposal = new();
    private volatile KeyboardPttBinding globalKeyboard;
    private volatile KeyboardPttBinding activeSystemKeyboard;
    private volatile IPttInputSource? serialPtt;
    private bool eventsAttached;
    private PttSessionLifecycleState lifecycleState;
    private bool spaceInputSuppressed;
    private int disposalStarted;

    public bool HasSuppressedDuplicateKeyboardBinding { get; }

    public PttSessionController(
        PttSettingsViewModel settings,
        Func<string, int, IPttInputSourceFactory> serialPttFactory,
        Func<PttTargetScope> getSerialTargetScope)
        : this(
            settings,
            serialPttFactory,
            getSerialTargetScope,
            static (key, toggleMode) => new KeyboardPttBinding(key, toggleMode))
    {
    }

    internal PttSessionController(
        PttSettingsViewModel settings,
        Func<string, int, IPttInputSourceFactory> serialPttFactory,
        Func<PttTargetScope> getSerialTargetScope,
        Func<KeyboardPttKey, bool, KeyboardPttBinding> keyboardBindingFactory)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.serialPttFactory = serialPttFactory ?? throw new ArgumentNullException(nameof(serialPttFactory));
        this.getSerialTargetScope = getSerialTargetScope ?? throw new ArgumentNullException(nameof(getSerialTargetScope));
        this.keyboardBindingFactory = keyboardBindingFactory ??
            throw new ArgumentNullException(nameof(keyboardBindingFactory));
        KeyboardPttKey globalKey = settings.SelectedGlobalPttKey;
        KeyboardPttKey activeSystemKey = settings.SelectedActiveSystemPttKey;
        HasSuppressedDuplicateKeyboardBinding =
            globalKey != KeyboardPttKey.None && globalKey == activeSystemKey;
        globalKeyboard = keyboardBindingFactory(globalKey, settings.TogglePttMode);
        activeSystemKeyboard = keyboardBindingFactory(
            HasSuppressedDuplicateKeyboardBinding ? KeyboardPttKey.None : activeSystemKey,
            settings.TogglePttMode);
    }

    public PttSessionController(
        PttSettingsViewModel settings,
        Func<string, int, IPttSource> serialPttFactory,
        Func<PttTargetScope> getSerialTargetScope)
        : this(
            settings,
            (portName, baudRate) => new DelegateSerialPttInputSourceFactory(
                () => serialPttFactory(portName, baudRate)),
            getSerialTargetScope)
    {
    }

    public event EventHandler<PttSourceStateChange>? StateChanged;
    public event EventHandler<Exception>? CaptureFailed;
    private void HandleCaptureFailed(object? sender, Exception failure) => CaptureFailed?.Invoke(this, failure);

    public KeyboardPttKey GlobalKey => globalKeyboard.ActivationKey;
    public KeyboardPttKey ActiveSystemKey => activeSystemKeyboard.ActivationKey;
    public bool HasSerialSource => serialPtt is not null;
    public bool IsStarted => lifecycleState is
        PttSessionLifecycleState.Started or PttSessionLifecycleState.StopFailed;
    internal PttSessionLifecycleState LifecycleState => lifecycleState;

    public bool IsAnySourcePressed
        => globalKeyboard.IsPressed ||
           activeSystemKeyboard.IsPressed ||
           serialPtt?.IsPressed == true;

    public async ValueTask CreateInitialSerialSourceAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposalStarted) != 0, this);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposalStarted) != 0, this);
            await EnsureConfiguredSerialSourceAsync().ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public void AttachEvents()
    {
        if (eventsAttached)
            return;
        eventsAttached = true;
        globalKeyboard.StateChanged += HandleGlobalKeyboardStateChanged;
        globalKeyboard.CaptureFailed += HandleCaptureFailed;
        activeSystemKeyboard.StateChanged += HandleActiveSystemKeyboardStateChanged;
        activeSystemKeyboard.CaptureFailed += HandleCaptureFailed;
        if (serialPtt is not null)
            serialPtt.StateChanged += HandleSerialStateChanged;
    }

    public void SetToggleMode(bool toggleMode)
    {
        globalKeyboard.ToggleMode = toggleMode;
        activeSystemKeyboard.ToggleMode = toggleMode;
    }

    public async ValueTask<PttSessionStartResult> StartAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposalStarted) != 0, this);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposalStarted) != 0, this);
            return await StartCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async ValueTask<PttSessionStartResult> StartCoreAsync(
        CancellationToken cancellationToken)
    {
        if (lifecycleState == PttSessionLifecycleState.StopFailed)
        {
            throw new InvalidOperationException(
                "The previous PTT stop did not complete. Retry stopping before restarting input sources.");
        }

        KeyboardPttStartResult globalResult;
        KeyboardPttStartResult activeSystemResult;
        bool startedByThisCall = lifecycleState == PttSessionLifecycleState.Stopped;
        if (startedByThisCall)
        {
            lifecycleState = PttSessionLifecycleState.Starting;
            bool globalStarted = false;
            try
            {
                globalResult = await globalKeyboard.StartAsync(cancellationToken).ConfigureAwait(false);
                globalStarted = true;
                activeSystemResult = await activeSystemKeyboard.StartAsync(cancellationToken).ConfigureAwait(false);
                lifecycleState = PttSessionLifecycleState.Started;
            }
            catch
            {
                if (globalStarted)
                    await globalKeyboard.StopAsync(CancellationToken.None).ConfigureAwait(false);
                ReleaseAllKeyboardToggleLatches();
                lifecycleState = PttSessionLifecycleState.Stopped;
                throw;
            }
        }
        else
        {
            globalResult = CurrentStartResult(globalKeyboard);
            activeSystemResult = CurrentStartResult(activeSystemKeyboard);
        }

        Exception? serialError = null;
        try
        {
            if (serialPtt is null)
                await EnsureConfiguredSerialSourceAsync().ConfigureAwait(false);
            if (serialPtt is not null)
            {
                try
                {
                    await serialPtt.StartAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsExpectedSerialException(exception))
                {
                    serialError = exception;
                }
                catch
                {
                    if (startedByThisCall)
                        await StopKeyboardBindingsAfterFailedStartAsync().ConfigureAwait(false);
                    throw;
                }
            }
        }
        catch (Exception exception) when (IsExpectedSerialException(exception))
        {
            serialError = exception;
        }
        catch
        {
            if (startedByThisCall)
                await StopKeyboardBindingsAfterFailedStartAsync().ConfigureAwait(false);
            throw;
        }

        return new PttSessionStartResult(globalResult, activeSystemResult, serialError);
    }

    private async ValueTask EnsureConfiguredSerialSourceAsync()
    {
        if (serialPtt is not null ||
            !settings.SerialPttEnabled ||
            settings.SerialPttPortName.Length == 0)
        {
            return;
        }

        IPttInputSource created = await serialPttFactory(
                settings.SerialPttPortName,
                settings.SerialPttBaudRate)
            .CreateAsync()
            .ConfigureAwait(false);
        if (eventsAttached)
            created.StateChanged += HandleSerialStateChanged;
        serialPtt = created;
    }

    private async Task StopKeyboardBindingsAfterFailedStartAsync()
    {
        ReleaseAllKeyboardToggleLatches();
        var cleanup = new AsyncCleanup();
        await cleanup.RunTaskAsync(
            () => globalKeyboard.StopAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
        await cleanup.RunTaskAsync(
            () => activeSystemKeyboard.StopAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
        lifecycleState = PttSessionLifecycleState.Stopped;
        cleanup.ThrowIfFailed();
    }

    public async Task ReplaceSerialSourceAsync(
        bool enabled,
        string portName,
        int baudRate,
        Action persistSettings)
    {
        ArgumentNullException.ThrowIfNull(persistSettings);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposalStarted) != 0, this);
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposalStarted) != 0, this);
            IPttInputSource? candidate = null;
            IPttInputSource? previous = serialPtt;
            bool previousStopped = false;
            try
            {
                if (enabled)
                {
                    candidate = await serialPttFactory(portName, baudRate)
                        .CreateAsync()
                        .ConfigureAwait(false);
                    if (eventsAttached)
                        candidate.StateChanged += HandleSerialStateChanged;
                }

                if (previous is not null && IsStarted)
                {
                    await previous.StopAsync().ConfigureAwait(false);
                    previousStopped = true;
                }
                if (candidate is not null && IsStarted)
                    await candidate.StartAsync().ConfigureAwait(false);

                persistSettings();
                serialPtt = candidate;
                candidate = null;
            }
            catch (Exception replacementFailure)
            {
                if (candidate is not null)
                {
                    try
                    {
                        await StopAndDisposeSerialPttAsync(candidate).ConfigureAwait(false);
                    }
                    catch (Exception cleanupFailure)
                    {
                        replacementFailure = new AggregateException(
                            "Serial PTT replacement and candidate cleanup both failed.",
                            replacementFailure,
                            cleanupFailure);
                    }
                }

                try
                {
                    if (previous is not null && previousStopped)
                        await previous.StartAsync().ConfigureAwait(false);
                    serialPtt = previous;
                }
                catch (Exception rollbackFailure)
                {
                    throw new AggregateException(
                        "Serial PTT replacement failed and the previous source could not be restored.",
                        replacementFailure,
                        rollbackFailure);
                }

                throw replacementFailure;
            }

            if (previous is not null)
            {
                try
                {
                    if (previousStopped)
                        await DisposeSerialPttAsync(previous).ConfigureAwait(false);
                    else
                        await StopAndDisposeSerialPttAsync(previous).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Trace.TraceError(
                        "Retired serial PTT source cleanup failed: {0}",
                        exception);
                }
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task ReplaceKeyboardBindingAsync(
        PttTargetScope scope,
        KeyboardPttKey key,
        Func<Task> stopLatchedTransmit)
    {
        ArgumentNullException.ThrowIfNull(stopLatchedTransmit);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposalStarted) != 0, this);
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposalStarted) != 0, this);
            await ReplaceKeyboardBindingCoreAsync(scope, key, stopLatchedTransmit).ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task ReplaceKeyboardBindingCoreAsync(
        PttTargetScope scope,
        KeyboardPttKey key,
        Func<Task> stopLatchedTransmit)
    {
        bool activeSystemOnly = scope == PttTargetScope.ActiveSystem;
        KeyboardPttKey otherKey = activeSystemOnly
            ? globalKeyboard.ActivationKey
            : activeSystemKeyboard.ActivationKey;
        if (key != KeyboardPttKey.None && key == otherKey)
        {
            throw new InvalidOperationException(
                $"{key} is already assigned to the other keyboard PTT scope.");
        }

        KeyboardPttBinding previous = activeSystemOnly
            ? activeSystemKeyboard
            : globalKeyboard;
        bool anotherSourcePressed = activeSystemOnly
            ? globalKeyboard.IsPressed || serialPtt?.IsPressed == true
            : activeSystemKeyboard.IsPressed || serialPtt?.IsPressed == true;
        if (previous.IsPressed && !anotherSourcePressed)
            await stopLatchedTransmit().ConfigureAwait(false);

        EventHandler<KeyboardPttStateChange> handler = activeSystemOnly
            ? HandleActiveSystemKeyboardStateChanged
            : HandleGlobalKeyboardStateChanged;
        if (IsStarted)
            await previous.StopAsync(CancellationToken.None).ConfigureAwait(false);

        KeyboardPttBinding? replacement = null;
        try
        {
            replacement = keyboardBindingFactory(key, settings.TogglePttMode);
            replacement.SetInputSuppressed(
                spaceInputSuppressed && key == KeyboardPttKey.Space);
            if (eventsAttached)
            {
                replacement.StateChanged += handler;
                replacement.CaptureFailed += HandleCaptureFailed;
            }
            if (IsStarted)
                await replacement.StartAsync(CancellationToken.None).ConfigureAwait(false);

            if (activeSystemOnly)
                activeSystemKeyboard = replacement;
            else
                globalKeyboard = replacement;
            previous.StateChanged -= handler;
            previous.CaptureFailed -= HandleCaptureFailed;
        }
        catch (Exception replacementFailure)
        {
            if (replacement is not null)
            {
                replacement.StateChanged -= handler;
                replacement.CaptureFailed -= HandleCaptureFailed;
                try
                {
                    await replacement.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupFailure)
                {
                    replacementFailure = new AggregateException(
                        "Keyboard PTT replacement and candidate cleanup both failed.",
                        replacementFailure,
                        cleanupFailure);
                }
            }

            try
            {
                if (IsStarted)
                    await previous.StartAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackFailure)
            {
                throw new AggregateException(
                    "Keyboard PTT replacement failed and the previous binding could not be restored.",
                    replacementFailure,
                    rollbackFailure);
            }
            throw replacementFailure;
        }

        try
        {
            await previous.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(
                "Retired keyboard PTT binding cleanup failed: {0}",
                exception);
        }
    }

    public bool HandleKeyDown(KeyboardPttKey key)
    {
        bool globalHandled = globalKeyboard.HandleKeyDown(key);
        bool activeSystemHandled = activeSystemKeyboard.HandleKeyDown(key);
        return globalHandled || activeSystemHandled;
    }

    public bool HandleKeyUp(KeyboardPttKey key)
    {
        bool globalHandled = globalKeyboard.HandleKeyUp(key);
        bool activeSystemHandled = activeSystemKeyboard.HandleKeyUp(key);
        return globalHandled || activeSystemHandled;
    }

    public bool IsConfiguredKey(KeyboardPttKey key)
        => globalKeyboard.ActivationKey == key || activeSystemKeyboard.ActivationKey == key;

    public bool IsInputPressed(
        PttTargetScope scope,
        PttActivationSource source)
    {
        if (source == PttActivationSource.SerialHardware)
            return serialPtt?.IsPressed == true;

        KeyboardPttBinding binding = scope == PttTargetScope.ActiveSystem
            ? activeSystemKeyboard
            : globalKeyboard;
        KeyboardPttInputOrigin origin = source == PttActivationSource.OsGlobalKeyboard
            ? KeyboardPttInputOrigin.OsGlobal
            : KeyboardPttInputOrigin.WindowLocal;
        return binding.IsPressedFrom(origin);
    }

    public void ReleaseKeyboardToggleLatch(
        PttTargetScope scope,
        PttActivationSource source)
    {
        KeyboardPttBinding binding = scope == PttTargetScope.ActiveSystem
            ? activeSystemKeyboard
            : globalKeyboard;
        KeyboardPttInputOrigin origin = source == PttActivationSource.OsGlobalKeyboard
            ? KeyboardPttInputOrigin.OsGlobal
            : KeyboardPttInputOrigin.WindowLocal;
        binding.ReleaseToggleLatch(origin);
    }

    public void ReleaseAllKeyboardToggleLatches()
    {
        globalKeyboard.ReleaseAllToggleLatches();
        activeSystemKeyboard.ReleaseAllToggleLatches();
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async ValueTask StopCoreAsync(CancellationToken cancellationToken)
    {
        if (lifecycleState == PttSessionLifecycleState.Stopped)
            return;

        lifecycleState = PttSessionLifecycleState.Stopping;
        ReleaseAllKeyboardToggleLatches();
        var cleanup = new AsyncCleanup();
        await cleanup.RunTaskAsync(
            () => globalKeyboard.StopAsync(cancellationToken).AsTask()).ConfigureAwait(false);
        await cleanup.RunTaskAsync(
            () => activeSystemKeyboard.StopAsync(cancellationToken).AsTask()).ConfigureAwait(false);
        if (serialPtt is not null)
        {
            await cleanup.RunTaskAsync(
                () => serialPtt.StopAsync(cancellationToken).AsTask()).ConfigureAwait(false);
        }
        if (cleanup.HasFailures)
        {
            lifecycleState = PttSessionLifecycleState.StopFailed;
            cleanup.ThrowIfFailed();
        }
        lifecycleState = PttSessionLifecycleState.Stopped;
    }

    public void SetSpaceInputSuppressed(bool suppressed)
    {
        spaceInputSuppressed = suppressed;
        globalKeyboard.SetInputSuppressed(
            suppressed && globalKeyboard.ActivationKey == KeyboardPttKey.Space);
        activeSystemKeyboard.SetInputSuppressed(
            suppressed && activeSystemKeyboard.ActivationKey == KeyboardPttKey.Space);
    }

    public ValueTask DisposeAsync()
        => disposal.RunAsync(DisposeCoreAsync);

    private async Task DisposeCoreAsync()
    {
        Volatile.Write(ref disposalStarted, 1);
        var cleanup = new AsyncCleanup();
        bool lifecycleGateEntered = false;
        try
        {
            await lifecycleGate.WaitAsync().ConfigureAwait(false);
            lifecycleGateEntered = true;
        }
        catch (Exception exception)
        {
            cleanup.Capture(exception);
        }
        if (!lifecycleGateEntered)
        {
            cleanup.ThrowIfFailed();
            return;
        }

        lifecycleState = PttSessionLifecycleState.Disposing;

        await cleanup.RunTaskAsync(() => StopCoreAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
        cleanup.Run(() =>
        {
            globalKeyboard.StateChanged -= HandleGlobalKeyboardStateChanged;
            globalKeyboard.CaptureFailed -= HandleCaptureFailed;
            activeSystemKeyboard.StateChanged -= HandleActiveSystemKeyboardStateChanged;
            activeSystemKeyboard.CaptureFailed -= HandleCaptureFailed;
        });
        await cleanup.RunTaskAsync(() => globalKeyboard.DisposeAsync().AsTask()).ConfigureAwait(false);
        await cleanup.RunTaskAsync(() => activeSystemKeyboard.DisposeAsync().AsTask()).ConfigureAwait(false);

        try
        {
            IPttInputSource? currentSerialPtt = serialPtt;
            serialPtt = null;
            if (currentSerialPtt is not null)
            {
                await cleanup.RunTaskAsync(
                    () => StopAndDisposeSerialPttAsync(currentSerialPtt)).ConfigureAwait(false);
            }
        }
        finally
        {
            lifecycleGate.Release();
        }

        cleanup.Run(lifecycleGate.Dispose);
        lifecycleState = PttSessionLifecycleState.Disposed;
        cleanup.ThrowIfFailed();
    }

    private async Task StopAndDisposeSerialPttAsync(IPttInputSource source)
    {
        try
        {
            await source.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            source.StateChanged -= HandleSerialStateChanged;
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task DisposeSerialPttAsync(IPttInputSource source)
    {
        source.StateChanged -= HandleSerialStateChanged;
        await source.DisposeAsync().ConfigureAwait(false);
    }

    private void HandleGlobalKeyboardStateChanged(object? sender, KeyboardPttStateChange change)
        => PublishStateChange(new PttSourceStateChange(
            change.Pressed,
            PttTargetScope.AllSelectedResources,
            ToActivationSource(change.Origin)));

    private void HandleActiveSystemKeyboardStateChanged(object? sender, KeyboardPttStateChange change)
        => PublishStateChange(new PttSourceStateChange(
            change.Pressed,
            PttTargetScope.ActiveSystem,
            ToActivationSource(change.Origin)));

    private void HandleSerialStateChanged(object? sender, bool pressed)
        => PublishStateChange(new PttSourceStateChange(
            pressed,
            getSerialTargetScope(),
            PttActivationSource.SerialHardware));

    private void PublishStateChange(PttSourceStateChange change)
    {
        Delegate[] subscribers = StateChanged?.GetInvocationList() ?? [];
        foreach (Delegate subscriber in subscribers)
        {
            try
            {
                ((EventHandler<PttSourceStateChange>)subscriber)(this, change);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceError(
                    "PTT state observer failed: {0}",
                    exception);
            }
        }
    }

    private static PttActivationSource ToActivationSource(KeyboardPttInputOrigin origin)
        => origin == KeyboardPttInputOrigin.OsGlobal
            ? PttActivationSource.OsGlobalKeyboard
            : PttActivationSource.WindowLocalKeyboard;

    private static KeyboardPttStartResult CurrentStartResult(KeyboardPttBinding binding)
        => binding.ActivationKey == KeyboardPttKey.None
            ? new KeyboardPttStartResult(KeyboardPttAvailability.Disabled)
            : new KeyboardPttStartResult(KeyboardPttAvailability.WindowFallback);

    private static bool IsExpectedSerialException(Exception exception)
        => exception is IOException or
            InvalidOperationException or
            UnauthorizedAccessException or
            ArgumentException or
            PlatformNotSupportedException or
            System.ComponentModel.Win32Exception;

}
