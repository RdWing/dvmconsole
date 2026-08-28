using System.Collections.Concurrent;
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
                Assert.True(backend.AlternatePlayback.DrainCalled);
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
    public async Task ReportsThePlayingPathUntilPlaybackIsStopped()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-recording-playback-tests",
            Guid.NewGuid().ToString("N"),
            "call.wav");
        var backend = new FakeAudioBackend { BlockWrites = true };
        var states = new ConcurrentQueue<RecordingPlaybackStateChangedEventArgs>();
        try
        {
            using (var writer = new PcmWavFileWriter(path, PcmAudioFormat.Voice8KhzMono16Bit))
                writer.Write(Enumerable.Repeat<short>(1200, 1600).ToArray());

            await using var coordinator = new RecordingPlaybackCoordinator(
                () => backend,
                () => "output");
            coordinator.PlaybackStateChanged += (_, state) => states.Enqueue(state);

            await coordinator.StartAsync(path);
            await WaitForAsync(() => coordinator.IsPlaying(path));
            await coordinator.StopAsync();

            Assert.Collection(
                states.ToArray(),
                state =>
                {
                    Assert.True(state.IsPlaying);
                    Assert.Equal(Path.GetFullPath(path), state.Path);
                },
                state =>
                {
                    Assert.False(state.IsPlaying);
                    Assert.Equal(Path.GetFullPath(path), state.Path);
                });
        }
        finally
        {
            string? directory = Path.GetDirectoryName(path);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReportsStoppedWhenPlaybackReachesTheEnd()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-recording-playback-tests",
            Guid.NewGuid().ToString("N"),
            "call.wav");
        var backend = new FakeAudioBackend();
        var states = new ConcurrentQueue<bool>();
        try
        {
            using (var writer = new PcmWavFileWriter(path, PcmAudioFormat.Voice8KhzMono16Bit))
                writer.Write(Enumerable.Repeat<short>(1200, 160).ToArray());

            await using var coordinator = new RecordingPlaybackCoordinator(
                () => backend,
                () => "output");
            coordinator.PlaybackStateChanged += (_, state) => states.Enqueue(state.IsPlaying);

            await coordinator.StartAsync(path);
            await WaitForAsync(() => states.Count == 2);

            Assert.Equal([true, false], states.ToArray());
        }
        finally
        {
            string? directory = Path.GetDirectoryName(path);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ThrowingStateSubscriberDoesNotInterruptPlaybackLifecycle()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-recording-playback-tests",
            Guid.NewGuid().ToString("N"),
            "call.wav");
        var backend = new FakeAudioBackend { BlockWrites = true };
        var states = new ConcurrentQueue<bool>();
        var observerFailures = new ConcurrentQueue<Exception>();
        try
        {
            using (var writer = new PcmWavFileWriter(path, PcmAudioFormat.Voice8KhzMono16Bit))
                writer.Write(Enumerable.Repeat<short>(1200, 1600).ToArray());

            await using var coordinator = new RecordingPlaybackCoordinator(
                () => backend,
                () => "output",
                observerFailures.Enqueue);
            coordinator.PlaybackStateChanged += (_, _) =>
                throw new InvalidOperationException("observer failed");
            coordinator.PlaybackStateChanged += (_, state) => states.Enqueue(state.IsPlaying);

            await coordinator.StartAsync(path);
            await WaitForAsync(() => coordinator.IsPlaying(path));
            await coordinator.StopAsync();

            Assert.Equal([true, false], states.ToArray());
            Assert.Equal(2, observerFailures.Count);
            Assert.All(observerFailures, failure =>
                Assert.Equal("observer failed", failure.Message));
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
    public async Task ThrowingStoppedSubscriberDoesNotInterruptNaturalCompletion()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-recording-playback-tests",
            Guid.NewGuid().ToString("N"),
            "call.wav");
        var backend = new FakeAudioBackend();
        var states = new ConcurrentQueue<bool>();
        var observerFailures = new ConcurrentQueue<Exception>();
        try
        {
            using (var writer = new PcmWavFileWriter(path, PcmAudioFormat.Voice8KhzMono16Bit))
                writer.Write(Enumerable.Repeat<short>(1200, 160).ToArray());

            await using var coordinator = new RecordingPlaybackCoordinator(
                () => backend,
                () => "output",
                observerFailures.Enqueue);
            coordinator.PlaybackStateChanged += (_, state) =>
            {
                if (!state.IsPlaying)
                    throw new InvalidOperationException("observer failed");
            };
            coordinator.PlaybackStateChanged += (_, state) => states.Enqueue(state.IsPlaying);

            await coordinator.StartAsync(path);
            await WaitForAsync(() => states.Count == 2 && observerFailures.Count == 1);

            Assert.Equal([true, false], states.ToArray());
            Assert.Equal("observer failed", Assert.Single(observerFailures).Message);
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
    public async Task ThrowingObserverFaultHandlerDoesNotInterruptPlaybackLifecycle()
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
                () => "output",
                _ => throw new InvalidOperationException("fault handler failed"));
            coordinator.PlaybackStateChanged += (_, _) =>
                throw new InvalidOperationException("observer failed");

            await coordinator.StartAsync(path);
            await WaitForAsync(() => coordinator.IsPlaying(path));
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
    public async Task StopIfPlayingWaitsForTheMatchingSessionBeforeFileDeletion()
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
            await WaitForAsync(() => coordinator.IsPlaying(path));

            Assert.True(await coordinator.StopIfPlayingAsync(path));
            Assert.False(coordinator.IsPlaying());
            Assert.True(backend.Playback.IsDisposed);

            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            string? directory = Path.GetDirectoryName(path);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task StopIfPlayingDoesNotStopADifferentRecording()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-recording-playback-tests",
            Guid.NewGuid().ToString("N"));
        string playingPath = Path.Combine(root, "playing.wav");
        string otherPath = Path.Combine(root, "other.wav");
        var backend = new FakeAudioBackend { BlockWrites = true };
        try
        {
            using (var writer = new PcmWavFileWriter(playingPath, PcmAudioFormat.Voice8KhzMono16Bit))
                writer.Write(Enumerable.Repeat<short>(1200, 1600).ToArray());
            using (var writer = new PcmWavFileWriter(otherPath, PcmAudioFormat.Voice8KhzMono16Bit))
                writer.Write(Enumerable.Repeat<short>(1200, 1600).ToArray());

            await using var coordinator = new RecordingPlaybackCoordinator(
                () => backend,
                () => "output");
            await coordinator.StartAsync(playingPath);
            await WaitForAsync(() => coordinator.IsPlaying(playingPath));

            Assert.False(await coordinator.StopIfPlayingAsync(otherPath));
            Assert.True(coordinator.IsPlaying(playingPath));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
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

    [Fact]
    public async Task DisposeAsyncIsIdempotent()
    {
        await using var coordinator = new RecordingPlaybackCoordinator(
            () => new FakeAudioBackend(),
            () => "output");

        await coordinator.DisposeAsync();
        await coordinator.DisposeAsync();
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
        public bool DrainCalled { get; private set; }
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

        public ValueTask<int?> DrainAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DrainCalled = true;
            return ValueTask.FromResult<int?>(Frames.Sum(frame => frame.Length));
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
