// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;
using DvmConsole.Ptt;
using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class PttSessionControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ToggleKeyboardPressReleasesTransmissionStartedByChannelCard(
        bool osGlobal)
    {
        PttActivationSource keyboardSource = osGlobal
            ? PttActivationSource.OsGlobalKeyboard
            : PttActivationSource.WindowLocalKeyboard;
        var arbiter = new PttActivationArbiter();
        arbiter.RecordStarted(PttActivationSource.LocalChannelControl);

        Assert.True(arbiter.ShouldReleaseFromKeyboard(
            toggleMode: true,
            hasActiveTransmit: true,
            keyboardSource));
        Assert.False(arbiter.ShouldReleaseFromKeyboard(
            toggleMode: false,
            hasActiveTransmit: true,
            keyboardSource));

        arbiter.RecordStarted(PttActivationSource.SerialHardware);
        Assert.False(arbiter.ShouldReleaseFromKeyboard(
            toggleMode: true,
            hasActiveTransmit: true,
            keyboardSource));
    }

    [Fact]
    public void ClearedPttOwnershipCannotAffectTheNextFreshPress()
    {
        var arbiter = new PttActivationArbiter();
        arbiter.RecordStarted(PttActivationSource.LocalChannelControl);
        arbiter.Clear();

        Assert.Equal(PttActivationSource.None, arbiter.Owner);
        Assert.False(arbiter.ShouldReleaseFromKeyboard(
            toggleMode: true,
            hasActiveTransmit: true,
            PttActivationSource.WindowLocalKeyboard));
    }

    [Fact]
    public void OwningKeyboardScopeReleasesWithoutBeingBlockedByAnotherInput()
    {
        var arbiter = new PttActivationArbiter();
        arbiter.RecordStarted(
            PttActivationSource.OsGlobalKeyboard,
            PttTargetScope.AllSelectedResources);

        Assert.True(arbiter.ShouldReleaseFromInput(
            PttTargetScope.AllSelectedResources,
            PttActivationSource.WindowLocalKeyboard));
        Assert.False(arbiter.ShouldReleaseFromInput(
            PttTargetScope.ActiveSystem,
            PttActivationSource.WindowLocalKeyboard));
        Assert.False(arbiter.ShouldReleaseFromInput(
            PttTargetScope.AllSelectedResources,
            PttActivationSource.SerialHardware));
    }

    [Fact]
    public async Task ExistingDuplicateKeyboardSettingsGiveGlobalBindingPrecedence()
    {
        var settings = new PttSettingsViewModel(
            KeyboardPttKey.F8,
            KeyboardPttKey.F8,
            togglePttMode: false,
            serialPttEnabled: false,
            serialPttActiveSystemOnly: false,
            serialPttPortName: string.Empty,
            serialPttBaudRate: 9_600);
        var controller = new PttSessionController(
            settings,
            (_, _) => new DelegateSerialPttInputSourceFactory(() => new TestPttSource([])),
            () => PttTargetScope.AllSelectedResources);

        Assert.True(controller.HasSuppressedDuplicateKeyboardBinding);
        Assert.Equal(KeyboardPttKey.F8, controller.GlobalKey);
        Assert.Equal(KeyboardPttKey.None, controller.ActiveSystemKey);

        await controller.DisposeAsync();
    }

    [Fact]
    public async Task ReplacementRejectsDuplicateKeyboardAssignment()
    {
        var settings = new PttSettingsViewModel(
            KeyboardPttKey.F8,
            KeyboardPttKey.F9,
            togglePttMode: false,
            serialPttEnabled: false,
            serialPttActiveSystemOnly: false,
            serialPttPortName: string.Empty,
            serialPttBaudRate: 9_600);
        var controller = new PttSessionController(
            settings,
            (_, _) => new DelegateSerialPttInputSourceFactory(() => new TestPttSource([])),
            () => PttTargetScope.AllSelectedResources);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.ReplaceKeyboardBindingAsync(
                PttTargetScope.ActiveSystem,
                KeyboardPttKey.F8,
                () => Task.CompletedTask));

        Assert.Equal(KeyboardPttKey.F9, controller.ActiveSystemKey);
        await controller.DisposeAsync();
    }

    [Fact]
    public async Task GlobalKeyboardBindingDoesNotDoubleToggleFromWindowEvents()
    {
        var global = new TestGlobalKeyboardBindingSource();
        var binding = new KeyboardPttBinding(
            KeyboardPttKey.F8,
            toggleMode: true,
            _ => global);
        var changes = new List<KeyboardPttStateChange>();
        binding.StateChanged += (_, change) => changes.Add(change);
        await binding.StartAsync();

        Assert.True(binding.HandleKeyDown(KeyboardPttKey.F8));
        global.Raise(pressed: true);
        Assert.True(binding.HandleKeyUp(KeyboardPttKey.F8));
        Assert.True(binding.HandleKeyDown(KeyboardPttKey.F8));
        global.Raise(pressed: false);
        Assert.True(binding.HandleKeyUp(KeyboardPttKey.F8));

        Assert.Equal(
            [
                new KeyboardPttStateChange(true, KeyboardPttInputOrigin.OsGlobal),
                new KeyboardPttStateChange(false, KeyboardPttInputOrigin.OsGlobal)
            ],
            changes);
        await binding.DisposeAsync();
    }

    [Theory]
    [InlineData(typeof(DllNotFoundException))]
    [InlineData(typeof(EntryPointNotFoundException))]
    [InlineData(typeof(BadImageFormatException))]
    public async Task NativeGlobalKeyboardLoadFailureFallsBackToWindowInput(
        Type exceptionType)
    {
        Exception failure = (Exception)Activator.CreateInstance(exceptionType)!;
        var source = new FailingGlobalKeyboardBindingSource(failure);
        await using var binding = new KeyboardPttBinding(
            KeyboardPttKey.F8,
            toggleMode: false,
            _ => source);

        KeyboardPttStartResult result = await binding.StartAsync();

        Assert.Equal(KeyboardPttAvailability.WindowFallback, result.Availability);
        Assert.Same(failure, result.GlobalCaptureError);
        Assert.True(source.Disposed);
        Assert.True(binding.HandleKeyDown(KeyboardPttKey.F8));
        Assert.True(binding.HandleKeyUp(KeyboardPttKey.F8));
    }

    [Fact]
    public async Task SerialEventsUseAppliedScopeInsteadOfUnappliedPresentationState()
    {
        var source = new TestPttSource([]);
        var settings = CreateSettings();
        PttTargetScope appliedScope = PttTargetScope.AllSelectedResources;
        var controller = new PttSessionController(
            settings,
            (_, _) => source,
            () => appliedScope);
        await controller.CreateInitialSerialSourceAsync();
        var changes = new List<PttSourceStateChange>();
        controller.StateChanged += (_, change) => changes.Add(change);
        controller.AttachEvents();
        await controller.StartAsync();

        settings.SerialPttActiveSystemOnly = true;
        source.Raise(true);
        appliedScope = PttTargetScope.ActiveSystem;
        source.Raise(false);

        Assert.Equal(
            [
                new PttSourceStateChange(
                    true,
                    PttTargetScope.AllSelectedResources,
                    PttActivationSource.SerialHardware),
                new PttSourceStateChange(
                    false,
                    PttTargetScope.ActiveSystem,
                    PttActivationSource.SerialHardware)
            ],
            changes);
        await controller.DisposeAsync();
    }

    [Fact]
    public async Task ThrowingPttObserverCannotBlockLaterSubscribers()
    {
        var source = new TestPttSource([]);
        var controller = new PttSessionController(
            CreateSettings(),
            (_, _) => source,
            () => PttTargetScope.AllSelectedResources);
        await controller.CreateInitialSerialSourceAsync();
        PttSourceStateChange? observed = null;
        controller.StateChanged += (_, _) => throw new InvalidOperationException("observer failed");
        controller.StateChanged += (_, change) => observed = change;
        controller.AttachEvents();
        await controller.StartAsync();

        source.Raise(pressed: true);

        Assert.Equal(
            new PttSourceStateChange(
                true,
                PttTargetScope.AllSelectedResources,
                PttActivationSource.SerialHardware),
            observed);
        await controller.DisposeAsync();
    }

    [Fact]
    public async Task SerialReplacementPreservesShutdownPersistConstructionOrder()
    {
        var operations = new List<string>();
        int sourceNumber = 0;
        var settings = CreateSettings();
        var controller = new PttSessionController(
            settings,
            (_, _) =>
            {
                sourceNumber++;
                operations.Add($"create-{sourceNumber}");
                return new TestPttSource(operations);
            },
            () => PttTargetScope.AllSelectedResources);
        await controller.CreateInitialSerialSourceAsync();
        controller.AttachEvents();
        await controller.StartAsync();
        operations.Clear();

        await controller.ReplaceSerialSourceAsync(
            enabled: true,
            "replacement",
            19_200,
            () => operations.Add("persist"));

        Assert.Equal(
            ["create-2", "stop", "start", "persist", "dispose"],
            operations);
        await controller.DisposeAsync();
    }

    [Fact]
    public async Task FailedSerialReplacementRestoresThePreviousSource()
    {
        var operations = new List<string>();
        int sourceNumber = 0;
        var controller = new PttSessionController(
            CreateSettings(),
            (_, _) =>
            {
                sourceNumber++;
                operations.Add($"create-{sourceNumber}");
                return new TestPttSource(
                    operations,
                    failStart: sourceNumber == 2);
            },
            () => PttTargetScope.AllSelectedResources);
        await controller.CreateInitialSerialSourceAsync();
        controller.AttachEvents();
        await controller.StartAsync();
        operations.Clear();

        await Assert.ThrowsAsync<IOException>(() => controller.ReplaceSerialSourceAsync(
            enabled: true,
            "replacement",
            19_200,
            () => operations.Add("persist")));

        Assert.True(controller.HasSerialSource);
        Assert.True(controller.IsStarted);
        Assert.Equal(
            ["create-2", "stop", "start", "stop", "dispose", "start"],
            operations);
        await controller.DisposeAsync();
    }

    [Fact]
    public async Task FailedStopRetainsLifecycleOwnershipUntilRetrySucceeds()
    {
        var source = new TestPttSource([], stopFailures: 1);
        var controller = new PttSessionController(
            CreateSettings(),
            (_, _) => source,
            () => PttTargetScope.AllSelectedResources);
        await controller.CreateInitialSerialSourceAsync();
        await controller.StartAsync();

        await Assert.ThrowsAsync<IOException>(() => controller.StopAsync().AsTask());

        Assert.True(controller.IsStarted);
        Assert.Equal(PttSessionLifecycleState.StopFailed, controller.LifecycleState);

        await controller.StopAsync();

        Assert.False(controller.IsStarted);
        Assert.Equal(PttSessionLifecycleState.Stopped, controller.LifecycleState);
        await controller.DisposeAsync();
    }

    [Fact]
    public async Task PartialKeyboardStartFailureStopsTheAlreadyActiveGlobalHook()
    {
        var settings = new PttSettingsViewModel(
            KeyboardPttKey.F8,
            KeyboardPttKey.F9,
            togglePttMode: false,
            serialPttEnabled: false,
            serialPttActiveSystemOnly: false,
            serialPttPortName: string.Empty,
            serialPttBaudRate: 9_600);
        var operations = new List<string>();
        var controller = new PttSessionController(
            settings,
            (_, _) => new DelegateSerialPttInputSourceFactory(() => new TestPttSource([])),
            () => PttTargetScope.AllSelectedResources,
            (key, toggleMode) => new KeyboardPttBinding(
                key,
                toggleMode,
                _ => new TestGlobalKeyboardBindingSource(
                    key.ToString(),
                    operations,
                    failStart: key == KeyboardPttKey.F9)));

        await Assert.ThrowsAsync<IOException>(() => controller.StartAsync().AsTask());

        Assert.False(controller.IsStarted);
        Assert.Equal(["F8:start", "F9:start", "F8:stop"], operations);
        await controller.DisposeAsync();
    }

    [Fact]
    public async Task ReplacingAKeyboardBindingNeverRunsTwoGlobalHooks()
    {
        var settings = new PttSettingsViewModel(
            KeyboardPttKey.F8,
            KeyboardPttKey.None,
            togglePttMode: false,
            serialPttEnabled: false,
            serialPttActiveSystemOnly: false,
            serialPttPortName: string.Empty,
            serialPttBaudRate: 9_600);
        int activeHooks = 0;
        int maximumActiveHooks = 0;
        var controller = new PttSessionController(
            settings,
            (_, _) => new DelegateSerialPttInputSourceFactory(() => new TestPttSource([])),
            () => PttTargetScope.AllSelectedResources,
            (key, toggleMode) => new KeyboardPttBinding(
                key,
                toggleMode,
                _ => new CountingGlobalKeyboardBindingSource(
                    () => maximumActiveHooks = Math.Max(maximumActiveHooks, ++activeHooks),
                    () => activeHooks--)));
        await controller.StartAsync();

        await controller.ReplaceKeyboardBindingAsync(
            PttTargetScope.AllSelectedResources,
            KeyboardPttKey.F10,
            () => Task.CompletedTask);

        Assert.Equal(1, activeHooks);
        Assert.Equal(1, maximumActiveHooks);
        await controller.DisposeAsync();
        Assert.Equal(0, activeHooks);
    }

    [Fact]
    public async Task FailedKeyboardReplacementRestartsThePreviousBinding()
    {
        var settings = new PttSettingsViewModel(
            KeyboardPttKey.F8,
            KeyboardPttKey.None,
            togglePttMode: false,
            serialPttEnabled: false,
            serialPttActiveSystemOnly: false,
            serialPttPortName: string.Empty,
            serialPttBaudRate: 9_600);
        var operations = new List<string>();
        var controller = new PttSessionController(
            settings,
            (_, _) => new DelegateSerialPttInputSourceFactory(() => new TestPttSource([])),
            () => PttTargetScope.AllSelectedResources,
            (key, toggleMode) => new KeyboardPttBinding(
                key,
                toggleMode,
                _ => new TestGlobalKeyboardBindingSource(
                    key.ToString(),
                    operations,
                    failStart: key == KeyboardPttKey.F10)));
        await controller.StartAsync();

        await Assert.ThrowsAsync<IOException>(() => controller.ReplaceKeyboardBindingAsync(
            PttTargetScope.AllSelectedResources,
            KeyboardPttKey.F10,
            () => Task.CompletedTask));

        Assert.Equal(KeyboardPttKey.F8, controller.GlobalKey);
        Assert.Equal(["F8:start", "F8:stop", "F10:start", "F8:start"], operations);
        await controller.DisposeAsync();
    }

    private static PttSettingsViewModel CreateSettings()
        => new(
            KeyboardPttKey.None,
            KeyboardPttKey.None,
            togglePttMode: false,
            serialPttEnabled: true,
            serialPttActiveSystemOnly: false,
            serialPttPortName: "initial",
            serialPttBaudRate: 9_600);

    private sealed class TestPttSource(
        List<string> operations,
        bool failStart = false,
        int stopFailures = 0) : IPttSource
    {
        private int remainingStopFailures = stopFailures;
        public event EventHandler<Exception>? CaptureFailed { add { } remove { } }
        public event EventHandler<bool>? StateChanged;

        public bool IsPressed { get; private set; }

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operations.Add("start");
            if (failStart)
                throw new IOException("serial start failed");
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operations.Add("stop");
            if (remainingStopFailures > 0)
            {
                remainingStopFailures--;
                throw new IOException("serial stop failed");
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            operations.Add("dispose");
            return ValueTask.CompletedTask;
        }

        public void Raise(bool pressed)
        {
            IsPressed = pressed;
            StateChanged?.Invoke(this, pressed);
        }
    }

    private sealed class TestGlobalKeyboardBindingSource : IGlobalKeyboardPttBindingSource
    {
        private readonly string? name;
        private readonly List<string>? operations;
        private readonly bool failStart;

        public TestGlobalKeyboardBindingSource()
        {
        }

        public TestGlobalKeyboardBindingSource(
            string name,
            List<string> operations,
            bool failStart)
        {
            this.name = name;
            this.operations = operations;
            this.failStart = failStart;
        }

        public event EventHandler<Exception>? CaptureFailed { add { } remove { } }
        public event EventHandler<bool>? StateChanged;
        public bool IsPressed { get; private set; }
        public bool ToggleMode { get; set; }
        public bool InputSuppressed { get; set; }

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (name is not null)
                operations!.Add($"{name}:start");
            if (failStart)
                throw new IOException("global hook start failed");
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (name is not null)
                operations!.Add($"{name}:stop");
            return ValueTask.CompletedTask;
        }

        public void ReleaseToggleLatch()
            => Raise(pressed: false);

        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;

        public void Raise(bool pressed)
        {
            if (IsPressed == pressed)
                return;
            IsPressed = pressed;
            StateChanged?.Invoke(this, pressed);
        }
    }

    private sealed class FailingGlobalKeyboardBindingSource(Exception failure)
        : IGlobalKeyboardPttBindingSource
    {
        public event EventHandler<Exception>? CaptureFailed { add { } remove { } }
        public event EventHandler<bool>? StateChanged
        {
            add { }
            remove { }
        }

        public bool IsPressed => false;
        public bool ToggleMode { get; set; }
        public bool InputSuppressed { get; set; }
        public bool Disposed { get; private set; }

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException(failure);

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public void ReleaseToggleLatch()
        {
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingGlobalKeyboardBindingSource(
        Action started,
        Action stopped) : IGlobalKeyboardPttBindingSource
    {
        private bool active;

        public event EventHandler<Exception>? CaptureFailed { add { } remove { } }
        public event EventHandler<bool>? StateChanged
        {
            add { }
            remove { }
        }
        public bool IsPressed => false;
        public bool ToggleMode { get; set; }
        public bool InputSuppressed { get; set; }

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!active)
            {
                active = true;
                started();
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Stop();
            return ValueTask.CompletedTask;
        }

        public void ReleaseToggleLatch()
        {
        }

        public ValueTask DisposeAsync()
        {
            Stop();
            return ValueTask.CompletedTask;
        }

        private void Stop()
        {
            if (!active)
                return;
            active = false;
            stopped();
        }
    }

}
