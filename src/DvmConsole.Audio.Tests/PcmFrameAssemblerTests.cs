using DvmConsole.Audio;
using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class PcmFrameAssemblerTests
{
    [Fact]
    public void AssemblesFramesAcrossArbitraryCallbackSizes()
    {
        var assembler = new PcmFrameAssembler();
        var frames = new List<short[]>();

        Assert.Equal(0, assembler.Append(CreateSamples(0, 100), samples => frames.Add(samples.ToArray())));
        Assert.Equal(0, assembler.Append(CreateSamples(100, 50), samples => frames.Add(samples.ToArray())));
        Assert.Equal(1, assembler.Append(CreateSamples(150, 90), samples => frames.Add(samples.ToArray())));

        Assert.Single(frames);
        Assert.Equal(CreateSamples(0, 160), frames[0]);
        Assert.Equal(80, assembler.BufferedSamples);
    }

    [Fact]
    public void ProducesMultipleFramesAndRetainsRemainder()
    {
        var assembler = new PcmFrameAssembler(frameSize: 4);
        var frames = new List<short[]>();

        Assert.Equal(2, assembler.Append(new short[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, samples => frames.Add(samples.ToArray())));

        Assert.Equal(new short[] { 1, 2, 3, 4 }, frames[0]);
        Assert.Equal(new short[] { 5, 6, 7, 8 }, frames[1]);
        Assert.Equal(1, assembler.BufferedSamples);
    }

    [Fact]
    public void ResetDiscardsPartialFrame()
    {
        var assembler = new PcmFrameAssembler(frameSize: 4);
        assembler.Append(new short[] { 1, 2 }, _ => { });

        assembler.Reset();

        Assert.Equal(0, assembler.BufferedSamples);
        Assert.Equal(1, assembler.Append(new short[] { 3, 4, 5, 6 }, _ => { }));
    }

    [Fact]
    public void FailedFrameHandoffIsNotRetriedAsAnEndOfStreamTail()
    {
        var assembler = new PcmFrameAssembler(frameSize: 4);

        Assert.Throws<IOException>(() => assembler.Append(
            new short[] { 1, 2, 3, 4 },
            _ => throw new IOException("send failed")));

        Assert.Equal(0, assembler.BufferedSamples);
        Assert.False(assembler.FlushPadded(_ => throw new InvalidOperationException()));

        assembler.Append(new short[] { 5, 6 }, _ => { });
        Assert.Throws<IOException>(() => assembler.FlushPadded(
            _ => throw new IOException("tail send failed")));
        Assert.Equal(0, assembler.BufferedSamples);
    }

    private static short[] CreateSamples(int start, int count)
    {
        return Enumerable.Range(start, count).Select(value => (short)value).ToArray();
    }
}
