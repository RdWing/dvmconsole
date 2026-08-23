using DvmConsole.Audio;
using NAudio.Wave;
using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class WindowsPlaybackObserverTests
{
    [Fact]
    public void ReadReportsHeartbeatAndCommitsOnlyResumedStarvation()
    {
        PcmAudioFormat format = PcmAudioFormat.Voice8KhzMono16Bit;
        var provider = new BufferedWaveProvider(
            new WaveFormat(format.SampleRate, format.BitsPerSample, format.Channels));
        var observer = new WindowsPlaybackObserver(provider, format);
        byte[] output = new byte[320];

        Assert.Equal(output.Length, observer.Read(output));
        Assert.Equal(1, observer.OutputCallbackCount);
        Assert.Equal(TimeSpan.Zero, observer.PendingStarvedDuration);

        observer.ResumePlaybackContinuity();
        Assert.Equal(output.Length, observer.Read(output));
        Assert.Equal(TimeSpan.FromMilliseconds(20), observer.PendingStarvedDuration);
        Assert.Equal(TimeSpan.Zero, observer.StarvedDuration);

        provider.AddSamples(new byte[output.Length], 0, output.Length);
        observer.ResumePlaybackContinuity();

        Assert.Equal(TimeSpan.Zero, observer.PendingStarvedDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(20), observer.StarvedDuration);
    }

    [Fact]
    public void EndExpectedPlaybackDiscardsAnOrdinaryEmptyTail()
    {
        PcmAudioFormat format = PcmAudioFormat.Voice8KhzMono16Bit;
        var provider = new BufferedWaveProvider(
            new WaveFormat(format.SampleRate, format.BitsPerSample, format.Channels));
        var observer = new WindowsPlaybackObserver(provider, format);
        byte[] output = new byte[320];

        observer.ResumePlaybackContinuity();
        observer.Read(output);
        observer.EndExpectedPlayback();
        observer.Read(output);

        Assert.Equal(TimeSpan.Zero, observer.PendingStarvedDuration);
        Assert.Equal(TimeSpan.Zero, observer.StarvedDuration);
    }
}
