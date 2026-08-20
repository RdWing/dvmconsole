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
        var player = new LocalTonePlayer(() => backend, () => "alternate");
        await using (player)
        {
            LocalTonePlaybackResult result = await player.PlayAsync(LocalToneCues.TalkPermit);

            Assert.Equal("alternate", result.Output.Id);
            Assert.Equal(1, backend.OpenPlaybackCount);
            Assert.True(backend.Playback.IsDisposed);
            Assert.Equal(2560, result.QueuedSamples);
            Assert.Equal(2560, result.ConsumedSamples);
        }

        Assert.Equal("alternate", backend.LastOutputDeviceId);
        Assert.Equal(2, backend.Playback.Frames.Count);
        Assert.Equal(1600, backend.Playback.Frames[0].Length);
        Assert.All(backend.Playback.Frames[0], sample => Assert.Equal((short)0, sample));
        short[] samples = backend.Playback.Frames[1];
        Assert.Equal(960, samples.Length);
        Assert.Contains(samples, sample => sample != 0);
        Assert.InRange(samples.Max(), 12_000, 14_000);
        Assert.Contains(samples[..640], sample => sample != 0);
        Assert.All(samples[640..], sample => Assert.Equal((short)0, sample));
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
        Assert.Equal(3, backend.OutputEnumerationCount);
        Assert.Equal("alternate", backend.LastOutputDeviceId);
        Assert.Single(backend.Playback.Frames);
        Assert.Equal(1, backend.Playback.DrainCount);
    }

    [Fact]
    public void CueDefinitionsDeclareTheirOutputPreparationPolicy()
    {
        Assert.True(LocalToneCues.TalkPermit.OutputWarmupDuration > TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, LocalToneCues.ConnectionEstablished.OutputWarmupDuration);
        Assert.Equal(TimeSpan.Zero, LocalToneCues.ConnectionLost.OutputWarmupDuration);
    }

    private sealed class FakeAudioBackend : IAudioBackend
    {
        public FakePlayback Playback { get; } = new();
        public string? LastOutputDeviceId { get; private set; }
        public int OpenPlaybackCount { get; private set; }
        public int MissingAlternateEnumerations { get; init; }
        public int OutputEnumerationCount { get; private set; }
        public bool IsDisposed { get; private set; }
        public string Name => "fake";

        public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
        {
            if (direction != AudioDirection.Output)
                return [new AudioDeviceInfo("input", "Fake input", direction, true)];

            OutputEnumerationCount++;
            if (OutputEnumerationCount <= MissingAlternateEnumerations)
                return [new AudioDeviceInfo("output", "Fake output", direction, true)];
            return
            [
                new AudioDeviceInfo("output", "Fake output", direction, true),
                new AudioDeviceInfo("alternate", "Fake alternate output", direction, false)
            ];
        }

        public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format)
            => throw new NotSupportedException();

        public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format)
        {
            LastOutputDeviceId = device.Id;
            OpenPlaybackCount++;
            return Playback;
        }

        public void Dispose() => IsDisposed = true;
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
