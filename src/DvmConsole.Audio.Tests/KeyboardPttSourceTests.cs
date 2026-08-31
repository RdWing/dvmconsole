using DvmConsole.Audio;
using DvmConsole.Ptt;
using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class KeyboardPttSourceTests
{
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
        public bool Started { get; private set; }
        public bool Stopped { get; private set; }

        public void Start() => Started = true;

        public void Stop() => Stopped = true;

        public void Dispose() => Stopped = true;

        public void Emit(KeyboardPttKey key, bool isDown)
            => KeyChanged?.Invoke(key, isDown);
    }
}
