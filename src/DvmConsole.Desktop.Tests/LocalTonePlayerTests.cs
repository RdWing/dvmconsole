using DvmConsole.Audio;
using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class LocalTonePlayerTests
{
    [Fact]
    public async Task PlaysTalkPermitCueOnRequestedOutputWithPreparedRoute()
    {
        var backend = new FakeAudioBackend();
        var routeResolver = new AudioOutputRouteResolver((_, _) => Task.CompletedTask);
        var delays = new List<TimeSpan>();
        var player = new LocalTonePlayer(
            () => backend,
            () => "alternate",
            routeResolver,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });
        await using (player)
        {
            LocalTonePlaybackResult result = await player.PlayAsync(LocalToneCues.TalkPermit);

            Assert.Equal("alternate", result.Output.Id);
            Assert.Equal(1, backend.OpenPlaybackCount);
            Assert.True(backend.Playback.IsDisposed);
            Assert.Equal(3040, result.QueuedSamples);
            Assert.Equal(3040, result.ConsumedSamples);
            Assert.Equal(1, result.Attempts);
        }

        Assert.Equal("alternate", backend.LastOutputDeviceId);
        Assert.Equal(2, backend.Playback.Frames.Count);
        Assert.Equal(2400, backend.Playback.Frames[0].Length);
        Assert.All(backend.Playback.Frames[0], sample => Assert.Equal((short)0, sample));
        short[] samples = backend.Playback.Frames[1];
        Assert.Equal(640, samples.Length);
        Assert.Contains(samples, sample => sample != 0);
        Assert.InRange(samples.Max(), 12_000, 14_000);
        Assert.Equal([TimeSpan.FromMilliseconds(200)], delays);
        Assert.False(backend.Playback.WasFlushed);
        Assert.Equal(2, backend.Playback.DrainCount);
        Assert.True(backend.Playback.IsDisposed);
        Assert.True(backend.IsDisposed);
    }

    [Fact]
    public async Task WaitsForTemporarilyMissingSelectedOutputDuringRouteChange()
    {
        var backend = new FakeAudioBackend { MissingAlternateEnumerations = 2 };
        var routeResolver = new AudioOutputRouteResolver((_, _) => Task.CompletedTask);
        await using var player = new LocalTonePlayer(
            () => backend,
            () => "alternate",
            routeResolver);

        LocalTonePlaybackResult result = await player.PlayAsync(LocalToneCues.ConnectionEstablished);

        Assert.Equal("alternate", result.Output.Id);
        Assert.Equal(6, backend.OutputEnumerationCount);
        Assert.Equal("alternate", backend.LastOutputDeviceId);
        Assert.Single(backend.Playback.Frames);
        Assert.Equal(1, backend.Playback.DrainCount);
    }

    [Fact]
    public async Task WaitsForSystemDefaultOutputIdentityToStabilize()
    {
        var backend = new ChangingDefaultAudioBackend(["old-default", "new-default", "new-default"]);
        var routeResolver = new AudioOutputRouteResolver((_, _) => Task.CompletedTask);

        AudioDeviceInfo output = await routeResolver.ResolveAsync(
            backend,
            "default",
            new AudioOutputRoutePolicy(4, TimeSpan.Zero),
            CancellationToken.None);

        Assert.Equal("new-default", output.Id);
        Assert.Equal(3, backend.OutputEnumerationCount);
    }

    [Fact]
    public async Task DoesNotFallBackWhenSelectedOutputNeverReturns()
    {
        var backend = new FakeAudioBackend { MissingAlternateEnumerations = int.MaxValue };
        var routeResolver = new AudioOutputRouteResolver((_, _) => Task.CompletedTask);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            routeResolver.ResolveAsync(
                backend,
                "alternate",
                new AudioOutputRoutePolicy(3, TimeSpan.Zero),
                CancellationToken.None));

        Assert.Contains("selected audio output 'alternate'", exception.Message);
        Assert.Equal(3, backend.OutputEnumerationCount);
        Assert.Null(backend.LastOutputDeviceId);
    }

    [Fact]
    public async Task RetriesTransientPlaybackOpenFailureBeforeCompletingCue()
    {
        var failed = new FakeAudioBackend { FailOpenPlayback = true };
        var recovered = new FakeAudioBackend();
        var backends = new Queue<FakeAudioBackend>([failed, recovered]);
        var routeResolver = new AudioOutputRouteResolver((_, _) => Task.CompletedTask);
        await using var player = new LocalTonePlayer(
            () => backends.Dequeue(),
            () => "alternate",
            routeResolver,
            (_, _) => Task.CompletedTask);

        LocalTonePlaybackResult result = await player.PlayAsync(LocalToneCues.TalkPermit);

        Assert.Equal(2, result.Attempts);
        Assert.True(failed.IsDisposed);
        Assert.Equal(1, failed.OpenPlaybackCount);
        Assert.Equal(1, recovered.OpenPlaybackCount);
        Assert.True(recovered.Playback.IsDisposed);
    }

    [Fact]
    public async Task ColdStartCueReopensChangedOutputAfterMicrophoneReadiness()
    {
        var backend = new FakeAudioBackend();
        var routeResolver = new SequenceOutputRouteResolver(
            Output("old", "Headset stereo"),
            Output("duplex", "Headset duplex"));
        var delays = new List<TimeSpan>();
        await using var player = new LocalTonePlayer(
            () => backend,
            () => "default",
            routeResolver,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        LocalTonePlaybackResult result = await player.PlayAsync(LocalToneCues.ColdStartTalkPermit);

        Assert.Equal(1, result.Attempts);
        Assert.Equal("duplex", result.Output.Id);
        Assert.Equal(2, backend.OpenPlaybackCount);
        Assert.Empty(backend.Playbacks[0].Frames);
        Assert.Single(backend.Playbacks[1].Frames);
        Assert.Contains(LocalToneCues.ColdStartTalkPermit.OutputPostDrainDuration, delays);
        Assert.All(backend.Playbacks, playback => Assert.True(playback.IsDisposed));
    }

    [Fact]
    public async Task KnownNonBluetoothColdRouteKeepsStandardPermitTimingForWiredAndWindowsEndpoints()
    {
        var backend = new FakeAudioBackend
        {
            OutputIsBluetooth = false,
            OutputPresentationLatency = TimeSpan.FromMilliseconds(725)
        };
        var delays = new List<TimeSpan>();
        await using var player = new LocalTonePlayer(
            () => backend,
            () => "default",
            new AudioOutputRouteResolver((_, _) => Task.CompletedTask),
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        LocalTonePlaybackResult result = await player.PlayTalkPermitAsync(
            microphoneStartedCold: true,
            microphoneIsBluetooth: false);

        Assert.Equal(3040, result.QueuedSamples);
        Assert.Equal(1, backend.OpenPlaybackCount);
        Assert.Equal(2, backend.Playback.Frames.Count);
        Assert.Equal(2400, backend.Playback.Frames[0].Length);
        Assert.All(backend.Playback.Frames[0], sample => Assert.Equal((short)0, sample));
        Assert.Equal(640, backend.Playback.Frames[1].Length);
        Assert.Equal([LocalToneCues.TalkPermit.OutputPostDrainDuration], delays);
        Assert.Null(result.MeasuredOutputPresentationLatency);
    }

    [Fact]
    public async Task WarmBluetoothRouteKeepsStandardPermitTiming()
    {
        var backend = new FakeAudioBackend
        {
            OutputIsBluetooth = true,
            OutputPresentationLatency = TimeSpan.FromMilliseconds(725)
        };
        var delays = new List<TimeSpan>();
        await using var player = new LocalTonePlayer(
            () => backend,
            () => "default",
            new AudioOutputRouteResolver((_, _) => Task.CompletedTask),
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        LocalTonePlaybackResult result = await player.PlayTalkPermitAsync(
            microphoneStartedCold: false,
            microphoneIsBluetooth: true);

        Assert.Equal(3040, result.QueuedSamples);
        Assert.Equal(1, backend.OpenPlaybackCount);
        Assert.Equal(2, backend.Playback.Frames.Count);
        Assert.All(backend.Playback.Frames[0], sample => Assert.Equal((short)0, sample));
        Assert.Equal([LocalToneCues.TalkPermit.OutputPostDrainDuration], delays);
        Assert.Null(result.MeasuredOutputPresentationLatency);
    }

    [Fact]
    public async Task BluetoothOutputUsesExtendedPermitTimingForColdMicrophone()
    {
        var backend = new FakeAudioBackend { OutputIsBluetooth = true };
        var delays = new List<TimeSpan>();
        await using var player = new LocalTonePlayer(
            () => backend,
            () => "default",
            new AudioOutputRouteResolver((_, _) => Task.CompletedTask),
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        LocalTonePlaybackResult result = await player.PlayTalkPermitAsync(
            microphoneStartedCold: true,
            microphoneIsBluetooth: false);

        Assert.Equal(1920, result.QueuedSamples);
        Assert.Equal(2, backend.OpenPlaybackCount);
        Assert.Empty(backend.Playbacks[0].Frames);
        Assert.Single(backend.Playback.Frames);
        Assert.Equal(1920, backend.Playback.Frames[0].Length);
        Assert.All(backend.Playback.Frames[0][..640], sample => Assert.Equal((short)0, sample));
        Assert.Contains(backend.Playback.Frames[0][640..], sample => sample != 0);
        Assert.Equal([LocalToneCues.ColdStartTalkPermit.OutputPostDrainDuration], delays);
        Assert.Null(result.MeasuredOutputPresentationLatency);
        Assert.Equal(LocalToneCues.ColdStartTalkPermit.OutputPostDrainDuration, result.PostDrainWaitDuration);
    }

    [Fact]
    public async Task ColdBluetoothWaitsForMeasuredCoreAudioPresentationDeadline()
    {
        var backend = new FakeAudioBackend
        {
            OutputIsBluetooth = true,
            OutputPresentationLatency = TimeSpan.FromMilliseconds(725)
        };
        var delays = new List<TimeSpan>();
        await using var player = new LocalTonePlayer(
            () => backend,
            () => "default",
            new AudioOutputRouteResolver((_, _) => Task.CompletedTask),
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        LocalTonePlaybackResult result = await player.PlayTalkPermitAsync(
            microphoneStartedCold: true,
            microphoneIsBluetooth: true);

        Assert.Equal(TimeSpan.FromMilliseconds(725), result.MeasuredOutputPresentationLatency);
        Assert.Equal(TimeSpan.FromMilliseconds(745), result.PostDrainWaitDuration);
        Assert.Equal([TimeSpan.FromMilliseconds(745)], delays);
    }

    [Fact]
    public async Task ColdOutputPreparationOverlapsTheCallerReadinessBarrier()
    {
        var backend = new FakeAudioBackend { OutputIsBluetooth = true };
        var cueRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var player = new LocalTonePlayer(
            () => backend,
            () => "default",
            new AudioOutputRouteResolver((_, _) => Task.CompletedTask),
            (_, _) => Task.CompletedTask);

        Task<LocalTonePlaybackResult> playback = player.PlayTalkPermitAsync(
            microphoneStartedCold: true,
            microphoneIsBluetooth: true,
            cueReleaseBarrier: cueRelease.Task);

        Assert.False(playback.IsCompleted);
        Assert.Equal(1, backend.OpenPlaybackCount);
        Assert.Empty(backend.Playback.Frames);

        cueRelease.SetResult(true);
        LocalTonePlaybackResult result = await playback;

        Assert.Equal(2, backend.OpenPlaybackCount);
        Assert.True(backend.Playbacks[0].IsDisposed);
        Assert.Single(backend.Playback.Frames);
        Assert.Contains(backend.Playback.Frames[0], sample => sample != 0);
        Assert.True(result.Timing.InitialPlaybackOpened <= result.Timing.CueReleased);
        Assert.True(result.Timing.CueReleased <= result.Timing.OutputRouteConfirmed);
        Assert.True(result.Timing.OutputRouteConfirmed <= result.Timing.FinalPlaybackOpened);
        Assert.True(result.Timing.FinalPlaybackOpened <= result.Timing.OutputWarmupDrained);
        Assert.True(result.Timing.OutputWarmupDrained <= result.Timing.CueQueued);
        Assert.True(result.Timing.CueQueued <= result.Timing.CueDrained);
        Assert.True(result.Timing.CueDrained <= result.Timing.Completed);
        Assert.True(result.PresentationEvidence.CallbackConsumptionConfirmed);
    }

    [Fact]
    public async Task ActivatesProtocolImmediatelyBeforeColdCueIsQueued()
    {
        var backend = new FakeAudioBackend { OutputIsBluetooth = true };
        int activationCount = 0;
        long callbacksAtActivation = 0;
        int framesAtActivation = 0;
        await using var player = new LocalTonePlayer(
            () => backend,
            () => "default",
            new AudioOutputRouteResolver((_, _) => Task.CompletedTask),
            (_, _) => Task.CompletedTask);

        LocalTonePlaybackResult result = await player.PlayTalkPermitAsync(
            microphoneStartedCold: true,
            microphoneIsBluetooth: true,
            beforeCueAsync: _ =>
            {
                activationCount++;
                callbacksAtActivation = backend.Playback.OutputCallbackCount;
                framesAtActivation = backend.Playback.Frames.Count;
                return Task.CompletedTask;
            });

        Assert.Equal(1, activationCount);
        Assert.Equal(0, callbacksAtActivation);
        Assert.Equal(0, framesAtActivation);
        Assert.Single(backend.Playback.Frames);
        Assert.True(result.PresentationEvidence.CallbackConsumptionConfirmed);
    }

    [Fact]
    public async Task PermitCueRejectsDrainWithoutNativeRenderProgress()
    {
        var backend = new FakeAudioBackend { SuppressCallbackProgress = true };
        await using var player = new LocalTonePlayer(
            () => backend,
            () => "default",
            new AudioOutputRouteResolver((_, _) => Task.CompletedTask),
            (_, _) => Task.CompletedTask);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            player.PlayTalkPermitAsync(
                microphoneStartedCold: false,
                microphoneIsBluetooth: false));

        Assert.IsType<IOException>(exception.InnerException);
        Assert.Contains("without a native render callback", exception.InnerException.Message);
    }

    [Fact]
    public async Task FailedCueReleaseDoesNotRetryAPreparedOutput()
    {
        var backend = new FakeAudioBackend { OutputIsBluetooth = true };
        var cueRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var player = new LocalTonePlayer(
            () => backend,
            () => "default",
            new AudioOutputRouteResolver((_, _) => Task.CompletedTask),
            (_, _) => Task.CompletedTask);

        Task<LocalTonePlaybackResult> playback = player.PlayTalkPermitAsync(
            microphoneStartedCold: true,
            microphoneIsBluetooth: true,
            cueReleaseBarrier: cueRelease.Task);
        cueRelease.SetException(new TimeoutException("microphone did not become ready"));

        await Assert.ThrowsAsync<TimeoutException>(() => playback);
        Assert.Equal(1, backend.OpenPlaybackCount);
    }

    [Theory]
    [InlineData(false, null, null, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, null, false, true)]
    [InlineData(true, false, null, true)]
    public void SelectsExtendedPermitOnlyForColdBluetoothOrUnknownRoutes(
        bool microphoneStartedCold,
        bool? microphoneIsBluetooth,
        bool? outputIsBluetooth,
        bool expectedExtended)
    {
        LocalTonePlaybackRequest selected = LocalToneCues.SelectTalkPermit(
            microphoneStartedCold,
            microphoneIsBluetooth,
            outputIsBluetooth);

        Assert.Same(
            expectedExtended ? LocalToneCues.ColdStartTalkPermit : LocalToneCues.TalkPermit,
            selected);
    }

    [Fact]
    public void CueDefinitionsDeclareTheirOutputPreparationPolicy()
    {
        Assert.True(LocalToneCues.TalkPermit.OutputWarmupDuration > TimeSpan.Zero);
        Assert.True(LocalToneCues.TalkPermit.OutputPostDrainDuration > TimeSpan.Zero);
        Assert.True(LocalToneCues.TalkPermit.MaximumPlaybackAttempts > 1);
        Assert.True(LocalToneCues.ColdStartTalkPermit.ReopenOutputAfterCueRelease);
        Assert.False(LocalToneCues.TalkPermit.ReopenOutputAfterCueRelease);
        Assert.Equal(TimeSpan.Zero, LocalToneCues.ColdStartTalkPermit.OutputWarmupDuration);
        Assert.True(LocalToneCues.ColdStartTalkPermit.UseMeasuredOutputPresentationLatency);
        Assert.False(LocalToneCues.TalkPermit.UseMeasuredOutputPresentationLatency);
        Assert.True(LocalToneCues.ColdStartTalkPermit.ToneDuration >
            LocalToneCues.TalkPermit.ToneDuration);
        Assert.Equal(TimeSpan.Zero, LocalToneCues.TalkPermit.LeadSilenceDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(80), LocalToneCues.ColdStartTalkPermit.LeadSilenceDuration);
        Assert.Equal(TimeSpan.Zero, LocalToneCues.TalkPermit.TailSilenceDuration);
        Assert.Equal(TimeSpan.Zero, LocalToneCues.ColdStartTalkPermit.TailSilenceDuration);
        Assert.True(LocalToneCues.ColdStartTalkPermit.OutputPostDrainDuration >
            LocalToneCues.TalkPermit.OutputPostDrainDuration);
        Assert.Equal(TimeSpan.Zero, LocalToneCues.ConnectionEstablished.OutputWarmupDuration);
        Assert.Equal(TimeSpan.Zero, LocalToneCues.ConnectionLost.OutputWarmupDuration);
    }

    private static AudioDeviceInfo Output(string id, string name)
        => new(id, name, AudioDirection.Output, true);

    private sealed class FakeAudioBackend : IAudioBackend
    {
        public List<FakePlayback> Playbacks { get; } = [];
        public FakePlayback Playback => Playbacks[^1];
        public string? LastOutputDeviceId { get; private set; }
        public int OpenPlaybackCount { get; private set; }
        public int MissingAlternateEnumerations { get; init; }
        public bool? OutputIsBluetooth { get; init; } = false;
        public TimeSpan OutputPresentationLatency { get; init; }
        public bool FailOpenPlayback { get; init; }
        public bool SuppressCallbackProgress { get; init; }
        public int OutputEnumerationCount { get; private set; }
        public bool IsDisposed { get; private set; }
        public string Name => "fake";

        public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
        {
            if (direction != AudioDirection.Output)
                return [new AudioDeviceInfo("input", "Fake input", direction, true, false)];

            OutputEnumerationCount++;
            if (OutputEnumerationCount <= MissingAlternateEnumerations)
                return [new AudioDeviceInfo("output", "Fake output", direction, true, OutputIsBluetooth)];
            return
            [
                new AudioDeviceInfo("output", "Fake output", direction, true, OutputIsBluetooth),
                new AudioDeviceInfo("alternate", "Fake alternate output", direction, false, OutputIsBluetooth)
            ];
        }

        public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format)
            => throw new NotSupportedException();

        public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format)
        {
            LastOutputDeviceId = device.Id;
            OpenPlaybackCount++;
            if (FailOpenPlayback)
                throw new IOException("test output route changed while opening playback");
            var playback = new FakePlayback(
                SuppressCallbackProgress,
                OutputPresentationLatency);
            Playbacks.Add(playback);
            return playback;
        }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class ChangingDefaultAudioBackend(IReadOnlyList<string> outputIds) : IAudioBackend
    {
        public string Name => "changing-default";
        public int OutputEnumerationCount { get; private set; }

        public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
        {
            if (direction != AudioDirection.Output)
                return [new AudioDeviceInfo("input", "Fake input", direction, true)];

            int index = Math.Min(OutputEnumerationCount, outputIds.Count - 1);
            OutputEnumerationCount++;
            string id = outputIds[index];
            return [new AudioDeviceInfo(id, id, direction, true)];
        }

        public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format)
            => throw new NotSupportedException();

        public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class SequenceOutputRouteResolver : IAudioOutputRouteResolver
    {
        private readonly Queue<AudioDeviceInfo> outputs;
        private AudioDeviceInfo? lastOutput;

        public SequenceOutputRouteResolver(params AudioDeviceInfo[] outputs)
        {
            this.outputs = new Queue<AudioDeviceInfo>(outputs);
        }

        public Task<AudioDeviceInfo> ResolveAsync(
            IAudioBackend backend,
            string? requestedDeviceId,
            AudioOutputRoutePolicy policy,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (outputs.Count > 0)
                lastOutput = outputs.Dequeue();
            return Task.FromResult(lastOutput ??
                throw new InvalidOperationException("No test output remains."));
        }
    }

    private sealed class FakePlayback(
        bool suppressCallbackProgress,
        TimeSpan outputPresentationLatency) :
        IAudioPlayback,
        IAudioPlaybackCallbackDiagnostics,
        IAudioPlaybackPresentationLatencyDiagnostics
    {
        public List<short[]> Frames { get; } = [];
        public bool IsDisposed { get; private set; }
        public bool WasFlushed { get; private set; }
        public int DrainCount { get; private set; }
        public int QueuedSampleCount { get; private set; }
        public int? QueuedSamples => QueuedSampleCount;
        public long OutputCallbackCount { get; private set; }
        public TimeSpan OutputPresentationLatency => outputPresentationLatency;
        public PcmAudioFormat Format { get; } = PcmAudioFormat.Voice8KhzMono16Bit;

        public ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            short[] frame = samples.ToArray();
            Frames.Add(frame);
            QueuedSampleCount += frame.Length;
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default)
        {
            WasFlushed = true;
            Frames.Clear();
            return ValueTask.CompletedTask;
        }

        public ValueTask<int?> DrainAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DrainCount++;
            if (!suppressCallbackProgress)
                OutputCallbackCount++;
            int consumed = QueuedSampleCount;
            QueuedSampleCount = 0;
            return ValueTask.FromResult<int?>(consumed);
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
