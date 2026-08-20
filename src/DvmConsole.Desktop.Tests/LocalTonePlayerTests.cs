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
            Assert.Equal(3680, result.QueuedSamples);
            Assert.Equal(3680, result.ConsumedSamples);
            Assert.Equal(1, result.Attempts);
        }

        Assert.Equal("alternate", backend.LastOutputDeviceId);
        Assert.Equal(2, backend.Playback.Frames.Count);
        Assert.Equal(2400, backend.Playback.Frames[0].Length);
        Assert.All(backend.Playback.Frames[0], sample => Assert.Equal((short)0, sample));
        short[] samples = backend.Playback.Frames[1];
        Assert.Equal(1280, samples.Length);
        Assert.Contains(samples, sample => sample != 0);
        Assert.InRange(samples.Max(), 12_000, 14_000);
        Assert.Contains(samples[..640], sample => sample != 0);
        Assert.All(samples[640..], sample => Assert.Equal((short)0, sample));
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
        Assert.Equal(4, backend.OutputEnumerationCount);
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
    public async Task ColdStartCueRetriesWhenOutputChangesDuringExtendedWarmup()
    {
        var first = new FakeAudioBackend();
        var recovered = new FakeAudioBackend();
        var backends = new Queue<FakeAudioBackend>([first, recovered]);
        var routeResolver = new SequenceOutputRouteResolver(
            Output("old", "Headset stereo"),
            Output("duplex", "Headset duplex"),
            Output("duplex", "Headset duplex"),
            Output("duplex", "Headset duplex"));
        var delays = new List<TimeSpan>();
        await using var player = new LocalTonePlayer(
            () => backends.Dequeue(),
            () => "default",
            routeResolver,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        LocalTonePlaybackResult result = await player.PlayAsync(LocalToneCues.ColdStartTalkPermit);

        Assert.Equal(2, result.Attempts);
        Assert.Equal("duplex", result.Output.Id);
        Assert.Single(first.Playback.Frames);
        Assert.Equal(2, recovered.Playback.Frames.Count);
        Assert.Contains(LocalToneCues.ColdStartTalkPermit.OutputPostDrainDuration, delays);
        Assert.True(first.Playback.IsDisposed);
        Assert.True(recovered.Playback.IsDisposed);
    }

    [Fact]
    public async Task KnownNonBluetoothColdRouteKeepsStandardPermitTiming()
    {
        var backend = new FakeAudioBackend { OutputIsBluetooth = false };
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

        Assert.Equal(3680, result.QueuedSamples);
        Assert.Equal(2400, backend.Playback.Frames[0].Length);
        Assert.Equal([LocalToneCues.TalkPermit.OutputPostDrainDuration], delays);
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

        Assert.Equal(10880, result.QueuedSamples);
        Assert.Equal(8000, backend.Playback.Frames[0].Length);
        Assert.Equal([LocalToneCues.ColdStartTalkPermit.OutputPostDrainDuration], delays);
    }

    [Theory]
    [InlineData(false, null, null, false)]
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
        Assert.True(LocalToneCues.ColdStartTalkPermit.OutputWarmupDuration >
            LocalToneCues.TalkPermit.OutputWarmupDuration);
        Assert.True(LocalToneCues.ColdStartTalkPermit.ToneDuration >
            LocalToneCues.TalkPermit.ToneDuration);
        Assert.True(LocalToneCues.ColdStartTalkPermit.OutputPostDrainDuration >
            LocalToneCues.TalkPermit.OutputPostDrainDuration);
        Assert.Equal(TimeSpan.Zero, LocalToneCues.ConnectionEstablished.OutputWarmupDuration);
        Assert.Equal(TimeSpan.Zero, LocalToneCues.ConnectionLost.OutputWarmupDuration);
    }

    private static AudioDeviceInfo Output(string id, string name)
        => new(id, name, AudioDirection.Output, true);

    private sealed class FakeAudioBackend : IAudioBackend
    {
        public FakePlayback Playback { get; } = new();
        public string? LastOutputDeviceId { get; private set; }
        public int OpenPlaybackCount { get; private set; }
        public int MissingAlternateEnumerations { get; init; }
        public bool? OutputIsBluetooth { get; init; } = false;
        public bool FailOpenPlayback { get; init; }
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
            return Playback;
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
            if (outputs.Count == 0)
                throw new InvalidOperationException("No test output remains.");
            return Task.FromResult(outputs.Dequeue());
        }
    }

    private sealed class FakePlayback : IAudioPlayback
    {
        public List<short[]> Frames { get; } = [];
        public bool IsDisposed { get; private set; }
        public bool WasFlushed { get; private set; }
        public int DrainCount { get; private set; }
        public int QueuedSampleCount { get; private set; }
        public int? QueuedSamples => QueuedSampleCount;
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
