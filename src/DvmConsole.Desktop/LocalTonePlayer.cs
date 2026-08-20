using DvmConsole.Audio;

namespace DvmConsole.Desktop;

internal sealed record AudioOutputRoutePolicy
{
    public AudioOutputRoutePolicy(int maximumAttempts, TimeSpan retryInterval)
    {
        if (maximumAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        if (retryInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryInterval));

        MaximumAttempts = maximumAttempts;
        RetryInterval = retryInterval;
    }

    public int MaximumAttempts { get; }
    public TimeSpan RetryInterval { get; }

    public static AudioOutputRoutePolicy TransientRouteChanges { get; } =
        new(6, TimeSpan.FromMilliseconds(50));
}

internal sealed record LocalTonePlaybackRequest(
    double Frequency,
    TimeSpan ToneDuration,
    double Amplitude,
    TimeSpan OutputWarmupDuration,
    TimeSpan TailSilenceDuration,
    AudioOutputRoutePolicy RoutePolicy);

internal sealed record LocalTonePlaybackResult(
    AudioDeviceInfo Output,
    int? QueuedSamples,
    int? ConsumedSamples);

internal static class LocalToneCues
{
    public static LocalTonePlaybackRequest TalkPermit { get; } = new(
        Frequency: 1200,
        ToneDuration: TimeSpan.FromMilliseconds(80),
        Amplitude: 0.40,
        OutputWarmupDuration: TimeSpan.FromMilliseconds(200),
        TailSilenceDuration: TimeSpan.FromMilliseconds(40),
        RoutePolicy: AudioOutputRoutePolicy.TransientRouteChanges);

    public static LocalTonePlaybackRequest ConnectionEstablished { get; } = new(
        Frequency: 1500,
        ToneDuration: TimeSpan.FromMilliseconds(80),
        Amplitude: 0.25,
        OutputWarmupDuration: TimeSpan.Zero,
        TailSilenceDuration: TimeSpan.FromMilliseconds(40),
        RoutePolicy: AudioOutputRoutePolicy.TransientRouteChanges);

    public static LocalTonePlaybackRequest ConnectionLost { get; } = new(
        Frequency: 500,
        ToneDuration: TimeSpan.FromMilliseconds(160),
        Amplitude: 0.25,
        OutputWarmupDuration: TimeSpan.Zero,
        TailSilenceDuration: TimeSpan.FromMilliseconds(40),
        RoutePolicy: AudioOutputRoutePolicy.TransientRouteChanges);
}

internal interface IAudioOutputRouteResolver
{
    Task<AudioDeviceInfo> ResolveAsync(
        IAudioBackend backend,
        string? requestedDeviceId,
        AudioOutputRoutePolicy policy,
        CancellationToken cancellationToken);
}

internal sealed class AudioOutputRouteResolver : IAudioOutputRouteResolver
{
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;

    public AudioOutputRouteResolver(Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        this.delayAsync = delayAsync ?? ((delay, cancellationToken) => Task.Delay(delay, cancellationToken));
    }

    public async Task<AudioDeviceInfo> ResolveAsync(
        IAudioBackend backend,
        string? requestedDeviceId,
        AudioOutputRoutePolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(policy);
        bool hasSpecificRequest = !string.IsNullOrWhiteSpace(requestedDeviceId) &&
            !requestedDeviceId.Equals("default", StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<AudioDeviceInfo> devices = [];
        for (int attempt = 0; attempt < policy.MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            devices = backend.EnumerateDevices(AudioDirection.Output);
            AudioDeviceInfo? requested = devices.FirstOrDefault(device =>
                hasSpecificRequest &&
                device.Id.Equals(requestedDeviceId, StringComparison.OrdinalIgnoreCase));
            if (requested is not null)
                return requested;
            if (!hasSpecificRequest || attempt + 1 == policy.MaximumAttempts)
                break;

            // CoreAudio can briefly omit the Bluetooth output while the
            // selected headset changes profile. Do not immediately select an
            // unrelated fallback device during that transition.
            await delayAsync(policy.RetryInterval, cancellationToken).ConfigureAwait(false);
        }

        return devices.FirstOrDefault(device => device.IsDefault)
            ?? devices.FirstOrDefault()
            ?? throw new InvalidOperationException("No audio output device is available for the local cue.");
    }
}

// Plays one explicitly described local cue. Cue meaning and route-stability
// policy live in LocalToneCues; this class owns only rendering and playback.
internal sealed class LocalTonePlayer : IAsyncDisposable
{
    private readonly Func<IAudioBackend> createAudioBackend;
    private readonly Func<string?> getOutputDeviceId;
    private readonly IAudioOutputRouteResolver outputRouteResolver;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;

    public LocalTonePlayer(
        Func<IAudioBackend> createAudioBackend,
        Func<string?> getOutputDeviceId,
        IAudioOutputRouteResolver? outputRouteResolver = null)
    {
        this.createAudioBackend = createAudioBackend ?? throw new ArgumentNullException(nameof(createAudioBackend));
        this.getOutputDeviceId = getOutputDeviceId ?? throw new ArgumentNullException(nameof(getOutputDeviceId));
        this.outputRouteResolver = outputRouteResolver ?? new AudioOutputRouteResolver();
    }

    public async Task<LocalTonePlaybackResult> PlayAsync(
        LocalTonePlaybackRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using IAudioBackend backend = createAudioBackend();
            AudioDeviceInfo output = await outputRouteResolver.ResolveAsync(
                backend,
                getOutputDeviceId(),
                request.RoutePolicy,
                cancellationToken).ConfigureAwait(false);
            await using IAudioPlayback playback = backend.OpenPlayback(
                output,
                PcmAudioFormat.Voice8KhzMono16Bit);

            var generator = new PcmToneGenerator();
            int? warmupQueued = null;
            int? warmupConsumed = null;
            if (request.OutputWarmupDuration > TimeSpan.Zero)
            {
                // The microphone can be ready before a newly opened Bluetooth
                // output becomes audible. Render and drain silent pre-roll so
                // the actual cue begins only after output callbacks are active.
                short[] warmup = generator.GenerateSilence(request.OutputWarmupDuration);
                await playback.WriteAsync(warmup, cancellationToken).ConfigureAwait(false);
                warmupQueued = playback.QueuedSamples;
                warmupConsumed = await playback.DrainAsync(cancellationToken).ConfigureAwait(false);
            }

            short[] tone = generator.GenerateTone(
                request.Frequency,
                request.ToneDuration,
                request.Amplitude);
            ApplyFade(tone, PcmAudioFormat.Voice8KhzMono16Bit.SampleRate / 200);
            short[] samples =
            [
                .. tone,
                .. generator.GenerateSilence(request.TailSilenceDuration)
            ];
            await playback.WriteAsync(samples, cancellationToken).ConfigureAwait(false);
            int? cueQueued = playback.QueuedSamples;
            int? cueConsumed = await playback.DrainAsync(cancellationToken).ConfigureAwait(false);
            return new LocalTonePlaybackResult(
                output,
                AddSampleCounts(warmupQueued, cueQueued),
                AddSampleCounts(warmupConsumed, cueConsumed));
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

    private static int? AddSampleCounts(int? first, int? second)
        => first is null ? second : second is null ? first : checked(first.Value + second.Value);
}
