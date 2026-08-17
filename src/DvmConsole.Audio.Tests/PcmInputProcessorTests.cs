using DvmConsole.Audio;
using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class PcmInputProcessorTests
{
    [Fact]
    public void AppliesConfiguredGainAndKeepsSamplesBounded()
    {
        var processor = new PcmInputProcessor(new AudioInputProcessingOptions
        {
            Gain = 2,
            LowGainDb = 0,
            MidGainDb = 0,
            HighGainDb = 0
        });
        short[] output = new short[320];

        processor.Process(Enumerable.Repeat((short)6000, output.Length).ToArray(), output);

        Assert.Contains(output, sample => sample != 0);
        Assert.All(output, sample => Assert.InRange(sample, short.MinValue, short.MaxValue));
        Assert.True(output[^1] > 6000);
    }

    [Fact]
    public void OptionalAgcRaisesQuietInputWithoutChangingSilence()
    {
        var processor = new PcmInputProcessor(new AudioInputProcessingOptions { AgcEnabled = true });
        short[] quietInput = Enumerable.Repeat((short)100, 320).ToArray();
        short[] quietOutput = new short[quietInput.Length];
        short[] silenceOutput = new short[quietInput.Length];

        processor.Process(quietInput, quietOutput);
        processor.Process(new short[quietInput.Length], silenceOutput);

        Assert.Contains(quietOutput, sample => Math.Abs(sample) > 100);
        Assert.All(silenceOutput, sample => Assert.Equal((short)0, sample));
    }

    [Fact]
    public void OptionalAgcTargetsP25NominalActiveSpeechLevel()
    {
        var processor = new PcmInputProcessor(new AudioInputProcessingOptions { AgcEnabled = true, Gain = 3 });
        short[] input = Enumerable.Repeat((short)1_000, 320).ToArray();
        short[] output = new short[input.Length];

        for (int block = 0; block < 64; block++)
            processor.Process(input, output);

        double rms = Math.Sqrt(output.Select(sample => Math.Pow(sample / (double)short.MaxValue, 2)).Average());
        Assert.InRange(rms, 0.054, 0.058);
    }

    [Fact]
    public void OptionalAgcUsesConfiguredTargetLevel()
    {
        var processor = new PcmInputProcessor(new AudioInputProcessingOptions
        {
            AgcEnabled = true,
            AgcTargetDbfs = -30
        });
        short[] input = Enumerable.Repeat((short)500, 320).ToArray();
        short[] output = new short[input.Length];

        for (int block = 0; block < 64; block++)
            processor.Process(input, output);

        double rms = Math.Sqrt(output.Select(sample => Math.Pow(sample / (double)short.MaxValue, 2)).Average());
        Assert.InRange(rms, 0.030, 0.033);
    }

    [Fact]
    public void NormalizesDeviceAndProcessingBounds()
    {
        AudioInputProcessingOptions normalized = new AudioInputProcessingOptions
        {
            DeviceId = "  microphone-1 ",
            AgcTargetDbfs = -100,
            Gain = 100,
            LowGainDb = -100,
            MidGainDb = double.NaN,
            HighGainDb = 100
        }.Normalize();

        Assert.Equal("microphone-1", normalized.DeviceId);
        Assert.Equal(-40, normalized.AgcTargetDbfs);
        Assert.Equal(3, normalized.Gain);
        Assert.Equal(-12, normalized.LowGainDb);
        Assert.Equal(0, normalized.MidGainDb);
        Assert.Equal(12, normalized.HighGainDb);
    }

    [Fact]
    public async Task AppleModeDoesNotRunDvmConsoleGainOrAgcASecondTime()
    {
        var source = new TestCapture();
        await using var capture = new ProcessedAudioCapture(source, new AudioInputProcessingOptions
        {
            ProcessingMode = AudioProcessingMode.AppleVoiceProcessing,
            AgcEnabled = true,
            Gain = 3,
            LowGainDb = 12,
            MidGainDb = 12,
            HighGainDb = 12
        });
        short[]? received = null;
        capture.SamplesAvailable += (_, args) => received = args.Samples.ToArray();

        short[] appleProcessed = [100, -200, 300];
        source.Emit(appleProcessed);

        Assert.Equal(appleProcessed, received);
    }

    private sealed class TestCapture : IAudioCapture
    {
        public event EventHandler<PcmSamplesEventArgs>? SamplesAvailable;
        public PcmAudioFormat Format => PcmAudioFormat.Voice8KhzMono16Bit;
        public bool IsRunning { get; private set; }
        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            return ValueTask.CompletedTask;
        }
        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = false;
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Emit(short[] samples)
            => SamplesAvailable?.Invoke(this, new PcmSamplesEventArgs(samples));
    }
}
