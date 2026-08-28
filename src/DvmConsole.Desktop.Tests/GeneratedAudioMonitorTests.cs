using DvmConsole.Audio;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class GeneratedAudioMonitorTests
{
    [Fact]
    public async Task LocalOutputFailureDoesNotPreventRadioTransmission()
    {
        bool transmitted = false;

        Exception? monitorFailure = await GeneratedAudioMonitorSession.RunAsync(
            _ => Task.FromException(new IOException("output unavailable")),
            () =>
            {
                transmitted = true;
                return Task.CompletedTask;
            });

        Assert.True(transmitted);
        Assert.IsType<IOException>(monitorFailure);
        Assert.Equal("output unavailable", monitorFailure!.Message);
    }

    [Fact]
    public async Task RadioTransmissionFailureCancelsTheLocalMonitor()
    {
        bool monitorCanceled = false;

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GeneratedAudioMonitorSession.RunAsync(
                async cancellationToken =>
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        monitorCanceled = true;
                        throw;
                    }
                },
                () => Task.FromException(new InvalidOperationException("radio unavailable"))));

        Assert.True(monitorCanceled);
        Assert.Equal("radio unavailable", failure.Message);
    }

    [Fact]
    public async Task PlaysExactGeneratedPcmOnTheConfiguredOutput()
    {
        var backend = new FakeAudioBackend();
        short[] samples = [0, 125, -250, 500, -1_000];
        await using var monitor = new GeneratedAudioMonitor(
            () => backend,
            () => "alternate",
            new ImmediateOutputRouteResolver());

        await monitor.PlayAsync(samples);

        Assert.Equal("alternate", backend.LastOutputDeviceId);
        Assert.Same(PcmAudioFormat.Voice8KhzMono16Bit, backend.LastFormat);
        Assert.Equal(samples, Assert.Single(backend.Playback.Frames));
        Assert.True(backend.Playback.DrainCalled);
        Assert.True(backend.Playback.IsDisposed);
        Assert.True(backend.IsDisposed);
    }

    private sealed class ImmediateOutputRouteResolver : IAudioOutputRouteResolver
    {
        public Task<AudioDeviceInfo> ResolveAsync(
            IAudioBackend backend,
            string? requestedDeviceId,
            AudioOutputRoutePolicy policy,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AudioDeviceInfo output = backend
                .EnumerateDevices(AudioDirection.Output)
                .Single(device => device.Id == requestedDeviceId);
            return Task.FromResult(output);
        }
    }

    private sealed class FakeAudioBackend : IAudioBackend
    {
        public FakePlayback Playback { get; } = new();
        public string? LastOutputDeviceId { get; private set; }
        public PcmAudioFormat? LastFormat { get; private set; }
        public bool IsDisposed { get; private set; }
        public string Name => "generated-monitor-test";

        public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
            => direction == AudioDirection.Output
                ? [
                    new AudioDeviceInfo("default", "Default output", direction, true),
                    new AudioDeviceInfo("alternate", "Alternate output", direction, false)
                ]
                : [new AudioDeviceInfo("input", "Input", direction, true)];

        public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format)
            => throw new NotSupportedException();

        public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format)
        {
            LastOutputDeviceId = device.Id;
            LastFormat = format;
            return Playback;
        }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakePlayback : IAudioPlayback
    {
        private int queuedSamples;
        public List<short[]> Frames { get; } = [];
        public bool DrainCalled { get; private set; }
        public bool IsDisposed { get; private set; }
        public PcmAudioFormat Format { get; } = PcmAudioFormat.Voice8KhzMono16Bit;
        public int? QueuedSamples => queuedSamples;

        public ValueTask WriteAsync(
            ReadOnlyMemory<short> samples,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Frames.Add(samples.ToArray());
            queuedSamples += samples.Length;
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default)
        {
            queuedSamples = 0;
            return ValueTask.CompletedTask;
        }

        public ValueTask<int?> DrainAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DrainCalled = true;
            int consumed = queuedSamples;
            queuedSamples = 0;
            return ValueTask.FromResult<int?>(consumed);
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
