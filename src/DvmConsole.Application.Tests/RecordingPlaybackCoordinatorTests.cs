using System.Collections.Concurrent;
using DvmConsole.Application;
using DvmConsole.Audio;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class RecordingPlaybackCoordinatorTests
{
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

        public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
            => direction == AudioDirection.Output
                ? [
                    new AudioDeviceInfo("default", "Default", direction, true),
                    new AudioDeviceInfo("alternate", "Alternate", direction, false)
                ]
                : [];

        public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format)
            => throw new NotSupportedException();

        public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format)
        {
            OpenedDeviceId = device.Id;
            return Playback;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakePlayback : IAudioPlayback
    {
        public List<short[]> Frames { get; } = [];
        public bool DrainCalled { get; private set; }
        public PcmAudioFormat Format { get; } = PcmAudioFormat.Voice8KhzMono16Bit;

        public ValueTask WriteAsync(
            ReadOnlyMemory<short> samples,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Frames.Add(samples.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<int?> DrainAsync(CancellationToken cancellationToken = default)
        {
            DrainCalled = true;
            return ValueTask.FromResult<int?>(Frames.Sum(frame => frame.Length));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
