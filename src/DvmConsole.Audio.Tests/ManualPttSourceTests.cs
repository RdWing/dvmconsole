using DvmConsole.Audio;
using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class ManualPttSourceTests
{
    [Fact]
    public async Task PublishesOnlyStateTransitions()
    {
        await using var ptt = new ManualPttSource();
        var states = new List<bool>();
        ptt.StateChanged += (_, pressed) => states.Add(pressed);

        await ptt.StartAsync();
        ptt.SetPressed(true);
        ptt.SetPressed(true);
        ptt.SetPressed(false);
        await ptt.StopAsync();

        Assert.Equal(new[] { true, false }, states);
        Assert.False(ptt.IsPressed);
    }

    [Fact]
    public void RejectsStateChangesBeforeStart()
    {
        var ptt = new ManualPttSource();

        Assert.Throws<InvalidOperationException>(() => ptt.SetPressed(true));
    }
}
