using DvmConsole.Audio;
using DvmConsole.Media;
using Xunit;

namespace DvmConsole.Media.Tests;

public sealed class AudioMixerTests
{
    [Fact]
    public async Task MixesActiveChannelsWithSaturation()
    {
        var output = new FakePlayback();
        await using var mixer = new AudioMixer(output);
        await using IAudioPlayback first = mixer.OpenChannel();
        await using IAudioPlayback second = mixer.OpenChannel();

        await first.WriteAsync(CreateSamples(30_000));
        await second.WriteAsync(CreateSamples(10_000));
        await WaitForAsync(() => output.Frames.Count > 0);

        Assert.Equal(160, output.Frames[0].Length);
        Assert.Equal(short.MaxValue, output.Frames[0][0]);
        Assert.Equal(short.MaxValue, output.Frames[0][159]);
    }

    [Fact]
    public async Task RemovingAChannelLeavesTheOtherChannelAudible()
    {
        var output = new FakePlayback();
        await using var mixer = new AudioMixer(output);
        await using IAudioPlayback first = mixer.OpenChannel();
        IAudioPlayback second = mixer.OpenChannel();

        await first.WriteAsync(CreateSamples(100));
        await second.WriteAsync(CreateSamples(200));
        await WaitForAsync(() => output.Frames.Count > 0);
        await second.DisposeAsync();

        int priorFrameCount = output.Frames.Count;
        await first.WriteAsync(CreateSamples(300));
        await WaitForAsync(() => output.Frames.Count > priorFrameCount);

        Assert.Equal((short)300, output.Frames[^1][0]);
    }

    [Fact]
    public async Task AppliesIndependentChannelGainBeforeSaturation()
    {
        var output = new FakePlayback();
        await using var mixer = new AudioMixer(output);
        await using IAudioPlayback channel = mixer.OpenChannel();

        Assert.IsAssignableFrom<IAudioGainControl>(channel);
        ((IAudioGainControl)channel).Gain = 0.5;
        await channel.WriteAsync(CreateSamples(20_000));
        await WaitForAsync(() => output.Frames.Count > 0);

        Assert.Equal((short)10_000, output.Frames[0][0]);
    }

    private static short[] CreateSamples(short value)
    {
        var samples = new short[160];
        Array.Fill(samples, value);
        return samples;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(5);

        Assert.True(condition());
    }

    private sealed class FakePlayback : IAudioPlayback
    {
        public List<short[]> Frames { get; } = [];
        public PcmAudioFormat Format { get; } = PcmAudioFormat.Voice8KhzMono16Bit;

        public ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Frames.Add(samples.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
