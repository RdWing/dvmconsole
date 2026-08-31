using System.Text;
using DvmConsole.Audio;
using DvmConsole.Ptt;
using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class SerialPttSourceTests
{
    [Fact]
    public async Task ParsesSerialLinesAndPublishesTransitions()
    {
        var states = new List<bool>();
        await using var ptt = new SerialPttSource(() => new MemoryStream(
            Encoding.ASCII.GetBytes("junk\nPTT=on\nPTT=on\npressed\nPTT=off\n")));
        ptt.StateChanged += (_, pressed) => states.Add(pressed);

        await ptt.StartAsync();
        await WaitForAsync(() => states.SequenceEqual(new[] { true, false }));

        Assert.False(ptt.IsPressed);
    }

    [Fact]
    public async Task ReleasesPttWhenDeviceReachesEndOfStream()
    {
        var states = new List<bool>();
        await using var ptt = new SerialPttSource(() => new MemoryStream(
            Encoding.ASCII.GetBytes("pressed\n")));
        ptt.StateChanged += (_, pressed) => states.Add(pressed);

        await ptt.StartAsync();
        await WaitForAsync(() => states.SequenceEqual(new[] { true, false }));

        Assert.False(ptt.IsPressed);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData(" true ", true)]
    [InlineData("pressed", true)]
    [InlineData("PTT=on", true)]
    [InlineData("0", false)]
    [InlineData("released", false)]
    [InlineData("PTT=off", false)]
    public void ParsesSupportedStateTokens(string line, bool expected)
    {
        Assert.True(SerialPttSource.TryParseState(line, out bool pressed));
        Assert.Equal(expected, pressed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("maybe")]
    [InlineData("STATE=on")]
    public void IgnoresUnknownStateTokens(string line)
    {
        Assert.False(SerialPttSource.TryParseState(line, out _));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(5);

        Assert.True(condition());
    }
}
