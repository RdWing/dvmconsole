using DvmConsole.Audio;

namespace DvmConsole.Desktop;

// Plays the short local talk-permit indication without sending it over the
// selected radio channel. Its playback endpoint exists only for the tone so a
// dormant CoreAudio output stream cannot delay the next duplex PTT startup.
public sealed class TalkPermitTonePlayer : IAsyncDisposable
{
    private static readonly TimeSpan ToneDuration = TimeSpan.FromMilliseconds(120);
    private readonly Func<IAudioBackend> createAudioBackend;
    private readonly Func<string?> getOutputDeviceId;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;

    public TalkPermitTonePlayer(
        Func<IAudioBackend> createAudioBackend,
        Func<string?> getOutputDeviceId)
    {
        this.createAudioBackend = createAudioBackend ?? throw new ArgumentNullException(nameof(createAudioBackend));
        this.getOutputDeviceId = getOutputDeviceId ?? throw new ArgumentNullException(nameof(getOutputDeviceId));
    }

    public int? LastQueuedSamples { get; private set; }
    public int? LastConsumedSamples { get; private set; }

    public async Task<AudioDeviceInfo> PlayAsync(
        double frequency = 1200,
        TimeSpan? duration = null,
        double amplitude = 0.40,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            IAudioBackend backend = createAudioBackend();
            try
            {
                AudioDeviceInfo output = ResolveOutputDevice(backend, getOutputDeviceId());
                await using IAudioPlayback playback = backend.OpenPlayback(
                    output,
                    PcmAudioFormat.Voice8KhzMono16Bit);

                short[] samples = new PcmToneGenerator().GenerateTone(
                    frequency,
                    duration ?? ToneDuration,
                    amplitude);
                ApplyFade(samples, PcmAudioFormat.Voice8KhzMono16Bit.SampleRate / 100);
                await playback.WriteAsync(samples, cancellationToken).ConfigureAwait(false);
                LastQueuedSamples = playback.QueuedSamples;
                LastConsumedSamples = await playback.DrainAsync(cancellationToken).ConfigureAwait(false);
                return output;
            }
            finally
            {
                backend.Dispose();
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
                return;
            disposed = true;
        }
        finally
        {
            gate.Release();
            gate.Dispose();
        }
    }

    private static void ApplyFade(short[] samples, int fadeSamples)
    {
        int boundedFade = Math.Min(Math.Max(0, fadeSamples), samples.Length / 2);
        for (int index = 0; index < boundedFade; index++)
        {
            double scale = (double)index / boundedFade;
            samples[index] = (short)Math.Round(samples[index] * scale);
            int tail = samples.Length - index - 1;
            samples[tail] = (short)Math.Round(samples[tail] * scale);
        }
    }

    private static AudioDeviceInfo ResolveOutputDevice(IAudioBackend backend, string? requestedDeviceId)
    {
        IReadOnlyList<AudioDeviceInfo> devices = backend.EnumerateDevices(AudioDirection.Output);
        return devices.FirstOrDefault(device =>
                   !string.IsNullOrWhiteSpace(requestedDeviceId) &&
                   device.Id.Equals(requestedDeviceId, StringComparison.OrdinalIgnoreCase))
               ?? devices.FirstOrDefault(device => device.IsDefault)
               ?? devices.FirstOrDefault()
               ?? throw new InvalidOperationException("No audio output device is available for the talk-permit tone.");
    }
}
