using DvmConsole.Audio;
using DvmConsole.Media;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class RecordingPlaybackCoordinatorTests
{
    [Fact]
    public async Task PlaysLocalWavThroughSelectedOutputAndStopsAtEnd()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-recording-playback-tests",
            Guid.NewGuid().ToString("N"),
            "call.wav");
        var backend = new FakeAudioBackend();
        try
        {
            using (var writer = new PcmWavFileWriter(path, PcmAudioFormat.Voice8KhzMono16Bit))
                writer.Write(Enumerable.Repeat<short>(1200, 1600).ToArray());

            await using (var coordinator = new RecordingPlaybackCoordinator(
                             () => backend,
                             () => "alternate"))
            {
                await coordinator.StartAsync(path);
                await WaitForAsync(() => !coordinator.IsPlaying());

                Assert.Equal("alternate", backend.LastOutputDeviceId);
                Assert.Single(backend.AlternatePlayback.Frames);
                Assert.Equal((short)1200, backend.AlternatePlayback.Frames[0][0]);
                Assert.True(backend.AlternatePlayback.IsDisposed);
            }

            Assert.True(backend.IsDisposed);
        }
        finally
        {
            string? directory = Path.GetDirectoryName(path);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task StopCancelsAnActivePlaybackSession()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-recording-playback-tests",
            Guid.NewGuid().ToString("N"),
            "call.wav");
        var backend = new FakeAudioBackend { BlockWrites = true };
        try
        {
            using (var writer = new PcmWavFileWriter(path, PcmAudioFormat.Voice8KhzMono16Bit))
                writer.Write(Enumerable.Repeat<short>(1200, 1600).ToArray());

            await using var coordinator = new RecordingPlaybackCoordinator(
                () => backend,
                () => "output");
            await coordinator.StartAsync(path);
            await WaitForAsync(() => coordinator.IsPlaying());

            await coordinator.StopAsync();

            Assert.False(coordinator.IsPlaying());
            Assert.True(backend.Playback.IsDisposed);
        }
        finally
        {
            string? directory = Path.GetDirectoryName(path);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReportsPlaybackFailureAfterDisposingTheSession()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-recording-playback-tests",
            Guid.NewGuid().ToString("N"),
            "call.wav");
        var backend = new FakeAudioBackend
        {
            WriteFailure = new IOException("The output device was removed.")
        };
        Exception? failure = null;
        try
        {
            using (var writer = new PcmWavFileWriter(path, PcmAudioFormat.Voice8KhzMono16Bit))
                writer.Write(Enumerable.Repeat<short>(1200, 1600).ToArray());

            await using var coordinator = new RecordingPlaybackCoordinator(
                () => backend,
                () => "output",
                exception => failure = exception);
            await coordinator.StartAsync(path);
            await WaitForAsync(() => failure is not null && !coordinator.IsPlaying());

            Assert.IsType<IOException>(failure);
            Assert.Equal("The output device was removed.", failure!.Message);
            Assert.True(backend.Playback.IsDisposed);
        }
        finally
        {
            string? directory = Path.GetDirectoryName(path);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(5);
        Assert.True(condition());
    }

    private sealed class FakeAudioBackend : IAudioBackend
    {
        public FakePlayback Playback { get; } = new();
        public FakePlayback AlternatePlayback { get; } = new();
        public bool BlockWrites { get; init; }
        public Exception? WriteFailure { get; init; }
        public string? LastOutputDeviceId { get; private set; }
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
            if (BlockWrites)
                Playback.BlockWrites = true;
            if (WriteFailure is not null)
                Playback.WriteFailure = WriteFailure;
            return device.Id == "alternate" ? AlternatePlayback : Playback;
        }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakePlayback : IAudioPlayback
    {
        public List<short[]> Frames { get; } = [];
        public bool IsDisposed { get; private set; }
        public bool BlockWrites { get; set; }
        public Exception? WriteFailure { get; set; }
        public PcmAudioFormat Format { get; } = PcmAudioFormat.Voice8KhzMono16Bit;

        public ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BlockWrites)
                return new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
            if (WriteFailure is not null)
                throw WriteFailure;
            Frames.Add(samples.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
