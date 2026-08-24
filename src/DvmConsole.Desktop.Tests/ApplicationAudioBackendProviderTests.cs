using DvmConsole.Audio;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ApplicationAudioBackendProviderTests
{
    private static readonly AudioDeviceInfo Output =
        new("output", "Test output", AudioDirection.Output, true);

    [Fact]
    public async Task AppleModeSharesOnePhysicalPlaybackAcrossBackendClients()
    {
        var state = new FakeAudioState();
        await using var provider = new ApplicationAudioBackendProvider(
            CreateConfiguration(AudioProcessingMode.AppleVoiceProcessing),
            _ => new FakeAudioBackend(state));
        using IAudioBackend firstBackend = provider.CreateBackend();
        using IAudioBackend secondBackend = provider.CreateBackend();
        await using IAudioPlayback first = firstBackend.OpenPlayback(
            Output,
            PcmAudioFormat.Voice8KhzMono16Bit);
        await using IAudioPlayback second = secondBackend.OpenPlayback(
            Output,
            PcmAudioFormat.Voice8KhzMono16Bit);

        Assert.Equal(1, state.OpenPlaybackCalls);

        await first.WriteAsync(new short[160]);
        await second.WriteAsync(new short[160]);
        await WaitForAsync(() => state.PhysicalWriteCalls > 0);
    }

    [Fact]
    public async Task SwitchingToDvmConsoleModeReturnsToDirectPlayback()
    {
        var state = new FakeAudioState();
        await using var provider = new ApplicationAudioBackendProvider(
            CreateConfiguration(AudioProcessingMode.AppleVoiceProcessing),
            _ => new FakeAudioBackend(state));
        using (IAudioBackend appleBackend = provider.CreateBackend())
        {
            await using IAudioPlayback applePlayback = appleBackend.OpenPlayback(
                Output,
                PcmAudioFormat.Voice8KhzMono16Bit);
            Assert.Equal(1, state.OpenPlaybackCalls);
        }

        await provider.ReconfigureAsync(CreateConfiguration(AudioProcessingMode.DvmConsole));
        using IAudioBackend dvmBackend = provider.CreateBackend();
        await using IAudioPlayback dvmPlayback = dvmBackend.OpenPlayback(
            Output,
            PcmAudioFormat.Voice8KhzMono16Bit);

        Assert.IsNotType<SharedOutputAudioBackend>(dvmBackend);
        Assert.Equal(2, state.OpenPlaybackCalls);
        Assert.Equal(1, state.DisposedPlaybackCalls);
    }

    [Fact]
    public async Task SharedRouteRejectsStereoSoReceiveCanUseItsMonoFallback()
    {
        var state = new FakeAudioState();
        await using var provider = new ApplicationAudioBackendProvider(
            CreateConfiguration(AudioProcessingMode.AppleVoiceProcessing),
            _ => new FakeAudioBackend(state));
        using IAudioBackend backend = provider.CreateBackend();

        Assert.Throws<NotSupportedException>(() =>
            backend.OpenPlayback(Output, PcmAudioFormat.Voice8KhzStereo16Bit));

        await using IAudioPlayback playback = backend.OpenPlayback(
            Output,
            PcmAudioFormat.Voice8KhzMono16Bit);
        Assert.Equal(PcmAudioFormat.Voice8KhzMono16Bit, playback.Format);
    }

    [Fact]
    public async Task LocalCueDrainsItsSharedMixerLaneBeforeReturning()
    {
        var state = new FakeAudioState();
        await using var provider = new ApplicationAudioBackendProvider(
            CreateConfiguration(AudioProcessingMode.AppleVoiceProcessing),
            _ => new FakeAudioBackend(state));
        await using var player = new LocalTonePlayer(
            provider.CreateBackend,
            () => Output.Id);

        LocalTonePlaybackResult result = await player.PlayAsync(
            LocalToneCues.ConnectionEstablished);

        Assert.Equal(960, result.ConsumedSamples);
        Assert.True(state.PhysicalWriteCalls > 0);
    }

    [Fact]
    public async Task FailedSharedMixerIsReplacedForTheNextPlaybackClient()
    {
        var state = new FakeAudioState { FailNextPhysicalWrite = 1 };
        await using var provider = new ApplicationAudioBackendProvider(
            CreateConfiguration(AudioProcessingMode.AppleVoiceProcessing),
            _ => new FakeAudioBackend(state));
        using IAudioBackend firstBackend = provider.CreateBackend();
        IAudioPlayback failedPlayback = firstBackend.OpenPlayback(
            Output,
            PcmAudioFormat.Voice8KhzMono16Bit);

        await failedPlayback.WriteAsync(new short[160]);
        await WaitForAsync(() => state.DisposedPlaybackCalls > 0);

        using IAudioBackend recoveredBackend = provider.CreateBackend();
        await using IAudioPlayback recoveredPlayback = recoveredBackend.OpenPlayback(
            Output,
            PcmAudioFormat.Voice8KhzMono16Bit);
        await recoveredPlayback.WriteAsync(new short[160]);
        await WaitForAsync(() => state.SuccessfulPhysicalWriteCalls > 0);

        Assert.Equal(2, state.OpenPlaybackCalls);
    }

    private static ApplicationAudioConfiguration CreateConfiguration(AudioProcessingMode mode)
        => new(mode, "default", "default", false);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(5);
        Assert.True(condition());
    }

    private sealed class FakeAudioState
    {
        public int OpenPlaybackCalls;
        public int PhysicalWriteCalls;
        public int SuccessfulPhysicalWriteCalls;
        public int DisposedPlaybackCalls;
        public int FailNextPhysicalWrite;
    }

    private sealed class FakeAudioBackend(FakeAudioState state) : IAudioBackend
    {
        public string Name => "fake";

        public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
            => direction == AudioDirection.Output
                ? [Output]
                : [new AudioDeviceInfo("input", "Test input", direction, true)];

        public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format)
            => throw new NotSupportedException();

        public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format)
        {
            Interlocked.Increment(ref state.OpenPlaybackCalls);
            return new FakePlayback(state, format);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakePlayback(FakeAudioState state, PcmAudioFormat format) : IAudioPlayback
    {
        public PcmAudioFormat Format { get; } = format;

        public ValueTask WriteAsync(
            ReadOnlyMemory<short> samples,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref state.PhysicalWriteCalls);
            if (Interlocked.Exchange(ref state.FailNextPhysicalWrite, 0) != 0)
                throw new IOException("Simulated physical output failure.");
            Interlocked.Increment(ref state.SuccessfulPhysicalWriteCalls);
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref state.DisposedPlaybackCalls);
            return ValueTask.CompletedTask;
        }
    }
}
