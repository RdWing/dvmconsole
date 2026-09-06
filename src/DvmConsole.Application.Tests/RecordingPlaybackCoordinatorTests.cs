// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Concurrent;
using DvmConsole.Application;
using DvmConsole.Audio;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class RecordingPlaybackCoordinatorTests
{
    [Fact]
    public async Task StartupTimingSeparatesSlowNotificationsFromTheAudioWrite()
    {
        var id = RecordingId.New();
        var clock = new ManualStartupClock();
        var backend = new FakeAudioBackend();
        backend.Playback.OnWrite = () => clock.Advance(7);
        var observed = new TaskCompletionSource<RecordingPlaybackStartupMetrics>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new RecordingPlaybackCoordinator(
            new FakeRecordingStore(id, CreateWave([1200, 1200])),
            () => backend, () => "default", startupObserver: metrics => observed.TrySetResult(metrics),
            timeProvider: clock);
        coordinator.PlaybackStateChanged += (_, state) =>
        {
            if (state.IsPlaying) clock.Advance(400);
        };
        await coordinator.StartAsync(id);
        var timing = await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromMilliseconds(400), timing.NotificationDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(400), timing.NotificationCompleted);
        Assert.Equal(TimeSpan.FromMilliseconds(7), timing.FirstWriteDuration);
        Assert.Equal(TimeSpan.Zero, timing.FirstWritePacingWait);
        Assert.Equal(TimeSpan.FromMilliseconds(407), timing.FirstOutput);
    }

    private sealed class ManualStartupClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(int milliseconds) => ticks += TimeSpan.FromMilliseconds(milliseconds).Ticks;
    }

    [Fact]
    public async Task SynchronousPlaybackStartupDoesNotRunOnCallingThread()
    {
        RecordingId id = RecordingId.New();
        var store = new FakeRecordingStore(id, CreateWave([1200, 1200]));
        var backend = new FakeAudioBackend();
        await using var coordinator = new RecordingPlaybackCoordinator(store, () => backend, () => "default");
        var startup = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        int callerThreadId = 0;
        var caller = new Thread(() =>
        {
            callerThreadId = Environment.CurrentManagedThreadId;
            try
            {
                startup.SetResult(coordinator.StartAsync(id));
            }
            catch (Exception exception)
            {
                startup.SetException(exception);
            }
        });

        caller.Start();
        Task playbackStarted = await startup.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await playbackStarted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(callerThreadId, backend.DiscoveryThreadId);
        Assert.NotEqual(callerThreadId, backend.OutputOpenThreadId);
    }

    [Fact]
    public async Task CanceledStopCannotReleaseBackendWhilePlaybackIsRetiring()
    {
        RecordingId id = RecordingId.New();
        var store = new FakeRecordingStore(id, CreateWave([1200, 1200]));
        var backend = new FakeAudioBackend();
        backend.Playback.BlockDrain = true;
        var coordinator = new RecordingPlaybackCoordinator(store, () => backend, () => "default");
        await coordinator.StartAsync(id);
        await backend.Playback.DrainEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        Task stop = coordinator.StopAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
        Task reset = coordinator.ResetAudioBackendAsync();
        Task dispose = coordinator.DisposeAsync().AsTask();
        Assert.False(reset.IsCompleted);
        Assert.False(dispose.IsCompleted);
        Assert.False(backend.IsDisposed);
        backend.Playback.DrainRelease.SetResult();
        await Task.WhenAll(reset, dispose).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(backend.IsDisposed);
    }

    [Fact]
    public async Task PlaysStoreStreamByStableIdWithoutAFilePath()
    {
        RecordingId recordingId = RecordingId.New();
        var store = new FakeRecordingStore(
            recordingId,
            CreateWave(Enumerable.Repeat<short>(1200, 160).ToArray()));
        var backend = new FakeAudioBackend();
        var states = new ConcurrentQueue<RecordingPlaybackStateChangedEventArgs>();

        await using var coordinator = new RecordingPlaybackCoordinator(
            store,
            () => backend,
            () => "alternate");
        coordinator.PlaybackStateChanged += (_, state) => states.Enqueue(state);

        await coordinator.StartAsync(recordingId);
        await WaitForAsync(() => states.Count == 2);

        Assert.Equal(recordingId, store.OpenedId);
        Assert.Equal("alternate", backend.OpenedDeviceId);
        Assert.Equal((short)1200, Assert.Single(backend.Playback.Frames)[0]);
        Assert.True(backend.Playback.DrainCalled);
        Assert.Collection(
            states,
            state =>
            {
                Assert.Equal(recordingId, state.RecordingId);
                Assert.True(state.IsPlaying);
            },
            state =>
            {
                Assert.Equal(recordingId, state.RecordingId);
                Assert.False(state.IsPlaying);
            });
    }

    private static byte[] CreateWave(IReadOnlyCollection<short> samples)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            int dataLength = samples.Count * sizeof(short);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataLength);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(8000);
            writer.Write(16000);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(dataLength);
            foreach (short sample in samples)
                writer.Write(sample);
        }
        return stream.ToArray();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(5);
        Assert.True(condition());
    }

    private sealed class FakeRecordingStore(RecordingId id, byte[] content) : IRecordingStore
    {
        public RecordingId? OpenedId { get; private set; }

        public ValueTask<IRecordingWriteHandle> CreateAsync(
            CallId callId,
            ChannelId channelId,
            DateTimeOffset startedAt,
            string mediaType,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<Stream> OpenReadAsync(
            RecordingId recordingId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(id, recordingId);
            OpenedId = recordingId;
            return ValueTask.FromResult<Stream>(new MemoryStream(content, writable: false));
        }

        public async IAsyncEnumerable<RecordingDescriptor> ListAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FakeAudioBackend : IAudioBackend
    {
        public FakePlayback Playback { get; } = new();
        public string? OpenedDeviceId { get; private set; }
        public string Name => "fake";
        public bool IsDisposed { get; private set; }
        public int DiscoveryThreadId { get; private set; }
        public int OutputOpenThreadId { get; private set; }

        public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
        {
            DiscoveryThreadId = Environment.CurrentManagedThreadId;
            return direction == AudioDirection.Output
                ? [
                    new AudioDeviceInfo("default", "Default", direction, true),
                    new AudioDeviceInfo("alternate", "Alternate", direction, false)
                ]
                : [];
        }

        public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format)
            => throw new NotSupportedException();

        public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format)
        {
            OutputOpenThreadId = Environment.CurrentManagedThreadId;
            OpenedDeviceId = device.Id;
            return Playback;
        }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    private sealed class FakePlayback : IAudioPlayback
    {
        public List<short[]> Frames { get; } = [];
        public Action? OnWrite { get; set; }
        public bool DrainCalled { get; private set; }
        public bool BlockDrain { get; set; }
        public TaskCompletionSource DrainEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DrainRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PcmAudioFormat Format { get; } = PcmAudioFormat.Voice8KhzMono16Bit;

        public ValueTask WriteAsync(
            ReadOnlyMemory<short> samples,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OnWrite?.Invoke();
            Frames.Add(samples.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public async ValueTask<int?> DrainAsync(CancellationToken cancellationToken = default)
        {
            DrainCalled = true;
            DrainEntered.TrySetResult();
            if (BlockDrain)
                await DrainRelease.Task;
            return Frames.Sum(frame => frame.Length);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
