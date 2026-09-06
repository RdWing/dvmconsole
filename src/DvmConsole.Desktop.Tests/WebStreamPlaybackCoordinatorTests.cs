// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Buffers.Binary;
using System.Text;
using DvmConsole.Application;
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

    [Fact]
    public async Task StopCancelsAConnectionThatHasNotFinishedOpening()
    {
        var backend = new FakeAudioBackend();
        var enteredOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new WebStreamViewModel(new WebStreamConfiguration
        {
            Name = "Slow stream",
            Url = "https://example.test/slow"
        });
        await using var coordinator = new WebStreamPlaybackCoordinator(
            () => backend,
            () => "output",
            async (_, cancellationToken) =>
            {
                enteredOpen.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Stream.Null;
            });

        Task start = coordinator.StartAsync(stream);
        await enteredOpen.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await coordinator.StopAsync(stream).WaitAsync(TimeSpan.FromSeconds(1));
        await start.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(coordinator.IsActive(stream));
        Assert.False(stream.IsActive);
        Assert.Equal("Off", stream.StatusText);
        Assert.True(backend.IsDisposed);
    }

    [Fact]
    public async Task StopDisposesAStalledDecoderBeforeWaitingForItsReadLoop()
    {
        var backend = new FakeAudioBackend();
        var reader = new DisposeReleasedPcmReader();
        WebStreamViewModel stream = CreateStream("Stalled decoder", 1.0);
        await using var coordinator = new WebStreamPlaybackCoordinator(
            () => backend,
            () => "output",
            (_, _) => Task.FromResult<Stream>(Stream.Null),
            (_, _) => Task.FromResult<IAudioPcmStreamReader>(reader));

        await coordinator.StartAsync(stream);
        await reader.ReadStarted.WaitAsync(TimeSpan.FromSeconds(1));

        await coordinator.StopAsync(stream).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(reader.IsDisposed);
        Assert.False(coordinator.IsActive(stream));
        Assert.False(stream.IsActive);
        Assert.False(stream.IsFailed);
        Assert.Equal("Off", stream.StatusText);
    }

    [Fact]
    public async Task DisposeCancelsAConnectionThatHasNotFinishedOpening()
    {
        var backend = new FakeAudioBackend();
        var enteredOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new WebStreamViewModel(new WebStreamConfiguration
        {
            Name = "Slow stream",
            Url = "https://example.test/slow"
        });
        var coordinator = new WebStreamPlaybackCoordinator(
            () => backend,
            () => "output",
            async (_, cancellationToken) =>
            {
                enteredOpen.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Stream.Null;
            });

        Task start = coordinator.StartAsync(stream);
        await enteredOpen.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        await start.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(stream.IsActive);
        Assert.Equal("Off", stream.StatusText);
        Assert.True(backend.IsDisposed);
    }

    [Fact]
    public async Task DisposeInterruptsAnActiveStalledDecoder()
    {
        var backend = new FakeAudioBackend();
        var reader = new DisposeReleasedPcmReader();
        WebStreamViewModel stream = CreateStream("Stalled shutdown", 1.0);
        var coordinator = new WebStreamPlaybackCoordinator(
            () => backend,
            () => "output",
            (_, _) => Task.FromResult<Stream>(Stream.Null),
            (_, _) => Task.FromResult<IAudioPcmStreamReader>(reader));

        await coordinator.StartAsync(stream);
        await reader.ReadStarted.WaitAsync(TimeSpan.FromSeconds(1));

        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(reader.IsDisposed);
        Assert.True(backend.IsDisposed);
    }

    [Fact]
    public async Task ConcurrentDisposeCallersShareTheSameCompletion()
    {
        var backend = new FakeAudioBackend();
        var reader = new DisposeReleasedPcmReader();
        WebStreamViewModel stream = CreateStream("Shared shutdown", 1.0);
        var coordinator = new WebStreamPlaybackCoordinator(
            () => backend,
            () => "output",
            (_, _) => Task.FromResult<Stream>(Stream.Null),
            (_, _) => Task.FromResult<IAudioPcmStreamReader>(reader));

        await coordinator.StartAsync(stream);
        await reader.ReadStarted.WaitAsync(TimeSpan.FromSeconds(1));

        Task first = coordinator.DisposeAsync().AsTask();
        Task second = coordinator.DisposeAsync().AsTask();

        Assert.Same(first, second);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(reader.IsDisposed);
    }

    [Fact]
    public async Task ConcurrentStreamsShareOnePhysicalRouteAndRetainIndependentGain()
    {
        var backend = new FakeAudioBackend();
        var releaseAudio = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var first = CreateStream("Dispatch one", 0.5);
        var second = CreateStream("Dispatch two", 1.0);
        var readers = new Queue<IAudioPcmStreamReader>([
            new GatedPcmReader(releaseAudio.Task, 4_000),
            new GatedPcmReader(releaseAudio.Task, 8_000)
        ]);
        await using var coordinator = new WebStreamPlaybackCoordinator(
            () => backend,
            () => "output",
            (_, _) => Task.FromResult<Stream>(Stream.Null),
            (_, _) => Task.FromResult(readers.Dequeue()));

        // Establish the two independently configured lanes before releasing
        // either reader. Concurrent playback is the behavior under test; a
        // shared Queue is intentionally not used as a concurrent start oracle.
        await coordinator.StartAsync(first);
        await coordinator.StartAsync(second);
        releaseAudio.TrySetResult();

        await WaitForAsync(
            () => backend.Playback.ContainsSample(10_000),
            backend.Playback.DescribeFrames);

        Assert.Equal(1, backend.OpenPlaybackCallCount);
        Assert.True(coordinator.IsActive(first));
        Assert.True(coordinator.IsActive(second));

        await Task.WhenAll(
            coordinator.StopAsync(first),
            coordinator.StopAsync(second));
    }

    [Fact]
    public async Task TenConcurrentStreamsUseOnePhysicalRouteAndStopIndependently()
    {
        var backend = new FakeAudioBackend();
        WebStreamViewModel[] streams = Enumerable.Range(1, 10)
            .Select(index => CreateStream($"Dispatch {index}", 1.0))
            .ToArray();
        await using var coordinator = new WebStreamPlaybackCoordinator(
            () => backend,
            () => "output",
            (_, _) => Task.FromResult<Stream>(Stream.Null),
            (_, _) => Task.FromResult<IAudioPcmStreamReader>(new WaitingPcmReader()));

        await Task.WhenAll(streams.Select(stream => coordinator.StartAsync(stream)));

        Assert.Equal(1, backend.OpenPlaybackCallCount);
        Assert.All(streams, stream => Assert.True(coordinator.IsActive(stream)));

        await coordinator.StopAsync(streams[0]);
        Assert.False(coordinator.IsActive(streams[0]));
        Assert.All(streams[1..], stream => Assert.True(coordinator.IsActive(stream)));

        await Task.WhenAll(streams[1..].Select(stream => coordinator.StopAsync(stream)));
        Assert.All(streams, stream => Assert.False(coordinator.IsActive(stream)));
    }

    [Fact]
    public async Task StreamsOnDifferentDevicesUseIndependentPhysicalRoutes()
    {
        var backend = new FakeAudioBackend();
        WebStreamViewModel primary = CreateStream("Primary route", 1.0);
        WebStreamViewModel alternate = CreateStream("Alternate route", 1.0);
        await using var coordinator = new WebStreamPlaybackCoordinator(
            () => backend,
            () => "output",
            (_, _) => Task.FromResult<Stream>(Stream.Null),
            (_, _) => Task.FromResult<IAudioPcmStreamReader>(new WaitingPcmReader()),
            stream => ReferenceEquals(stream, alternate) ? "alternate" : "output");

        await Task.WhenAll(
            coordinator.StartAsync(primary),
            coordinator.StartAsync(alternate));

        Assert.Equal(2, backend.OpenPlaybackCallCount);
        Assert.True(coordinator.IsActive(primary));
        Assert.True(coordinator.IsActive(alternate));

        await Task.WhenAll(
            coordinator.StopAsync(primary),
            coordinator.StopAsync(alternate));
        Assert.True(backend.Playback.IsDisposed);
        Assert.True(backend.AlternatePlayback.IsDisposed);
    }

    [Fact]
    public async Task FailedSecondStartDoesNotRetireAnExistingStream()
    {
        var backend = new FakeAudioBackend();
        WebStreamViewModel healthy = CreateStream("Healthy stream", 1.0);
        WebStreamViewModel rejected = CreateStream("Rejected stream", 1.0);
        int decoderCount = 0;
        await using var coordinator = new WebStreamPlaybackCoordinator(
            () => backend,
            () => "output",
            (_, _) => Task.FromResult<Stream>(Stream.Null),
            (_, _) => ++decoderCount == 1
                ? Task.FromResult<IAudioPcmStreamReader>(new WaitingPcmReader())
                : Task.FromException<IAudioPcmStreamReader>(
                    new InvalidDataException("invalid stream payload")));

        await coordinator.StartAsync(healthy);
        await coordinator.StartAsync(rejected);

        Assert.True(coordinator.IsActive(healthy));
        Assert.False(coordinator.IsActive(rejected));
        Assert.True(rejected.IsFailed);
        Assert.False(backend.IsDisposed);

        await coordinator.StopAsync(healthy);
    }

    [Fact]
    public async Task SynchronousNativePlaybackStartupDoesNotBlockTheCallingThread()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var backend = new BlockingOpenAudioBackend(entered, release);
        WebStreamViewModel stream = CreateStream("Blocking native open", 1.0);
        await using var coordinator = new WebStreamPlaybackCoordinator(
            () => backend,
            () => "output",
            (_, _) => Task.FromResult<Stream>(Stream.Null),
            (_, _) => Task.FromResult<IAudioPcmStreamReader>(new WaitingPcmReader()));
        Task releaseNativeOpen = Task.Run(() =>
        {
            entered.Wait(TimeSpan.FromSeconds(1));
            Thread.Sleep(500);
            release.Set();
        });

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        Task start = coordinator.StartAsync(stream);
        TimeSpan callDuration = System.Diagnostics.Stopwatch.GetElapsedTime(started);

        Assert.True(entered.Wait(TimeSpan.FromSeconds(1)));
        Assert.True(
            callDuration < TimeSpan.FromMilliseconds(250),
            $"StartAsync held its caller for {callDuration.TotalMilliseconds:0} ms.");
        await start.WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.StopAsync(stream).WaitAsync(TimeSpan.FromSeconds(1));
        await releaseNativeOpen;
    }

    [Fact]
    public async Task SynchronousBackendPreparationDoesNotBlockTheCallingThread()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var backend = new FakeAudioBackend();
        WebStreamViewModel stream = CreateStream("Blocking backend preparation", 1.0);
        await using var coordinator = new WebStreamPlaybackCoordinator(
            () =>
            {
                entered.TrySetResult();
                release.Wait();
                return backend;
            },
            () => "output",
            (_, _) => Task.FromResult<Stream>(Stream.Null),
            (_, _) => Task.FromResult<IAudioPcmStreamReader>(new WaitingPcmReader()));

        // Keep the caller separate from the worker pool. Its invocation must
        // return a task while backend preparation is still explicitly blocked.
        Task<Task> invocation = Task.Factory.StartNew(
            () => coordinator.StartAsync(stream),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        try
        {
            Task start = await invocation.WaitAsync(TimeSpan.FromSeconds(10));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(start.IsCompleted);
        }
        finally
        {
            release.Set();
            await invocation.Unwrap().WaitAsync(TimeSpan.FromSeconds(10));
        }
        await coordinator.StopAsync(stream).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ThrowingStateObserverCannotStrandAnActiveStream()
    {
        var backend = new FakeAudioBackend();
        var descriptor = new WebStreamPlaybackDescriptor(
            WebStreamId.New(),
            "Observer isolation",
            "https://example.test/observer",
            string.Empty,
            string.Empty,
            1,
            "output");
        await using var coordinator = new DvmConsole.Application.WebStreamPlaybackCoordinator(
            () => backend,
            () => "output",
            (_, _) => Task.FromResult<Stream>(Stream.Null),
            (_, _) => Task.FromResult<IAudioPcmStreamReader>(new WaitingPcmReader()),
            _ => ValueTask.FromException(new InvalidOperationException("observer failed")));

        await coordinator.StartAsync(descriptor);

        Assert.True(coordinator.IsActive(descriptor.Id));
        await coordinator.StopAsync(descriptor.Id).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(coordinator.IsActive(descriptor.Id));
    }

    [Fact]
    public async Task DecoderDisposalFailureStillRetiresTheStream()
    {
        var backend = new FakeAudioBackend();
        var descriptor = new WebStreamPlaybackDescriptor(
            WebStreamId.New(),
            "Cleanup isolation",
            "https://example.test/cleanup",
            string.Empty,
            string.Empty,
            1,
            "output");
        var failed = new TaskCompletionSource<WebStreamPlaybackState>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new DvmConsole.Application.WebStreamPlaybackCoordinator(
            () => backend,
            () => "output",
            (_, _) => Task.FromResult<Stream>(Stream.Null),
            (_, _) => Task.FromResult<IAudioPcmStreamReader>(new ThrowingDisposePcmReader()),
            state =>
            {
                if (state.IsFailed)
                    failed.TrySetResult(state);
                return ValueTask.CompletedTask;
            });

        await coordinator.StartAsync(descriptor);
        WebStreamPlaybackState lastState = await failed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(coordinator.IsActive(descriptor.Id));
        Assert.True(lastState.IsFailed);
        Assert.StartsWith("Failed:", lastState.Status, StringComparison.Ordinal);
    }

    private static WebStreamViewModel CreateStream(string name, double volume)
    {
        var stream = new WebStreamViewModel(new WebStreamConfiguration
        {
            Name = name,
            Url = $"https://example.test/{Uri.EscapeDataString(name)}"
        });
        stream.SetInitialVolume(volume);
        return stream;
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

    private static async Task WaitForAsync(
        Func<bool> condition,
        Func<string>? describeFailure = null)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition(), describeFailure?.Invoke());
    }

    private sealed class FakeAudioBackend : IAudioBackend
    {
        public FakePlayback Playback { get; } = new();
        public FakePlayback AlternatePlayback { get; } = new();
        public string? LastOutputDeviceId { get; private set; }
        public bool IsDisposed { get; private set; }
        public int OpenPlaybackCallCount { get; private set; }
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
            OpenPlaybackCallCount++;
            LastOutputDeviceId = device.Id;
            return device.Id == "alternate" ? AlternatePlayback : Playback;
        }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class BlockingOpenAudioBackend(
        ManualResetEventSlim entered,
        ManualResetEventSlim release) : IAudioBackend
    {
        private readonly FakePlayback playback = new();

        public string Name => "blocking fake";

        public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
            => direction == AudioDirection.Output
                ? [new AudioDeviceInfo("output", "Fake output", direction, true)]
                : [new AudioDeviceInfo("input", "Fake input", direction, true)];

        public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format)
            => throw new NotSupportedException();

        public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format)
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(2));
            return playback;
        }

        public void Dispose() { }
    }

    private sealed class FakePlayback : IAudioPlayback
    {
        private readonly object sync = new();
        public List<short[]> Frames { get; } = [];
        public bool IsDisposed { get; private set; }
        public PcmAudioFormat Format { get; } = PcmAudioFormat.Voice8KhzMono16Bit;

        public ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
                Frames.Add(samples.ToArray());
            return ValueTask.CompletedTask;
        }

        public bool ContainsSample(short sample)
        {
            lock (sync)
                return Frames.Any(frame => frame.Contains(sample));
        }

        public string DescribeFrames()
        {
            lock (sync)
            {
                return $"Frames={Frames.Count}; values={string.Join(",", Frames.SelectMany(frame => frame).Distinct().Order().Take(20))}";
            }
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class GatedPcmReader(Task release, short sample) : IAudioPcmStreamReader
    {
        private bool emitted;

        public int SampleRate => 8_000;

        public async ValueTask<int> ReadSamplesAsync(
            Memory<short> destination,
            CancellationToken cancellationToken = default)
        {
            if (!emitted)
            {
                await release.WaitAsync(cancellationToken);
                emitted = true;
                int count = Math.Min(destination.Length, 1_600);
                destination.Span[..count].Fill(sample);
                return count;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class WaitingPcmReader : IAudioPcmStreamReader
    {
        public int SampleRate => 8_000;

        public async ValueTask<int> ReadSamplesAsync(
            Memory<short> destination,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DisposeReleasedPcmReader : IAudioPcmStreamReader
    {
        private readonly TaskCompletionSource<int> readCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource readStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int SampleRate => 8_000;
        public Task ReadStarted => readStarted.Task;
        public bool IsDisposed { get; private set; }

        public async ValueTask<int> ReadSamplesAsync(
            Memory<short> destination,
            CancellationToken cancellationToken = default)
        {
            readStarted.TrySetResult();
            return await readCompletion.Task.ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            readCompletion.TrySetException(new ObjectDisposedException(nameof(DisposeReleasedPcmReader)));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingDisposePcmReader : IAudioPcmStreamReader
    {
        public int SampleRate => 8_000;

        public ValueTask<int> ReadSamplesAsync(
            Memory<short> destination,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(0);

        public ValueTask DisposeAsync()
            => ValueTask.FromException(new IOException("decoder cleanup failed"));
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
