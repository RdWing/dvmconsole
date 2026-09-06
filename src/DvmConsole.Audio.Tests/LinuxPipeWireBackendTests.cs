// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;
using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class LinuxPipeWireBackendTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedPlaybackStartupDestroysStreamAndCanReopen(bool throws)
    {
        using var api = new FakePipeWireApi { StartResult = -1, ThrowOnStart = throws };
        using var backend = new LinuxPipeWireBackend(api);
        AudioDeviceInfo output = Assert.Single(backend.EnumerateDevices(AudioDirection.Output));
        Assert.ThrowsAny<Exception>(() => backend.OpenPlayback(output, PcmAudioFormat.Voice8KhzMono16Bit));
        Assert.Equal(1, api.DestroyedStreams);
        api.StartResult = 0;
        api.ThrowOnStart = false;
        await using (IAudioPlayback playback = backend.OpenPlayback(output, PcmAudioFormat.Voice8KhzMono16Bit))
            Assert.Equal(1, api.DestroyedStreams);
        Assert.Equal(2, api.DestroyedStreams);
    }

    [Fact]
    public void EnumeratesThePipeWireDefaultRoutes()
    {
        using var api = new FakePipeWireApi();
        using var backend = new LinuxPipeWireBackend(api);

        AudioDeviceInfo input = Assert.Single(backend.EnumerateDevices(AudioDirection.Input));
        AudioDeviceInfo output = Assert.Single(backend.EnumerateDevices(AudioDirection.Output));

        Assert.Equal("default", input.Id);
        Assert.Equal("PipeWire default input", input.Name);
        Assert.Equal(AudioDirection.Input, input.Direction);
        Assert.True(input.IsDefault);
        Assert.Null(input.IsBluetooth);
        Assert.Equal("default", output.Id);
        Assert.Equal("PipeWire default output", output.Name);
        Assert.Equal(AudioDirection.Output, output.Direction);
        Assert.Equal("default", backend.GetDefaultDeviceIdentity(AudioDirection.Output));
    }

    [Fact]
    public void RejectsAnEndpointWithTheWrongDirection()
    {
        using var api = new FakePipeWireApi();
        using var backend = new LinuxPipeWireBackend(api);
        var output = new AudioDeviceInfo(
            "default",
            "PipeWire default output",
            AudioDirection.Output,
            true);

        Assert.Throws<ArgumentException>(() =>
            backend.OpenCapture(output, PcmAudioFormat.Voice8KhzMono16Bit));
    }

    [Fact]
    public async Task PlaybackRetriesPartialNativeWritesAndExposesDiagnostics()
    {
        using var api = new FakePipeWireApi
        {
            MaximumWriteSize = 2,
            QueuedSamples = 80,
            StarvedSamples = 40,
            PendingStarvedSamples = 20,
            OutputCallbackCount = 7
        };
        using var backend = new LinuxPipeWireBackend(api);
        AudioDeviceInfo output = Assert.Single(backend.EnumerateDevices(AudioDirection.Output));
        await using IAudioPlayback playback = backend.OpenPlayback(
            output,
            PcmAudioFormat.Voice8KhzMono16Bit);

        await playback.WriteAsync(new short[] { 1, 2, 3, 4, 5 });

        Assert.Equal([1, 2, 3, 4, 5], api.WrittenSamples);
        Assert.Equal(80, playback.QueuedSamples);
        var continuity = Assert.IsAssignableFrom<IAudioPlaybackContinuityDiagnostics>(playback);
        Assert.Equal(TimeSpan.FromMilliseconds(5), continuity.StarvedDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(2.5), continuity.PendingStarvedDuration);
        Assert.Equal(
            7,
            Assert.IsAssignableFrom<IAudioPlaybackCallbackDiagnostics>(playback).OutputCallbackCount);
        continuity.EndExpectedPlayback();
        Assert.True(api.EndPlaybackContinuityCalled);
    }

    [Fact]
    public async Task CaptureBlocksForNativeReadinessAndWakeStopsPromptly()
    {
        using var api = new FakePipeWireApi();
        using var backend = new LinuxPipeWireBackend(api);
        AudioDeviceInfo input = Assert.Single(backend.EnumerateDevices(AudioDirection.Input));
        await using IAudioCapture capture = backend.OpenCapture(
            input,
            PcmAudioFormat.Voice8KhzMono16Bit);
        var received = new TaskCompletionSource<short[]>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        capture.SamplesAvailable += (_, args) => received.TrySetResult(args.Samples.ToArray());

        await capture.StartAsync();
        api.QueueCaptureSamples([1, 2, 3, 4]);

        Assert.Equal([1, 2, 3, 4], await received.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        await capture.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(api.WaitForCaptureCalls > 0);
        Assert.True(api.WakeCaptureCalls > 0);
    }

    [Fact]
    public void PublicBackendConstructorRejectsNonLinuxBeforeLoadingTheNativeLibrary()
    {
        if (OperatingSystem.IsLinux())
            return;

        PlatformNotSupportedException exception = Assert.Throws<PlatformNotSupportedException>(() =>
            new LinuxPipeWireBackend("/path/that/does/not/exist/libdvmaudio-pipewire.so"));

        Assert.Contains("requires Linux", exception.Message, StringComparison.Ordinal);
    }

    private sealed class FakePipeWireApi : IPipeWireApi
    {
        private readonly object captureSync = new();
        private readonly Queue<short[]> captureSamples = [];
        private readonly ManualResetEventSlim captureReady = new(false);
        private int disposed;
        public int MaximumWriteSize { get; init; } = int.MaxValue;
        public uint QueuedSamples { get; init; }
        public ulong StarvedSamples { get; init; }
        public ulong PendingStarvedSamples { get; init; }
        public ulong OutputCallbackCount { get; init; }
        public List<short> WrittenSamples { get; } = [];
        public bool EndPlaybackContinuityCalled { get; private set; }
        public int WaitForCaptureCalls;
        public int WakeCaptureCalls;
        public int StartResult { get; set; }
        public bool ThrowOnStart { get; set; }
        public int DestroyedStreams;

        public void QueueCaptureSamples(short[] samples)
        {
            lock (captureSync)
                captureSamples.Enqueue(samples);
            captureReady.Set();
        }

        public int GetDeviceCount(int input, out int count)
        {
            count = 1;
            return 0;
        }

        public int GetDevice(
            int input,
            int index,
            out ulong deviceId,
            byte[] name,
            int capacity,
            out int isDefault)
        {
            deviceId = 0;
            isDefault = 1;
            string displayName = input != 0
                ? "PipeWire default input"
                : "PipeWire default output";
            byte[] encoded = System.Text.Encoding.UTF8.GetBytes(displayName);
            encoded.CopyTo(name, 0);
            return 0;
        }

        public int IsBluetoothDevice(ulong deviceId) => -1;

        public SafePipeWireStreamHandle CreateStream(
            ulong deviceId,
            int input,
            int sampleRate,
            int channels,
            int bitsPerSample)
            => new(new IntPtr(1), _ => Interlocked.Increment(ref DestroyedStreams));

        public int StartStream(SafePipeWireStreamHandle stream)
            => ThrowOnStart ? throw new IOException("Injected native startup failure") : StartResult;
        public int StopStream(SafePipeWireStreamHandle stream) => 0;
        public int GetSampleRate(SafePipeWireStreamHandle stream) => 8_000;
        public int ReadStream(SafePipeWireStreamHandle stream, short[] samples, int capacity)
        {
            lock (captureSync)
            {
                if (!captureSamples.TryDequeue(out short[]? available))
                {
                    captureReady.Reset();
                    return 0;
                }
                int count = Math.Min(capacity, available.Length);
                available.AsSpan(0, count).CopyTo(samples);
                if (captureSamples.Count == 0)
                    captureReady.Reset();
                return count;
            }
        }

        public int WaitForCapture(SafePipeWireStreamHandle stream, int timeoutMilliseconds)
        {
            Interlocked.Increment(ref WaitForCaptureCalls);
            return captureReady.Wait(timeoutMilliseconds) ? 1 : 0;
        }

        public void WakeCapture(SafePipeWireStreamHandle stream)
        {
            Interlocked.Increment(ref WakeCaptureCalls);
            captureReady.Set();
        }

        public int WriteStream(SafePipeWireStreamHandle stream, short[] samples, int count)
        {
            int accepted = Math.Min(count, MaximumWriteSize);
            WrittenSamples.AddRange(samples.AsSpan(0, accepted).ToArray());
            return accepted;
        }

        public uint GetQueuedSamples(SafePipeWireStreamHandle stream) => QueuedSamples;
        public ulong GetStarvedSamples(SafePipeWireStreamHandle stream) => StarvedSamples;
        public ulong GetPendingStarvedSamples(SafePipeWireStreamHandle stream) => PendingStarvedSamples;
        public ulong GetOutputCallbackCount(SafePipeWireStreamHandle stream) => OutputCallbackCount;

        public void EndPlaybackContinuity(SafePipeWireStreamHandle stream)
            => EndPlaybackContinuityCalled = true;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                captureReady.Dispose();
        }
    }
}
