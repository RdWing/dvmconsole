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
        Assert.Equal(23 * 160, mixer.DroppedSamples);
        Assert.Equal((short)(23 * 160), output.Frames[0][0]);
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
        Assert.Equal(23 * 160, diagnostics.LastDroppedLaneSamples);
        Assert.Equal(23 * 160, diagnostics.DroppedSamples);
        Assert.Equal(1, diagnostics.OverflowResynchronizations);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(9)]
    public async Task AgesWholeLivePacketsInsteadOfReplayingAnOldBurst(int packetFrames)
    {
        var output = new BufferedFakePlayback(initialQueuedSamples: 4 * 160);
        await using var mixer = new AudioMixer(output);
        await using IAudioPlayback channel = mixer.OpenChannel("Dispatch stream 77");
        var packetPlayback = Assert.IsAssignableFrom<ILivePacketAudioPlayback>(channel);
        short[] oldPacket = Enumerable.Repeat((short)100, packetFrames * 160).ToArray();
        short[] currentPacket = Enumerable.Repeat((short)200, packetFrames * 160).ToArray();
        int oldPacketCount = 27 / packetFrames;

        for (int index = 0; index < oldPacketCount; index++)
            await packetPlayback.WriteLivePacketAsync(oldPacket);
        await packetPlayback.WriteLivePacketAsync(currentPacket);

        AudioMixerDiagnostics diagnostics = mixer.GetDiagnostics();
        AudioMixerLaneDiagnostics lane = Assert.Single(diagnostics.LaneDiagnostics!);
        int expectedAgedSamples = oldPacketCount * packetFrames * 160;
        Assert.Equal(expectedAgedSamples, diagnostics.AgedLiveSamples);
        Assert.Equal(expectedAgedSamples, diagnostics.DroppedSamples);
        Assert.Equal(1, diagnostics.OverflowResynchronizations);
        Assert.Equal("Dispatch stream 77", lane.Label);
        Assert.Equal(expectedAgedSamples, lane.AgedLiveSamples);

        output.ConsumeAll();
        await WaitForAsync(() => output.Frames.Count > 0);
        Assert.Equal((short)200, output.Frames[0][0]);
    }

    [Fact]
    public async Task RetainsTwoCompleteP25LdusWithoutAgingCurrentSpeech()
    {
        var output = new BufferedFakePlayback(initialQueuedSamples: 4 * 160);
        await using var mixer = new AudioMixer(output);
        await using IAudioPlayback channel = mixer.OpenChannel("P25 dispatch stream");
        var packetPlayback = Assert.IsAssignableFrom<ILivePacketAudioPlayback>(channel);
        short[] ldu = Enumerable.Repeat((short)200, 9 * 160).ToArray();

        await packetPlayback.WriteLivePacketAsync(ldu);
        await packetPlayback.WriteLivePacketAsync(ldu);

        AudioMixerDiagnostics diagnostics = mixer.GetDiagnostics();
        Assert.Equal(0, diagnostics.MaximumBufferedFrames % 9);
        Assert.Equal(0, diagnostics.AgedLiveSamples);
        Assert.Equal(0, diagnostics.DroppedSamples);
        Assert.Equal(0, diagnostics.OverflowResynchronizations);
    }

    [Fact]
    public async Task ReportsPendingPhysicalStarvation()
    {
        var output = new BufferedFakePlayback
        {
            PendingStarvedDuration = TimeSpan.FromMilliseconds(60)
        };
        await using var mixer = new AudioMixer(output);

        Assert.Equal(
            TimeSpan.FromMilliseconds(60),
            mixer.GetDiagnostics().PendingPhysicalOutputStarvation);
    }

    [Fact]
    public async Task SharedLanePropagatesPhysicalHealthWithoutOwningTheDeviceQueue()
    {
        var physicalOutput = new ObservablePhysicalPlayback
        {
            StarvedDuration = TimeSpan.FromMilliseconds(40),
            PendingStarvedDuration = TimeSpan.FromMilliseconds(20),
            OutputCallbackCount = 17
        };
        await using var sharedMixer = new AudioMixer(physicalOutput);
        await using IAudioPlayback sharedLane = sharedMixer.OpenChannel();
        await using var clientMixer = new AudioMixer(sharedLane);

        AudioMixerDiagnostics diagnostics = clientMixer.GetDiagnostics();

        Assert.Equal(TimeSpan.FromMilliseconds(40), diagnostics.PhysicalOutputStarvation);
        Assert.Equal(TimeSpan.FromMilliseconds(20), diagnostics.PendingPhysicalOutputStarvation);
        Assert.Equal(17, diagnostics.PhysicalOutputCallbackCount);
        Assert.Null(sharedLane.QueuedSamples);
        Assert.False(sharedLane is IAudioPlaybackContinuityDiagnostics);
    }

    [Fact]
    public async Task ClientMixerDetectsAStalledPhysicalCallbackAcrossASharedLane()
    {
        var physicalOutput = new StalledCallbackPlayback();
        var sharedMixer = new AudioMixer(physicalOutput);
        IAudioPlayback sharedLane = sharedMixer.OpenChannel();
        var clientMixer = new AudioMixer(sharedLane);
        IAudioPlayback clientLane = clientMixer.OpenChannel();
        var faulted = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        clientMixer.Faulted += exception => faulted.TrySetResult(exception);
        try
        {
            await clientLane.WriteAsync(
                Enumerable.Repeat((short)500, 4 * 160).ToArray());

            Exception observed = await faulted.Task.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.IsType<IOException>(observed);
        }
        finally
        {
            await clientLane.DisposeAsync();
            await clientMixer.DisposeAsync();
            await sharedLane.DisposeAsync();
            await sharedMixer.DisposeAsync();
        }
    }

    [Fact]
    public async Task StopsTheMixerWhenThePhysicalOutputCallbackStalls()
    {
        var output = new StalledCallbackPlayback();
        var mixer = new AudioMixer(output);
        IAudioPlayback channel = mixer.OpenChannel();
        var faulted = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        mixer.Faulted += exception => faulted.TrySetResult(exception);
        try
        {
            await channel.WriteAsync(
                Enumerable.Repeat((short)500, 4 * 160).ToArray());
            await WaitForAsync(() => output.WriteCalls >= 4);
            await Task.Delay(1_200);

            Exception observed = await faulted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.IsType<IOException>(observed);

            await Assert.ThrowsAsync<IOException>(async () =>
                await channel.WriteAsync(CreateSamples(600)));
        }
        finally
        {
            await channel.DisposeAsync();
            await mixer.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConcurrentDisposalWaitsForTheSharedCleanupOperation()
    {
        var output = new BlockingDrainPlayback();
        var mixer = new AudioMixer(output);

        Task first = mixer.DisposeAsync().AsTask();
        await output.DrainEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Task second = mixer.DisposeAsync().AsTask();

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        output.AllowDrain();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, output.DisposeCalls);
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
        Assert.Equal(27, diagnostics.MaximumBufferedFrames);
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
    public async Task DrainsAPartialLaneWithoutClosingIt()
    {
        var output = new FakePlayback();
        await using var mixer = new AudioMixer(output);
        await using IAudioPlayback channel = mixer.OpenChannel();

        await channel.WriteAsync(Enumerable.Repeat((short)300, 200).ToArray());
        int? firstDrain = await channel.DrainAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        await channel.WriteAsync(CreateSamples(400));
        int? secondDrain = await channel.DrainAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(200, firstDrain);
        Assert.Equal(160, secondDrain);
        Assert.Equal(3, output.Frames.Count);
        Assert.All(output.Frames[1].Skip(40), sample => Assert.Equal((short)0, sample));
        Assert.Equal((short)400, output.Frames[2][0]);
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
        public BufferedFakePlayback(int initialQueuedSamples = 0)
        {
            QueuedSamples = initialQueuedSamples;
        }

        public List<short[]> Frames { get; } = [];
        public PcmAudioFormat Format { get; } = PcmAudioFormat.Voice8KhzMono16Bit;
        public int? QueuedSamples { get; private set; }
        public TimeSpan StarvedDuration { get; init; }
        public TimeSpan PendingStarvedDuration { get; init; }
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

    private sealed class StalledCallbackPlayback :
        IAudioPlayback,
        IAudioPlaybackCallbackDiagnostics
    {
        public PcmAudioFormat Format { get; } = PcmAudioFormat.Voice8KhzMono16Bit;
        public int? QueuedSamples { get; private set; } = 0;
        public long OutputCallbackCount => 1;
        public int WriteCalls { get; private set; }

        public ValueTask WriteAsync(
            ReadOnlyMemory<short> samples,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCalls++;
            QueuedSamples += samples.Length;
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ObservablePhysicalPlayback :
        IAudioPlayback,
        IAudioPlaybackContinuityDiagnostics,
        IAudioPlaybackCallbackDiagnostics
    {
        public PcmAudioFormat Format => PcmAudioFormat.Voice8KhzMono16Bit;
        public int? QueuedSamples => 0;
        public TimeSpan StarvedDuration { get; init; }
        public TimeSpan PendingStarvedDuration { get; init; }
        public long OutputCallbackCount { get; init; }

        public void EndExpectedPlayback()
        {
        }

        public ValueTask WriteAsync(
            ReadOnlyMemory<short> samples,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;
    }

    private sealed class BlockingDrainPlayback : IAudioPlayback
    {
        private readonly TaskCompletionSource drainCompletion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public PcmAudioFormat Format { get; } = PcmAudioFormat.Voice8KhzMono16Bit;
        public TaskCompletionSource DrainEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCalls { get; private set; }

        public ValueTask WriteAsync(
            ReadOnlyMemory<short> samples,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask FlushAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public async ValueTask<int?> DrainAsync(CancellationToken cancellationToken = default)
        {
            DrainEntered.TrySetResult();
            await drainCompletion.Task.WaitAsync(cancellationToken);
            return 0;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }

        public void AllowDrain() => drainCompletion.TrySetResult();
    }
}
