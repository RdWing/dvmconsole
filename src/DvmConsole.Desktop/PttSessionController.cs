using DvmConsole.Audio;

namespace DvmConsole.Desktop;

internal sealed record PttSourceStateChange(
    bool Pressed,
    PttTargetScope Scope);

internal sealed record PttSessionStartResult(
    KeyboardPttStartResult GlobalKeyboard,
    KeyboardPttStartResult ActiveSystemKeyboard,
    Exception? SerialError);

internal sealed class PttSessionController : IAsyncDisposable
{
    private readonly PttSettingsViewModel settings;
    private readonly Func<string, int, IPttSource> serialPttFactory;
    private readonly Func<PttTargetScope> getSerialTargetScope;
    private readonly SemaphoreSlim serialChangeGate = new(1, 1);
    private readonly AsyncDisposal disposal = new();
    private KeyboardPttBinding globalKeyboard;
    private KeyboardPttBinding activeSystemKeyboard;
    private IPttSource? serialPtt;
    private bool eventsAttached;
    private bool started;

    public PttSessionController(
        PttSettingsViewModel settings,
        Func<string, int, IPttSource> serialPttFactory,
        Func<PttTargetScope> getSerialTargetScope)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.serialPttFactory = serialPttFactory ?? throw new ArgumentNullException(nameof(serialPttFactory));
        this.getSerialTargetScope = getSerialTargetScope ?? throw new ArgumentNullException(nameof(getSerialTargetScope));
        globalKeyboard = new KeyboardPttBinding(
            settings.SelectedGlobalPttKey,
            settings.TogglePttMode);
        activeSystemKeyboard = new KeyboardPttBinding(
            settings.SelectedActiveSystemPttKey,
            settings.TogglePttMode);
    }

    public event EventHandler<PttSourceStateChange>? StateChanged;

    public KeyboardPttKey GlobalKey => globalKeyboard.ActivationKey;
    public KeyboardPttKey ActiveSystemKey => activeSystemKeyboard.ActivationKey;
    public bool HasSerialSource => serialPtt is not null;
    public bool IsStarted => started;

    public bool IsAnySourcePressed
        => globalKeyboard.IsPressed ||
           activeSystemKeyboard.IsPressed ||
           serialPtt?.IsPressed == true;

    public void CreateInitialSerialSource()
    {
        if (serialPtt is not null ||
            !settings.SerialPttEnabled ||
            settings.SerialPttPortName.Length == 0)
        {
            return;
        }

        serialPtt = serialPttFactory(
            settings.SerialPttPortName,
            settings.SerialPttBaudRate);
    }

    public void AttachEvents()
    {
        if (eventsAttached)
            return;
        eventsAttached = true;
        globalKeyboard.StateChanged += HandleGlobalKeyboardStateChanged;
        activeSystemKeyboard.StateChanged += HandleActiveSystemKeyboardStateChanged;
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
        KeyboardPttStartResult globalResult;
        KeyboardPttStartResult activeSystemResult;
        if (!started)
        {
            globalResult = await globalKeyboard.StartAsync(cancellationToken).ConfigureAwait(false);
            activeSystemResult = await activeSystemKeyboard.StartAsync(cancellationToken).ConfigureAwait(false);
            started = true;
        }
        else
        {
            globalResult = CurrentStartResult(globalKeyboard);
            activeSystemResult = CurrentStartResult(activeSystemKeyboard);
        }

        Exception? serialError = null;
        await serialChangeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
            }
        }
        finally
        {
            serialChangeGate.Release();
        }

        return new PttSessionStartResult(globalResult, activeSystemResult, serialError);
    }

    public async Task ReplaceSerialSourceAsync(
        bool enabled,
        string portName,
        int baudRate,
        Action persistSettings)
    {
        ArgumentNullException.ThrowIfNull(persistSettings);
        await serialChangeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            IPttSource? previous = serialPtt;
            serialPtt = null;
            if (previous is not null)
                await StopAndDisposeSerialPttAsync(previous).ConfigureAwait(false);

            persistSettings();
            if (!enabled)
                return;

            IPttSource? candidate = null;
            try
            {
                candidate = serialPttFactory(portName, baudRate);
                if (eventsAttached)
                    candidate.StateChanged += HandleSerialStateChanged;
                if (started)
                    await candidate.StartAsync().ConfigureAwait(false);
                serialPtt = candidate;
            }
            catch
            {
                if (candidate is not null)
                {
                    candidate.StateChanged -= HandleSerialStateChanged;
                    await candidate.DisposeAsync().ConfigureAwait(false);
                }
                throw;
            }
        }
        finally
        {
            serialChangeGate.Release();
        }
    }

    public async Task ReplaceKeyboardBindingAsync(
        PttTargetScope scope,
        KeyboardPttKey key,
        Func<Task> stopLatchedTransmit)
    {
        ArgumentNullException.ThrowIfNull(stopLatchedTransmit);
        bool activeSystemOnly = scope == PttTargetScope.ActiveSystem;
        KeyboardPttBinding previous = activeSystemOnly
            ? activeSystemKeyboard
            : globalKeyboard;
        bool anotherSourcePressed = activeSystemOnly
            ? globalKeyboard.IsPressed || serialPtt?.IsPressed == true
            : activeSystemKeyboard.IsPressed || serialPtt?.IsPressed == true;
        if (previous.IsPressed && !anotherSourcePressed)
            await stopLatchedTransmit().ConfigureAwait(false);

        EventHandler<bool> handler = activeSystemOnly
            ? HandleActiveSystemKeyboardStateChanged
            : HandleGlobalKeyboardStateChanged;
        previous.StateChanged -= handler;
        await previous.DisposeAsync().ConfigureAwait(false);

        var replacement = new KeyboardPttBinding(key, settings.TogglePttMode);
        if (eventsAttached)
            replacement.StateChanged += handler;
        if (activeSystemOnly)
            activeSystemKeyboard = replacement;
        else
            globalKeyboard = replacement;

        if (started)
            await replacement.StartAsync(CancellationToken.None).ConfigureAwait(false);
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

    public ValueTask DisposeAsync()
        => disposal.RunAsync(DisposeCoreAsync);

    private async Task DisposeCoreAsync()
    {
        var cleanup = new AsyncCleanup();
        cleanup.Run(() =>
        {
            globalKeyboard.StateChanged -= HandleGlobalKeyboardStateChanged;
            activeSystemKeyboard.StateChanged -= HandleActiveSystemKeyboardStateChanged;
        });
        await cleanup.RunTaskAsync(() => globalKeyboard.DisposeAsync().AsTask()).ConfigureAwait(false);
        await cleanup.RunTaskAsync(() => activeSystemKeyboard.DisposeAsync().AsTask()).ConfigureAwait(false);

        bool serialGateEntered = false;
        try
        {
            await serialChangeGate.WaitAsync().ConfigureAwait(false);
            serialGateEntered = true;
        }
        catch (Exception exception)
        {
            cleanup.Capture(exception);
        }
        if (serialGateEntered)
        {
            try
            {
                IPttSource? currentSerialPtt = serialPtt;
                serialPtt = null;
                if (currentSerialPtt is not null)
                {
                    await cleanup.RunTaskAsync(
                        () => StopAndDisposeSerialPttAsync(currentSerialPtt)).ConfigureAwait(false);
                }
            }
            finally
            {
                serialChangeGate.Release();
            }
        }

        cleanup.Run(serialChangeGate.Dispose);
        cleanup.ThrowIfFailed();
    }

    private async Task StopAndDisposeSerialPttAsync(IPttSource source)
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

    private void HandleGlobalKeyboardStateChanged(object? sender, bool pressed)
        => StateChanged?.Invoke(
            this,
            new PttSourceStateChange(pressed, PttTargetScope.AllSelectedResources));

    private void HandleActiveSystemKeyboardStateChanged(object? sender, bool pressed)
        => StateChanged?.Invoke(
            this,
            new PttSourceStateChange(pressed, PttTargetScope.ActiveSystem));

    private void HandleSerialStateChanged(object? sender, bool pressed)
        => StateChanged?.Invoke(
            this,
            new PttSourceStateChange(pressed, getSerialTargetScope()));

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
