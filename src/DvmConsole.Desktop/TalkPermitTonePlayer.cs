using DvmConsole.Audio;

namespace DvmConsole.Desktop;

/// <summary>
/// Plays the short local talk-permit indication without sending it over the
/// selected radio channel. The backend and output device are created lazily
/// and reused for the lifetime of the desktop view model.
/// </summary>
public sealed class TalkPermitTonePlayer : IAsyncDisposable
{
    private static readonly TimeSpan ToneDuration = TimeSpan.FromMilliseconds(120);
    private readonly Func<IAudioBackend> createAudioBackend;
    private readonly Func<string?> getOutputDeviceId;
    private readonly SemaphoreSlim gate = new(1, 1);
    private IAudioBackend? audioBackend;
    private IAudioPlayback? playback;
    private string? playbackDeviceId;
    private bool disposed;

    public TalkPermitTonePlayer(
        Func<IAudioBackend> createAudioBackend,
        Func<string?> getOutputDeviceId)
    {
        this.createAudioBackend = createAudioBackend ?? throw new ArgumentNullException(nameof(createAudioBackend));
        this.getOutputDeviceId = getOutputDeviceId ?? throw new ArgumentNullException(nameof(getOutputDeviceId));
    }

    public async Task<AudioDeviceInfo> PlayAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            IAudioBackend backend = audioBackend ??= createAudioBackend();
            AudioDeviceInfo output = ResolveOutputDevice(backend, getOutputDeviceId());
            if (playback is null || !string.Equals(playbackDeviceId, output.Id, StringComparison.OrdinalIgnoreCase))
            {
                if (playback is not null)
                    await playback.DisposeAsync().ConfigureAwait(false);
                playback = backend.OpenPlayback(output, PcmAudioFormat.Voice8KhzMono16Bit);
                playbackDeviceId = output.Id;
            }

            short[] samples = new PcmToneGenerator().GenerateTone(
                frequency: 1200,
                duration: ToneDuration,
                amplitude: 0.40);
            ApplyFade(samples, PcmAudioFormat.Voice8KhzMono16Bit.SampleRate / 100);
            await playback.WriteAsync(samples, cancellationToken).ConfigureAwait(false);
            // Keep the playback stream alive between presses. Both platform
            // backends queue writes asynchronously; disposing a newly opened
            // stream immediately can truncate the complete indication.
            return output;
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
            if (playback is not null)
                await playback.DisposeAsync().ConfigureAwait(false);
            playback = null;
            playbackDeviceId = null;
            audioBackend?.Dispose();
            audioBackend = null;
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
