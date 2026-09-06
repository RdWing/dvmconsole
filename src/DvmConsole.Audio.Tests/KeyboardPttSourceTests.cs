// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;
using DvmConsole.Ptt;
using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class KeyboardPttSourceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureFailureReleasesAndFencesBothPttModes(bool toggle)
    {
        var capture = new FakeGlobalKeyboardCapture();
        await using var source = new GlobalKeyboardPttSource(KeyboardPttKey.Space, () => capture)
        { ToggleMode = toggle };
        Exception? reported = null;
        source.CaptureFailed += failure => reported = failure;
        await source.StartAsync();
        capture.Emit(KeyboardPttKey.Space, true);
        Assert.True(source.IsPressed);
        capture.Fail(new IOException("Capture disappeared"));
        Assert.False(source.IsPressed);
        Assert.NotNull(reported);
        capture.Emit(KeyboardPttKey.Space, false);
        capture.Emit(KeyboardPttKey.Space, true);
        Assert.False(source.IsPressed);
        await source.StopAsync();
        await source.StartAsync();
        capture.Emit(KeyboardPttKey.Space, true);
        Assert.True(source.IsPressed);
    }

    [Fact]
    public void WindowsHookInteropUsesExplicitUnicodeEntryPoints()
    {
        var expectedEntryPoints = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SetWindowsHookEx"] = "SetWindowsHookExW",
            ["GetModuleHandle"] = "GetModuleHandleW",
            ["PostThreadMessage"] = "PostThreadMessageW",
            ["PeekMessage"] = "PeekMessageW",
            ["GetMessage"] = "GetMessageW"
        };

        foreach ((string methodName, string expectedEntryPoint) in expectedEntryPoints)
        {
            MethodInfo method = typeof(WindowsGlobalKeyboardCapture).GetMethod(
                methodName,
                BindingFlags.NonPublic | BindingFlags.Static)!;
            LibraryImportAttribute import = method.GetCustomAttribute<LibraryImportAttribute>()!;

            Assert.NotNull(import);
            Assert.Equal(expectedEntryPoint, import.EntryPoint);
        }
    }

    [Theory]
    [InlineData(null, null, false, true)]
    [InlineData("org.example.Flatpak", null, false, false)]
    [InlineData(null, "/snap/dvmconsole/current", false, false)]
    [InlineData(null, null, true, false)]
    public void WaylandHostRegistrationExcludesSandboxedApplications(
        string? flatpakId,
        string? snapPath,
        bool flatpakInfoExists,
        bool expected)
        => Assert.Equal(
            expected,
            LinuxPortalGlobalKeyboardCapture.ShouldRegisterHostApplication(
                flatpakId,
                snapPath,
                flatpakInfoExists));

    [Fact]
    public async Task GlobalAdapterRelaysNativeKeyTransitionsAndStopsCapture()
    {
        var capture = new FakeGlobalKeyboardCapture();
        await using var ptt = new GlobalKeyboardPttSource(
            KeyboardPttKey.F12,
            () => capture);
        var states = new List<bool>();
        ptt.StateChanged += (_, pressed) => states.Add(pressed);

        await ptt.StartAsync();
        capture.Emit(KeyboardPttKey.F11, true);
        capture.Emit(KeyboardPttKey.F12, true);
        capture.Emit(KeyboardPttKey.F12, true);
        capture.Emit(KeyboardPttKey.F12, false);
        await ptt.StopAsync();

        Assert.True(capture.Started);
        Assert.True(capture.Stopped);
        Assert.Equal(new[] { true, false }, states);
        Assert.False(ptt.IsPressed);
    }

    [Fact]
    public async Task GlobalAdapterSupportsToggleMode()
    {
        var capture = new FakeGlobalKeyboardCapture();
        await using var ptt = new GlobalKeyboardPttSource(
            KeyboardPttKey.Space,
            () => capture)
        {
            ToggleMode = true
        };
        var states = new List<bool>();
        ptt.StateChanged += (_, pressed) => states.Add(pressed);

        await ptt.StartAsync();
        capture.Emit(KeyboardPttKey.Space, true);
        capture.Emit(KeyboardPttKey.Space, true);
        capture.Emit(KeyboardPttKey.Space, false);
        capture.Emit(KeyboardPttKey.Space, true);

        Assert.False(ptt.IsPressed);
        Assert.Equal(new[] { true, false }, states);
    }

    [Fact]
    public async Task GlobalAdapterDoesNotSynchronouslyBlockItsCallerOnNativeReadiness()
    {
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var capture = new BlockingGlobalKeyboardCapture(entered, release);
        await using var ptt = new GlobalKeyboardPttSource(
            KeyboardPttKey.F12,
            () => capture);
        var returned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task caller = Task.Run(() => returned.SetResult(ptt.StartAsync().AsTask()));
        try
        {
            Task operation = await returned.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            Assert.False(operation.IsCompleted);
            release.Set();
            await operation.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            release.Set();
            await caller.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task GlobalAdapterDoesNotSynchronouslyBlockItsCallerOnNativeTeardown()
    {
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var capture = new BlockingStopGlobalKeyboardCapture(entered, release);
        await using var ptt = new GlobalKeyboardPttSource(KeyboardPttKey.F12, () => capture);
        await ptt.StartAsync();
        var returned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task caller = Task.Run(() => returned.SetResult(ptt.StopAsync().AsTask()));
        try
        {
            Task operation = await returned.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            Assert.False(operation.IsCompleted);
            release.Set();
            await operation.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            release.Set();
            await caller.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Theory]
    [InlineData(0x20, KeyboardPttKey.Space)]
    [InlineData(0x70, KeyboardPttKey.F1)]
    [InlineData(0x7B, KeyboardPttKey.F12)]
    [InlineData(0x7C, KeyboardPttKey.F13)]
    [InlineData(0x82, KeyboardPttKey.F19)]
    public void MapsWindowsVirtualKeys(uint virtualKey, KeyboardPttKey expected)
    {
        Assert.True(KeyboardPttKeyMapping.TryFromWindowsVirtualKey(virtualKey, out KeyboardPttKey actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(105, KeyboardPttKey.F13)]
    [InlineData(80, KeyboardPttKey.F19)]
    public void MapsMacFunctionKeys(long keyCode, KeyboardPttKey expected)
    {
        Assert.True(KeyboardPttKeyMapping.TryFromMacKeyCode(keyCode, out KeyboardPttKey actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0x20, KeyboardPttKey.Space)]
    [InlineData(0xFFBE, KeyboardPttKey.F1)]
    [InlineData(0xFFC9, KeyboardPttKey.F12)]
    [InlineData(0xFFD0, KeyboardPttKey.F19)]
    public void MapsX11KeySymbols(ulong keySym, KeyboardPttKey expected)
    {
        Assert.True(KeyboardPttKeyMapping.TryFromX11KeySym((nuint)keySym, out KeyboardPttKey actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RejectsUnmappedX11KeySymbols()
    {
        Assert.False(KeyboardPttKeyMapping.TryFromX11KeySym(0x61, out _));
    }

    [Theory]
    [InlineData(KeyboardPttKey.Space, "space")]
    [InlineData(KeyboardPttKey.F1, "F1")]
    [InlineData(KeyboardPttKey.F12, "F12")]
    [InlineData(KeyboardPttKey.F19, "F19")]
    public void MapsPttKeysToWaylandPortalShortcutTriggers(
        KeyboardPttKey key,
        string expected)
    {
        Assert.Equal(expected, LinuxPortalGlobalKeyboardCapture.ToShortcutTrigger(key));
    }

    [Fact]
    public async Task NoneDisablesKeyboardActivation()
    {
        await using var ptt = new KeyboardPttSource(KeyboardPttKey.None);
        await ptt.StartAsync();

        Assert.False(ptt.HandleKeyDown(KeyboardPttKey.Space));
        Assert.False(ptt.HandleKeyDown(KeyboardPttKey.F19));
        Assert.False(ptt.IsPressed);
    }

    [Fact]
    public async Task PublishesOnlyMatchingKeyTransitions()
    {
        await using var ptt = new KeyboardPttSource(KeyboardPttKey.F12);
        var states = new List<bool>();
        ptt.StateChanged += (_, pressed) => states.Add(pressed);

        await ptt.StartAsync();
        Assert.False(ptt.HandleKeyDown(KeyboardPttKey.F11));
        Assert.True(ptt.HandleKeyDown(KeyboardPttKey.F12));
        Assert.True(ptt.HandleKeyDown(KeyboardPttKey.F12));
        Assert.True(ptt.HandleKeyUp(KeyboardPttKey.F12));
        Assert.True(ptt.HandleKeyUp(KeyboardPttKey.F12));

        Assert.Equal(new[] { true, false }, states);
        Assert.False(ptt.IsPressed);
    }

    [Fact]
    public async Task StopsAndReleasesPressedKey()
    {
        await using var ptt = new KeyboardPttSource();
        var states = new List<bool>();
        ptt.StateChanged += (_, pressed) => states.Add(pressed);

        await ptt.StartAsync();
        Assert.True(ptt.HandleKeyDown(KeyboardPttKey.Space));
        await ptt.StopAsync();

        Assert.Equal(new[] { true, false }, states);
        Assert.False(ptt.IsPressed);
        Assert.False(ptt.HandleKeyUp(KeyboardPttKey.Space));
    }

    [Fact]
    public async Task ToggleModeIgnoresKeyRepeatsAndChangesStateOnNextPress()
    {
        await using var ptt = new KeyboardPttSource { ToggleMode = true };
        var states = new List<bool>();
        ptt.StateChanged += (_, pressed) => states.Add(pressed);

        await ptt.StartAsync();
        Assert.True(ptt.HandleKeyDown(KeyboardPttKey.Space));
        Assert.True(ptt.HandleKeyDown(KeyboardPttKey.Space));
        Assert.True(ptt.IsPressed);
        Assert.True(ptt.HandleKeyUp(KeyboardPttKey.Space));
        Assert.True(ptt.IsPressed);
        Assert.True(ptt.HandleKeyDown(KeyboardPttKey.Space));
        Assert.False(ptt.IsPressed);

        Assert.Equal(new[] { true, false }, states);
    }

    [Fact]
    public async Task ReleasingToggleLatchIgnoresRepeatsUntilPhysicalKeyUp()
    {
        await using var ptt = new KeyboardPttSource { ToggleMode = true };
        var states = new List<bool>();
        ptt.StateChanged += (_, pressed) => states.Add(pressed);

        await ptt.StartAsync();
        Assert.True(ptt.HandleKeyDown(KeyboardPttKey.Space));
        ptt.ReleaseToggleLatch();
        Assert.True(ptt.HandleKeyDown(KeyboardPttKey.Space));

        Assert.False(ptt.IsPressed);
        Assert.True(ptt.HandleKeyUp(KeyboardPttKey.Space));
        Assert.True(ptt.HandleKeyDown(KeyboardPttKey.Space));

        Assert.True(ptt.IsPressed);
        Assert.Equal(new[] { true, false, true }, states);
    }

    [Fact]
    public async Task InputSuppressionReleasesHoldModeAndIgnoresSpaceUntilCleared()
    {
        await using var ptt = new KeyboardPttSource(KeyboardPttKey.Space);
        var states = new List<bool>();
        ptt.StateChanged += (_, pressed) => states.Add(pressed);
        await ptt.StartAsync();

        Assert.True(ptt.HandleKeyDown(KeyboardPttKey.Space));
        ptt.InputSuppressed = true;
        Assert.False(ptt.HandleKeyDown(KeyboardPttKey.Space));
        Assert.False(ptt.HandleKeyUp(KeyboardPttKey.Space));
        ptt.InputSuppressed = false;
        Assert.True(ptt.HandleKeyDown(KeyboardPttKey.Space));
        Assert.True(ptt.HandleKeyUp(KeyboardPttKey.Space));

        Assert.Equal(new[] { true, false, true, false }, states);
    }

    [Fact]
    public async Task InputSuppressionSafelyClearsLatchedToggleState()
    {
        await using var ptt = new KeyboardPttSource(KeyboardPttKey.Space)
        {
            ToggleMode = true
        };
        await ptt.StartAsync();

        Assert.True(ptt.HandleKeyDown(KeyboardPttKey.Space));
        Assert.True(ptt.IsPressed);
        ptt.InputSuppressed = true;
        Assert.False(ptt.IsPressed);
        ptt.InputSuppressed = false;
        Assert.True(ptt.HandleKeyDown(KeyboardPttKey.Space));
        Assert.True(ptt.IsPressed);
    }

    private sealed class FakeGlobalKeyboardCapture : IGlobalKeyboardCapture
    {
        public event Action<KeyboardPttKey, bool>? KeyChanged;
        public event Action<Exception>? Terminated;
        public void Fail(Exception exception) => Terminated?.Invoke(exception);
        public bool Started { get; private set; }
        public bool Stopped { get; private set; }

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Started = true;
            return ValueTask.CompletedTask;
        }

        public void Stop() => Stopped = true;

        public void Dispose() => Stopped = true;

        public void Emit(KeyboardPttKey key, bool isDown)
            => KeyChanged?.Invoke(key, isDown);
    }

    private sealed class BlockingGlobalKeyboardCapture(
        ManualResetEventSlim entered,
        ManualResetEventSlim release) : IGlobalKeyboardCapture
    {
        public event Action<Exception>? Terminated { add { } remove { } }
        public event Action<KeyboardPttKey, bool>? KeyChanged
        {
            add { }
            remove { }
        }

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            entered.Set();
            release.Wait(cancellationToken);
            return ValueTask.CompletedTask;
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class BlockingStopGlobalKeyboardCapture(
        ManualResetEventSlim entered,
        ManualResetEventSlim release) : IGlobalKeyboardCapture
    {
        public event Action<Exception>? Terminated { add { } remove { } }
        public event Action<KeyboardPttKey, bool>? KeyChanged
        {
            add { }
            remove { }
        }

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public void Stop()
        {
            entered.Set();
            release.Wait();
        }

        public void Dispose()
        {
        }
    }
}
