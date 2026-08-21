using DvmConsole.Audio;
using DvmConsole.Media;
using System.Collections.Concurrent;
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

    [Fact]
    public async Task BoundsSustainedBacklogAndRetainsNewestSamples()
    {
        var output = new FakePlayback();
        await using var mixer = new AudioMixer(output);
        await using IAudioPlayback channel = mixer.OpenChannel();
        short[] samples = Enumerable.Range(0, mixer.MaximumBufferedSamples + 160)
            .Select(value => (short)(value % short.MaxValue))
            .ToArray();

        await channel.WriteAsync(samples);
        await WaitForAsync(() => mixer.DroppedSamples > 0);
        await WaitForAsync(() => output.Frames.Count > 0);

        Assert.True(mixer.DroppedSamples > 0);
        Assert.True(mixer.DroppedSamples <= samples.Length);
        Assert.Equal(12 * 160, mixer.DroppedSamples);
        Assert.Equal((short)(12 * 160), output.Frames[0][0]);
    }

    [Fact]
    public async Task BoundsLiveConcealmentAndRetainsItsNewestFrames()
    {
        var output = new FakePlayback();
        await using var mixer = new AudioMixer(output);
        await using IAudioPlayback channel = mixer.OpenChannel();
        var concealmentPlayback = Assert.IsAssignableFrom<IConcealmentAudioPlayback>(channel);
        short[] concealedSamples = Enumerable.Range(1, 90)
            .SelectMany(frame => Enumerable.Repeat((short)frame, 160))
            .ToArray();

        await concealmentPlayback.WriteConcealmentAsync(concealedSamples);
        await WaitForAsync(() => output.Frames.Count > 0);

        AudioMixerDiagnostics diagnostics = mixer.GetDiagnostics();
        Assert.Equal(81 * 160, diagnostics.SuppressedLiveConcealmentSamples);
        Assert.Equal(0, diagnostics.DroppedSamples);
        Assert.Equal(0, diagnostics.OverflowResynchronizations);
        Assert.Equal((short)82, output.Frames[0][0]);
    }

    [Fact]
    public async Task ReportsTheLaneResponsibleForTheLatestOverflow()
    {
        var output = new FakePlayback();
        await using var mixer = new AudioMixer(output);
        await using IAudioPlayback channel = mixer.OpenChannel("East Bay/Oakland Fire DSP");
        short[] samples = Enumerable.Repeat(
            (short)500,
            mixer.MaximumBufferedSamples + 160).ToArray();

        await channel.WriteAsync(samples);
        await WaitForAsync(() => mixer.GetDiagnostics().LastDroppedLane is not null);

        AudioMixerDiagnostics diagnostics = mixer.GetDiagnostics();
        Assert.Equal("East Bay/Oakland Fire DSP", diagnostics.LastDroppedLane);
        Assert.Equal(12 * 160, diagnostics.LastDroppedLaneSamples);
        Assert.Equal(12 * 160, diagnostics.DroppedSamples);
        Assert.Equal(1, diagnostics.OverflowResynchronizations);
    }

    [Fact]
    public async Task StartsADeviceBackedLaneAfterTheDefaultPlayoutCushion()
    {
        var output = new BufferedFakePlayback();
        await using var mixer = new AudioMixer(output);
        await using IAudioPlayback channel = mixer.OpenChannel();

        await channel.WriteAsync(Enumerable.Repeat((short)500, 3 * 160).ToArray());
        await Task.Delay(40);
        Assert.Empty(output.Frames);

        await channel.WriteAsync(CreateSamples(500));
        await WaitForAsync(() => output.Frames.Count >= 4);

        AudioMixerDiagnostics diagnostics = mixer.GetDiagnostics();
        Assert.Equal(4, diagnostics.StartupBufferedFrames);
        Assert.Equal(16, diagnostics.MaximumBufferedFrames);
    }

    [Fact]
    public async Task ReleasesAShortCallWhenItsInputCompletes()
    {
        var output = new BufferedFakePlayback();
        await using var mixer = new AudioMixer(output);
        await using IAudioPlayback channel = mixer.OpenChannel();

        await channel.WriteAsync(Enumerable.Repeat((short)500, 3 * 160).ToArray());
        await Task.Delay(40);
        Assert.Empty(output.Frames);

        await channel.FlushAsync();
        await WaitForAsync(() => output.Frames.Count == 3);

        Assert.Equal(0, mixer.DroppedSamples);
    }

    [Fact]
    public async Task DisposingALaneDiscardsItsPendingAudioImmediately()
    {
        var output = new BufferedFakePlayback();
        await using var mixer = new AudioMixer(output);
        IAudioPlayback channel = mixer.OpenChannel();

        await channel.WriteAsync(Enumerable.Repeat((short)500, 3 * 160).ToArray());
        await channel.DisposeAsync();
        await Task.Delay(40);

        Assert.Empty(output.Frames);
    }

    [Fact]
    public async Task DisabledLiveLaneDiscardsPcmWithoutClosingTheLane()
    {
        var output = new FakePlayback();
        await using var mixer = new AudioMixer(output);
        await using IAudioPlayback channel = mixer.OpenChannel();
        var livePlayback = Assert.IsAssignableFrom<ILiveAudioPlaybackControl>(channel);

        livePlayback.LivePlaybackEnabled = false;
        await channel.WriteAsync(CreateSamples(100));
        await Task.Delay(40);
        Assert.Empty(output.Frames);

        livePlayback.LivePlaybackEnabled = true;
        await channel.WriteAsync(CreateSamples(200));
        await WaitForAsync(() => output.Frames.Count > 0);

        Assert.Equal((short)200, output.Frames[0][0]);
    }

    [Fact]
    public async Task WritesOutputOnTheDedicatedMixerThread()
    {
        var output = new FakePlayback();
        await using var mixer = new AudioMixer(output);
        await using IAudioPlayback channel = mixer.OpenChannel();

        await channel.WriteAsync(CreateSamples(500));
        await WaitForAsync(() => output.Frames.Count > 0);

        Assert.Equal("DVM Console RX mixer", output.LastWriterThreadName);
    }

    [Fact]
    public async Task DiscardsLiveInputWithoutReplayingItAfterTheTransition()
    {
        var output = new FakePlayback();
        await using var mixer = new AudioMixer(output);
        await using IAudioPlayback channel = mixer.OpenChannel();

        mixer.SetInputDiscarded(true);
        await channel.WriteAsync(Enumerable.Repeat((short)100, 320).ToArray());
        await Task.Delay(40);

        Assert.Empty(output.Frames);
        Assert.Equal(320, mixer.GetDiagnostics().TransitionDiscardedSamples);

        mixer.SetInputDiscarded(false);
        await channel.WriteAsync(CreateSamples(200));
        await WaitForAsync(() => output.Frames.Count > 0);

        Assert.Single(output.Frames);
        Assert.Equal((short)200, output.Frames[0][0]);
    }

    [Fact]
    public async Task RefillsAStarvedDeviceBufferWithMultipleReadyFrames()
    {
        var output = new BufferedFakePlayback();
        await using var mixer = new AudioMixer(output);
        await using IAudioPlayback channel = mixer.OpenChannel();
        var presentationDelays = new ConcurrentQueue<TimeSpan>();
        Assert.IsAssignableFrom<IAudioPlaybackPresentationSource>(channel)
            .SetPresentationObserver((_, delay) => presentationDelays.Enqueue(delay));
        short[] samples = Enumerable.Repeat((short)500, 4 * 160).ToArray();

        await channel.WriteAsync(samples);
        await WaitForAsync(() => output.Frames.Count >= 4);
        await WaitForAsync(() => presentationDelays.Count >= 4);

        Assert.Equal(4, output.Frames.Count);
        Assert.Equal(4 * 160, output.QueuedSamples);
        Assert.Equal(
            [0, 20, 40, 60],
            presentationDelays.Select(delay => (int)delay.TotalMilliseconds).ToArray());
    }

    [Fact]
    public async Task RaisesOutputTargetAfterAPrimedBufferRunsLowWithFramesReady()
    {
        var output = new BufferedFakePlayback();
        await using var mixer = new AudioMixer(output);
        await using IAudioPlayback channel = mixer.OpenChannel();

        await channel.WriteAsync(Enumerable.Repeat((short)500, 12 * 160).ToArray());
        await WaitForAsync(() => output.Frames.Count >= 4);

        output.ConsumeAll();
        await WaitForAsync(() => mixer.GetDiagnostics().LowBufferRecoveries > 0);

        AudioMixerDiagnostics diagnostics = mixer.GetDiagnostics();
        Assert.Equal(6, diagnostics.TargetOutputBufferedFrames);
        Assert.True(output.Frames.Count >= 10);
        Assert.True(diagnostics.PeakBufferedFrames >= 12);
    }

    [Fact]
    public async Task ReportsPhysicalStarvationAndEndsContinuityAfterAudioDrains()
    {
        var output = new BufferedFakePlayback
        {
            StarvedDuration = TimeSpan.FromMilliseconds(40)
        };
        await using var mixer = new AudioMixer(output);
        await using IAudioPlayback channel = mixer.OpenChannel();

        await channel.WriteAsync(Enumerable.Repeat((short)500, 8 * 160).ToArray());
        await WaitForAsync(() => output.Frames.Count >= 4);
        output.ConsumeAll();
        await WaitForAsync(() => output.Frames.Count >= 10);
        output.ConsumeAll();
        await channel.FlushAsync();
        await WaitForAsync(() => output.EndExpectedPlaybackCalls > 0);

        Assert.Equal(
            TimeSpan.FromMilliseconds(40),
            mixer.GetDiagnostics().PhysicalOutputStarvation);
    }

    [Fact]
    public async Task KeepsContinuityExpectedAcrossAnActiveInputGap()
    {
        var output = new BufferedFakePlayback();
        await using var mixer = new AudioMixer(output);
        await using IAudioPlayback channel = mixer.OpenChannel();

        await channel.WriteAsync(Enumerable.Repeat((short)500, 4 * 160).ToArray());
        await WaitForAsync(() => output.Frames.Count >= 4);
        output.ConsumeAll();
        await WaitForAsync(() => output.Frames.Count >= 8);
        output.ConsumeAll();
        await Task.Delay(40);

        Assert.Equal(0, output.EndExpectedPlaybackCalls);
        Assert.True(mixer.GetDiagnostics().GapFilledSamples >= 4 * 160);

        var concealment = Assert.IsAssignableFrom<IConcealmentAudioPlayback>(channel);
        await concealment.WriteConcealmentAsync(
            Enumerable.Repeat((short)250, 4 * 160).ToArray());
        Assert.Equal(4 * 160, mixer.GetDiagnostics().SuppressedLiveConcealmentSamples);

        await channel.FlushAsync();
        output.ConsumeAll();
        await WaitForAsync(() => output.EndExpectedPlaybackCalls > 0);
    }

    [Fact]
    public async Task RoutesMonoChannelsAcrossStereoAndProtectsBothSidesTogether()
    {
        var output = new FakePlayback(new PcmAudioFormat(8_000, 2, 16));
        await using var mixer = new AudioMixer(output);
        await using IAudioPlayback left = mixer.OpenChannel();
        await using IAudioPlayback center = mixer.OpenChannel();

        ((IAudioBalanceControl)left).Balance = -1.0;
        ((IAudioBalanceControl)center).Balance = 0.0;
        await left.WriteAsync(CreateSamples(30_000));
        await center.WriteAsync(CreateSamples(10_000));
        await WaitForAsync(() => output.Frames.Count > 0);

        Assert.Equal(320, output.Frames[0].Length);
        Assert.Equal(short.MaxValue, output.Frames[0][0]);
        Assert.Equal((short)8_192, output.Frames[0][1]);
        Assert.Equal(short.MaxValue, output.Frames[0][318]);
        Assert.Equal((short)8_192, output.Frames[0][319]);
    }

    [Fact]
    public async Task AccumulatesPartialWritesIntoOneTwentyMillisecondFrame()
    {
        var output = new FakePlayback();
        await using var mixer = new AudioMixer(output);
        await using IAudioPlayback channel = mixer.OpenChannel();

        await channel.WriteAsync(Enumerable.Repeat((short)100, 80).ToArray());
        await Task.Delay(40);
        Assert.Empty(output.Frames);

        await channel.WriteAsync(Enumerable.Repeat((short)200, 80).ToArray());
        await WaitForAsync(() => output.Frames.Count > 0);

        Assert.Single(output.Frames);
        Assert.Equal((short)100, output.Frames[0][0]);
        Assert.Equal((short)100, output.Frames[0][79]);
        Assert.Equal((short)200, output.Frames[0][80]);
        Assert.Equal((short)200, output.Frames[0][159]);
    }

    [Fact]
    public async Task MonoFallbackKeepsHardRightChannelAudible()
    {
        var output = new FakePlayback();
        await using var mixer = new AudioMixer(output);
        await using IAudioPlayback channel = mixer.OpenChannel();
        ((IAudioBalanceControl)channel).Balance = 1.0;

        await channel.WriteAsync(CreateSamples(12_000));
        await WaitForAsync(() => output.Frames.Count > 0);

        Assert.Equal((short)12_000, output.Frames[0][0]);
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
        public FakePlayback(PcmAudioFormat? format = null)
        {
            Format = format ?? PcmAudioFormat.Voice8KhzMono16Bit;
        }

        public List<short[]> Frames { get; } = [];
        public PcmAudioFormat Format { get; }
        public string? LastWriterThreadName { get; private set; }

        public ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastWriterThreadName = Thread.CurrentThread.Name;
            Frames.Add(samples.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BufferedFakePlayback : IAudioPlayback, IAudioPlaybackContinuityDiagnostics
    {
        public List<short[]> Frames { get; } = [];
        public PcmAudioFormat Format { get; } = PcmAudioFormat.Voice8KhzMono16Bit;
        public int? QueuedSamples { get; private set; } = 0;
        public TimeSpan StarvedDuration { get; init; }
        public int EndExpectedPlaybackCalls { get; private set; }

        public void ConsumeAll() => QueuedSamples = 0;
        public void EndExpectedPlayback() => EndExpectedPlaybackCalls++;

        public ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Frames.Add(samples.ToArray());
            QueuedSamples += samples.Length;
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QueuedSamples = 0;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
