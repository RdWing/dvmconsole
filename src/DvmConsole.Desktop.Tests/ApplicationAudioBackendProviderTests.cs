// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Audio;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ApplicationAudioBackendProviderTests
{
    [Fact]
    public async Task FinalFenceStopsEveryTrackedEndpointDespiteAnEarlierFailure()
    {
        var capture = new ImmediateCapture(throwOnStop: true);
        var playback = new ImmediatePlayback();
        var backend = new FakeBackend(capture, playback);
        await using var provider = CreateProvider(backend);
        using IAudioBackend tracked = provider.CreateBackend();
        _ = tracked.OpenCapture(backend.Input, PcmAudioFormat.Voice8KhzMono16Bit);
        _ = tracked.OpenPlayback(backend.Output, PcmAudioFormat.Voice8KhzMono16Bit);

        IReadOnlyList<Exception> failures = provider.StopImmediately();

        Assert.Equal(1, capture.StopCount);
        Assert.Equal(1, playback.StopCount);
        Assert.Single(failures);
    }

    [Fact]
    public async Task EndpointOpenedAfterFinalFenceIsStoppedBeforeItEscapes()
    {
        var capture = new ImmediateCapture();
        var playback = new ImmediatePlayback();
        var backend = new FakeBackend(capture, playback);
        await using var provider = CreateProvider(backend);
        using IAudioBackend tracked = provider.CreateBackend();

        provider.StopImmediately();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            IAudioPlayback opened = tracked.OpenPlayback(
                backend.Output,
                PcmAudioFormat.Voice8KhzMono16Bit);
            await opened.DisposeAsync();
        });

        Assert.Equal(1, playback.StopCount);
    }

    [Fact]
    public async Task TrackedBackendPreservesDefaultDeviceIdentity()
    {
        var backend = new FakeBackend(new ImmediateCapture(), new ImmediatePlayback());
        await using var provider = CreateProvider(backend);
        using IAudioBackend tracked = provider.CreateBackend();

        var identity = Assert.IsType<IDefaultAudioDeviceIdentityProvider>(tracked, exactMatch: false);

        Assert.Equal(backend.Output.Id, identity.GetDefaultDeviceIdentity(AudioDirection.Output));
    }

    private static ApplicationAudioBackendProvider CreateProvider(IAudioBackend backend)
        => new(
            new ApplicationAudioConfiguration(
                AudioProcessingMode.DvmConsole,
                "input",
                "output"),
            _ => backend);

    private sealed class FakeBackend(
        IAudioCapture capture,
        IAudioPlayback playback) :
        IAudioBackend,
        IDefaultAudioDeviceIdentityProvider
    {
        public AudioDeviceInfo Input { get; } = new(
            "input",
            "Input",
            AudioDirection.Input,
            true);
        public AudioDeviceInfo Output { get; } = new(
            "output",
            "Output",
            AudioDirection.Output,
            true);
        public string Name => "fake";

        public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
            => direction == AudioDirection.Input ? [Input] : [Output];

        public string? GetDefaultDeviceIdentity(AudioDirection direction)
            => direction == AudioDirection.Input ? Input.Id : Output.Id;

        public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format)
            => capture;

        public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format)
            => playback;

        public void Dispose()
        {
        }
    }

    private sealed class ImmediateCapture(bool throwOnStop = false) :
        IAudioCapture,
        IImmediateAudioStop
    {
        public event EventHandler<PcmSamplesEventArgs>? SamplesAvailable
        {
            add { }
            remove { }
        }

        public PcmAudioFormat Format => PcmAudioFormat.Voice8KhzMono16Bit;
        public bool IsRunning => false;
        public int StopCount { get; private set; }

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public void StopImmediately()
        {
            StopCount++;
            if (throwOnStop)
                throw new IOException("simulated immediate-stop failure");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ImmediatePlayback : IAudioPlayback, IImmediateAudioStop
    {
        public PcmAudioFormat Format => PcmAudioFormat.Voice8KhzMono16Bit;
        public int StopCount { get; private set; }

        public ValueTask WriteAsync(
            ReadOnlyMemory<short> samples,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask FlushAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public void StopImmediately() => StopCount++;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
