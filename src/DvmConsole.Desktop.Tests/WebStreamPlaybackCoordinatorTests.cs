using System.Buffers.Binary;
using System.Text;
using DvmConsole.Audio;
using DvmConsole.Core.Configuration;
using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class WebStreamPlaybackCoordinatorTests
{
    [Fact]
    public async Task DecodesPcmWavAppliesSavedVolumeAndRoutesToOutput()
    {
        var backend = new FakeAudioBackend();
        var stream = new WebStreamViewModel(new WebStreamConfiguration
        {
            Name = "Dispatch stream",
            Url = "https://example.test/dispatch.wav"
        });
        stream.SetInitialVolume(0.5);

        await using (var coordinator = new WebStreamPlaybackCoordinator(
                         () => backend,
                         () => "output",
                         (_, _) => Task.FromResult<Stream>(CreateWav(1600, 10_000)),
                         getStreamOutputDeviceId: _ => "alternate"))
        {
            await coordinator.StartAsync(stream);
            await WaitForAsync(() =>
                backend.AlternatePlayback.Frames.Count > 0 &&
                !coordinator.IsActive(stream) &&
                stream.StatusText == "Ended");

            Assert.Equal("alternate", backend.LastOutputDeviceId);
            Assert.Equal((short)5_000, backend.AlternatePlayback.Frames[0][0]);
            Assert.False(stream.IsFailed);
            Assert.True(stream.StatusText is "Receiving" or "Ended");
            Assert.False(coordinator.IsActive(stream));
        }

        Assert.True(backend.AlternatePlayback.IsDisposed);
        Assert.True(backend.IsDisposed);
    }

    [Fact]
    public async Task RejectsCompressedWavWithExplicitFailureState()
    {
        var backend = new FakeAudioBackend();
        var stream = new WebStreamViewModel(new WebStreamConfiguration
        {
            Name = "Compressed stream",
            Url = "https://example.test/dispatch.mp3"
        });

        await using var coordinator = new WebStreamPlaybackCoordinator(
            () => backend,
            () => "output",
            (_, _) => Task.FromResult<Stream>(CreateWav(1600, 10_000, formatTag: 3)));

        await coordinator.StartAsync(stream);

        Assert.True(stream.IsFailed);
        Assert.StartsWith("Unsupported:", stream.StatusText, StringComparison.Ordinal);
        Assert.False(coordinator.IsActive(stream));
        Assert.True(backend.IsDisposed);
    }

    [Fact]
    public async Task PlaybackStateChangesUseTheUiDispatcher()
    {
        var backend = new FakeAudioBackend();
        var dispatcher = new RecordingUiDispatcher();
        var stream = new WebStreamViewModel(new WebStreamConfiguration
        {
            Name = "Dispatch stream",
            Url = "https://example.test/dispatch.wav"
        });
        bool observedOutsideDispatcher = false;
        stream.PropertyChanged += (_, _) =>
            observedOutsideDispatcher |= !dispatcher.IsDispatching;
        await using var coordinator = new WebStreamPlaybackCoordinator(
            () => backend,
            () => "output",
            (_, _) => Task.FromResult<Stream>(CreateWav(1600, 10_000)),
            createDecoder: null,
            getStreamOutputDeviceId: null,
            uiDispatcher: dispatcher);

        await Task.Run(() => coordinator.StartAsync(stream));
        await WaitForAsync(() => stream.StatusText == "Ended");

        Assert.False(observedOutsideDispatcher);
        Assert.True(dispatcher.InvocationCount >= 3);
    }

    private static MemoryStream CreateWav(int sampleCount, short sample, ushort formatTag = 1)
    {
        byte[] data = new byte[sampleCount * sizeof(short)];
        for (int index = 0; index < sampleCount; index++)
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(index * sizeof(short), sizeof(short)), sample);

        byte[] bytes = new byte[44 + data.Length];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), (uint)(bytes.Length - 8));
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(bytes, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20, 2), formatTag);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24, 4), 8_000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28, 4), 16_000);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34, 2), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(bytes, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40, 4), (uint)data.Length);
        data.CopyTo(bytes, 44);
        return new MemoryStream(bytes, writable: false);
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
            return device.Id == "alternate" ? AlternatePlayback : Playback;
        }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakePlayback : IAudioPlayback
    {
        public List<short[]> Frames { get; } = [];
        public bool IsDisposed { get; private set; }
        public PcmAudioFormat Format { get; } = PcmAudioFormat.Voice8KhzMono16Bit;

        public ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

    private sealed class RecordingUiDispatcher : IUiDispatcher
    {
        private readonly AsyncLocal<bool> isDispatching = new();
        private int invocationCount;

        public bool IsDispatching => isDispatching.Value;
        public int InvocationCount => Volatile.Read(ref invocationCount);

        public bool CheckAccess() => isDispatching.Value;

        public void Post(Action action, bool background = false)
            => Dispatch(action);

        public ValueTask InvokeAsync(Action action)
        {
            Dispatch(action);
            return ValueTask.CompletedTask;
        }

        private void Dispatch(Action action)
        {
            Interlocked.Increment(ref invocationCount);
            bool previous = isDispatching.Value;
            isDispatching.Value = true;
            try
            {
                action();
            }
            finally
            {
                isDispatching.Value = previous;
            }
        }
    }
}
