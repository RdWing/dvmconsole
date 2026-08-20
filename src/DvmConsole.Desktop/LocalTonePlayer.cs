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
        new(12, TimeSpan.FromMilliseconds(50));
}

internal sealed record LocalTonePlaybackRequest(
    double Frequency,
    TimeSpan ToneDuration,
    double Amplitude,
    TimeSpan OutputWarmupDuration,
    TimeSpan TailSilenceDuration,
    TimeSpan OutputPostDrainDuration,
    int MaximumPlaybackAttempts,
    AudioOutputRoutePolicy RoutePolicy);

internal sealed record LocalTonePlaybackResult(
    AudioDeviceInfo Output,
    int? QueuedSamples,
    int? ConsumedSamples,
    int Attempts);

internal static class LocalToneCues
{
    public static LocalTonePlaybackRequest TalkPermit { get; } = new(
        Frequency: 1200,
        ToneDuration: TimeSpan.FromMilliseconds(80),
        Amplitude: 0.40,
        OutputWarmupDuration: TimeSpan.FromMilliseconds(300),
        TailSilenceDuration: TimeSpan.FromMilliseconds(80),
        OutputPostDrainDuration: TimeSpan.FromMilliseconds(200),
        MaximumPlaybackAttempts: 3,
        RoutePolicy: AudioOutputRoutePolicy.TransientRouteChanges);

    public static LocalTonePlaybackRequest ConnectionEstablished { get; } = new(
        Frequency: 1500,
        ToneDuration: TimeSpan.FromMilliseconds(80),
        Amplitude: 0.25,
        OutputWarmupDuration: TimeSpan.Zero,
        TailSilenceDuration: TimeSpan.FromMilliseconds(40),
        OutputPostDrainDuration: TimeSpan.Zero,
        MaximumPlaybackAttempts: 2,
        RoutePolicy: AudioOutputRoutePolicy.TransientRouteChanges);

    public static LocalTonePlaybackRequest ConnectionLost { get; } = new(
        Frequency: 500,
        ToneDuration: TimeSpan.FromMilliseconds(160),
        Amplitude: 0.25,
        OutputWarmupDuration: TimeSpan.Zero,
        TailSilenceDuration: TimeSpan.FromMilliseconds(40),
        OutputPostDrainDuration: TimeSpan.Zero,
        MaximumPlaybackAttempts: 2,
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
        AudioDeviceInfo? previousCandidate = null;
        int stableObservations = 0;
        for (int attempt = 0; attempt < policy.MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<AudioDeviceInfo> devices = backend.EnumerateDevices(AudioDirection.Output);
            AudioDeviceInfo? candidate = hasSpecificRequest
                ? devices.FirstOrDefault(device =>
                    device.Id.Equals(requestedDeviceId, StringComparison.OrdinalIgnoreCase))
                : devices.FirstOrDefault(device => device.IsDefault)
                    ?? devices.FirstOrDefault();

            if (candidate is not null &&
                previousCandidate is not null &&
                candidate.Id.Equals(previousCandidate.Id, StringComparison.OrdinalIgnoreCase))
            {
                stableObservations++;
                if (stableObservations >= 2)
                    return candidate;
            }
            else
            {
                previousCandidate = candidate;
                stableObservations = candidate is null ? 0 : 1;
            }

            if (attempt + 1 == policy.MaximumAttempts)
                break;

            // CoreAudio can briefly omit the Bluetooth output while the
            // selected headset changes profile, and its default output can
            // change identity while the microphone switches profiles. Require
            // two consecutive observations of the same endpoint before a cue
            // opens it.
            await delayAsync(policy.RetryInterval, cancellationToken).ConfigureAwait(false);
        }

        string route = hasSpecificRequest
            ? $"selected audio output '{requestedDeviceId}'"
            : "system-default audio output";
        throw new InvalidOperationException(
            $"The {route} did not become stable while the Bluetooth route was changing.");
    }
}

// Plays one explicitly described local cue. Cue meaning and route-stability
// policy live in LocalToneCues; this class owns only rendering and playback.
internal sealed class LocalTonePlayer : IAsyncDisposable
{
    private readonly Func<IAudioBackend> createAudioBackend;
    private readonly Func<string?> getOutputDeviceId;
    private readonly IAudioOutputRouteResolver outputRouteResolver;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;

    public LocalTonePlayer(
        Func<IAudioBackend> createAudioBackend,
        Func<string?> getOutputDeviceId,
        IAudioOutputRouteResolver? outputRouteResolver = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        this.createAudioBackend = createAudioBackend ?? throw new ArgumentNullException(nameof(createAudioBackend));
        this.getOutputDeviceId = getOutputDeviceId ?? throw new ArgumentNullException(nameof(getOutputDeviceId));
        this.outputRouteResolver = outputRouteResolver ?? new AudioOutputRouteResolver();
        this.delayAsync = delayAsync ?? ((delay, cancellationToken) => Task.Delay(delay, cancellationToken));
    }

    public async Task<LocalTonePlaybackResult> PlayAsync(
        LocalTonePlaybackRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MaximumPlaybackAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "A local cue requires at least one playback attempt.");
        if (request.OutputPostDrainDuration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(request), "A local cue cannot have a negative post-drain duration.");
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Exception? lastFailure = null;
            for (int attempt = 1; attempt <= request.MaximumPlaybackAttempts; attempt++)
            {
                try
                {
                    return await PlayOnceAsync(request, attempt, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException &&
                    attempt < request.MaximumPlaybackAttempts)
                {
                    lastFailure = exception;
                    await delayAsync(request.RoutePolicy.RetryInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    lastFailure = exception;
                }
            }

            throw new InvalidOperationException(
                $"The local cue could not complete after {request.MaximumPlaybackAttempts} playback attempts.",
                lastFailure);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<LocalTonePlaybackResult> PlayOnceAsync(
        LocalTonePlaybackRequest request,
        int attempt,
        CancellationToken cancellationToken)
    {
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
            EnsurePlaybackDrained("warm-up", warmupQueued, warmupConsumed);
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
        EnsurePlaybackDrained("cue", cueQueued, cueConsumed);

        // Queue drainage means CoreAudio or WaveOut accepted the last render
        // buffer; it does not mean a cold Bluetooth endpoint has presented it.
        // Keep the stream alive through the device's downstream latency so
        // stopping the stream cannot discard the complete permit indication.
        if (request.OutputPostDrainDuration > TimeSpan.Zero)
            await delayAsync(request.OutputPostDrainDuration, cancellationToken).ConfigureAwait(false);

        return new LocalTonePlaybackResult(
            output,
            AddSampleCounts(warmupQueued, cueQueued),
            AddSampleCounts(warmupConsumed, cueConsumed),
            attempt);
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

    private static void EnsurePlaybackDrained(string phase, int? queuedSamples, int? consumedSamples)
    {
        if (queuedSamples is > 0 && consumedSamples is int consumed && consumed < queuedSamples.Value)
        {
            throw new IOException(
                $"The local cue {phase} queued {queuedSamples.Value} samples but consumed only {consumed}.");
        }
    }
}
