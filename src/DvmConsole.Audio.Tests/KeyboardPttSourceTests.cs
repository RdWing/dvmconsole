using DvmConsole.Audio;
using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class KeyboardPttSourceTests
{
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
}
