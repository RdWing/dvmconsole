using DvmConsole.Audio;

namespace DvmConsole.Desktop;

/// <summary>
/// Plays the short local talk-permit indication without sending it over the
/// selected radio channel. The backend and output device are created lazily
/// and reused for the lifetime of the desktop view model.
/// </summary>
public sealed class TalkPermitTonePlayer : IAsyncDisposable
{
    private static readonly TimeSpan ToneDuration = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan PlaybackDrainTime = TimeSpan.FromMilliseconds(30);
    private readonly Func<IAudioBackend> createAudioBackend;
    private readonly Func<string?> getOutputDeviceId;
    private readonly SemaphoreSlim gate = new(1, 1);
    private IAudioBackend? audioBackend;
    private bool disposed;

    public TalkPermitTonePlayer(
        Func<IAudioBackend> createAudioBackend,
        Func<string?> getOutputDeviceId)
    {
        this.createAudioBackend = createAudioBackend ?? throw new ArgumentNullException(nameof(createAudioBackend));
        this.getOutputDeviceId = getOutputDeviceId ?? throw new ArgumentNullException(nameof(getOutputDeviceId));
    }

    public async Task PlayAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            IAudioBackend backend = audioBackend ??= createAudioBackend();
            AudioDeviceInfo output = ResolveOutputDevice(backend, getOutputDeviceId());
            await using IAudioPlayback playback = backend.OpenPlayback(
                output,
                PcmAudioFormat.Voice8KhzMono16Bit);
            short[] samples = new PcmToneGenerator().GenerateTone(
                frequency: 1200,
                duration: ToneDuration,
                amplitude: 0.25);
            await playback.WriteAsync(samples, cancellationToken).ConfigureAwait(false);
            // Playback writes are buffered on both supported backends. Flush is
            // intentionally not used here because it means "discard queued
            // audio" for the Windows backend. Keep the stream alive long
            // enough for the short tone to reach the device before disposal.
            await Task.Delay(ToneDuration + PlaybackDrainTime, cancellationToken).ConfigureAwait(false);
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
            audioBackend?.Dispose();
            audioBackend = null;
        }
        finally
        {
            gate.Release();
            gate.Dispose();
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
