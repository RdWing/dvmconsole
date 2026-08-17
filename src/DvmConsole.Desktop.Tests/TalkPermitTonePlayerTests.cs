using DvmConsole.Audio;
using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class TalkPermitTonePlayerTests
{
    [Fact]
    public async Task PlaysShortLocalToneOnRequestedOutput()
    {
        var backend = new FakeAudioBackend();
        var player = new TalkPermitTonePlayer(() => backend, () => "alternate");
        await using (player)
        {
            AudioDeviceInfo output = await player.PlayAsync();

            Assert.Equal("alternate", output.Id);
            Assert.Equal(1, backend.OpenPlaybackCount);
            Assert.True(backend.Playback.IsDisposed);
            Assert.Equal(960, player.LastQueuedSamples);
            Assert.Equal(960, player.LastConsumedSamples);
        }

        Assert.Equal("alternate", backend.LastOutputDeviceId);
        Assert.Single(backend.Playback.Frames);
        short[] samples = backend.Playback.Frames[0];
        Assert.Equal(960, samples.Length);
        Assert.Contains(samples, sample => sample != 0);
        Assert.InRange(samples.Max(), 12_000, 14_000);
        Assert.Equal(0, samples[0]);
        Assert.False(backend.Playback.WasFlushed);
        Assert.Equal(1, backend.Playback.DrainCount);
        Assert.True(backend.Playback.IsDisposed);
        Assert.True(backend.IsDisposed);
    }

    private sealed class FakeAudioBackend : IAudioBackend
    {
        public FakePlayback Playback { get; } = new();
        public string? LastOutputDeviceId { get; private set; }
        public int OpenPlaybackCount { get; private set; }
        public bool IsDisposed { get; private set; }
        public string Name => "fake";

        public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
            => direction == AudioDirection.Output
                ? [
                    new AudioDeviceInfo("output", "Fake output", direction, true),
                    new AudioDeviceInfo("alternate", "Fake alternate output", direction, false)
                ]
                : [new AudioDeviceInfo("input", "Fake input", direction, true)];

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
