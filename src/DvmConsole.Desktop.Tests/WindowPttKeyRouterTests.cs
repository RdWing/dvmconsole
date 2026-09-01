using Avalonia.Controls;
using Avalonia.Input;
using DvmConsole.Audio;
using DvmConsole.Desktop;
using DvmConsole.Ptt;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class WindowPttKeyRouterTests
{
    [Theory]
    [InlineData(Key.Space, KeyboardPttKey.Space)]
    [InlineData(Key.F1, KeyboardPttKey.F1)]
    [InlineData(Key.F19, KeyboardPttKey.F19)]
    public void MapsSupportedWindowKeys(Key key, KeyboardPttKey expected)
    {
        Assert.True(WindowPttKeyRouter.TryMap(key, out KeyboardPttKey actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RejectsUnrelatedWindowKeys()
        => Assert.False(WindowPttKeyRouter.TryMap(Key.A, out _));

    [Fact]
    public void SuppressesSpacePttInsideNestedEditableControl()
    {
        var editor = new TextBox();
        var panel = new Border { Child = editor };

        Assert.True(WindowPttInputGuard.ShouldSuppressSpacePtt(editor));
        Assert.False(WindowPttInputGuard.ShouldSuppressSpacePtt(panel));
    }

    [Fact]
    public void LosingWindowActivationClearsFocusSuppressionForGlobalPtt()
    {
        var editor = new TextBox();

        Assert.True(WindowPttInputGuard.ShouldSuppressSpacePtt(editor, isWindowActive: true));
        Assert.False(WindowPttInputGuard.ShouldSuppressSpacePtt(editor, isWindowActive: false));
    }

    [Fact]
    public void SuppressesSpacePttForInteractiveControlsButNotChannelSurface()
    {
        Assert.True(WindowPttInputGuard.ShouldSuppressSpacePtt(new Button()));
        Assert.True(WindowPttInputGuard.ShouldSuppressSpacePtt(new Slider()));
        Assert.False(WindowPttInputGuard.ShouldSuppressSpacePtt(new Button { Classes = { "ptt" } }));
        Assert.False(WindowPttInputGuard.ShouldSuppressSpacePtt(new ListBox { Classes = { "channel-list" } }));
        Assert.False(WindowPttInputGuard.ShouldSuppressSpacePtt(new Border { Focusable = true }));
    }

    [Theory]
    [InlineData("channel-list")]
    [InlineData("channel-card")]
    public void AllowsSpacePttAfterFocusingChannelControls(string surfaceClass)
    {
        var focusedButton = new Button();
        var surface = new Border
        {
            Classes = { surfaceClass },
            Child = focusedButton
        };

        Assert.False(WindowPttInputGuard.ShouldSuppressSpacePtt(focusedButton));
        GC.KeepAlive(surface);
    }
}
