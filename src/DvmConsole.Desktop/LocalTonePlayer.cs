using DvmConsole.Audio;
using System.Diagnostics;

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
    bool ReopenOutputAfterCueRelease,
    TimeSpan LeadSilenceDuration,
    TimeSpan TailSilenceDuration,
    TimeSpan OutputPostDrainDuration,
    int MaximumPlaybackAttempts,
    AudioOutputRoutePolicy RoutePolicy,
    bool RequireOutputCallbackEvidence = false,
    bool UseMeasuredOutputPresentationLatency = false);

internal readonly record struct LocalTonePresentationEvidence(
    long? WarmupCallbacksBefore,
    long? WarmupCallbacksAfter,
    long? CueCallbacksBefore,
    long? CueCallbacksAfter)
{
    public bool WarmupCallbackConsumptionObserved =>
        WarmupCallbacksBefore is long before && WarmupCallbacksAfter > before;
    public bool CueCallbackConsumptionObserved =>
        CueCallbacksBefore is long before && CueCallbacksAfter > before;
    public bool CallbackConsumptionConfirmed =>
        CueCallbackConsumptionObserved &&
        (WarmupCallbacksBefore is null || WarmupCallbackConsumptionObserved);
}

internal sealed record LocalTonePlaybackResult(
    AudioDeviceInfo Output,
    int? QueuedSamples,
    int? ConsumedSamples,
    int Attempts,
    TimeSpan? MeasuredOutputPresentationLatency,
    TimeSpan PostDrainWaitDuration,
    LocalTonePresentationEvidence PresentationEvidence,
    LocalTonePlaybackTiming Timing);

internal sealed record LocalTonePlaybackTiming(
    TimeSpan GateAcquired,
    TimeSpan InitialRouteResolved,
    TimeSpan InitialPlaybackOpened,
    TimeSpan CueReleased,
    TimeSpan OutputRouteConfirmed,
    TimeSpan FinalPlaybackOpened,
    TimeSpan OutputWarmupDrained,
    TimeSpan CueQueued,
    TimeSpan CueDrained,
    TimeSpan Completed);

internal static class LocalToneCues
{
    public static LocalTonePlaybackRequest TalkPermit { get; } = new(
        Frequency: 1200,
        ToneDuration: TimeSpan.FromMilliseconds(80),
        Amplitude: 0.40,
        OutputWarmupDuration: TimeSpan.FromMilliseconds(300),
        ReopenOutputAfterCueRelease: false,
        LeadSilenceDuration: TimeSpan.Zero,
        TailSilenceDuration: TimeSpan.Zero,
        OutputPostDrainDuration: TimeSpan.FromMilliseconds(200),
        MaximumPlaybackAttempts: 3,
        RoutePolicy: AudioOutputRoutePolicy.TransientRouteChanges,
        RequireOutputCallbackEvidence: true);

    // A cold Bluetooth microphone changes the headset's profile in place
    // without necessarily changing its CoreAudio device ID. Discard the
    // pre-transition playback stream after the first physical microphone
    // callback, then present the cue on a fresh duplex-profile stream.
    // Operator audio remains gated until capture resumes after output closes.
    public static LocalTonePlaybackRequest ColdStartTalkPermit { get; } = new(
        Frequency: 1200,
        ToneDuration: TimeSpan.FromMilliseconds(160),
        Amplitude: 0.40,
        OutputWarmupDuration: TimeSpan.Zero,
        ReopenOutputAfterCueRelease: true,
        LeadSilenceDuration: TimeSpan.FromMilliseconds(80),
        TailSilenceDuration: TimeSpan.Zero,
        OutputPostDrainDuration: TimeSpan.FromMilliseconds(500),
        MaximumPlaybackAttempts: 3,
        RoutePolicy: AudioOutputRoutePolicy.TransientRouteChanges,
        RequireOutputCallbackEvidence: true,
        UseMeasuredOutputPresentationLatency: true);

    public static LocalTonePlaybackRequest SelectTalkPermit(
        bool microphoneStartedCold,
        bool? microphoneIsBluetooth,
        bool? outputIsBluetooth)
    {
        if (!microphoneStartedCold)
            return TalkPermit;

        // A temporarily unclassified CoreAudio endpoint is expected while a
        // Bluetooth profile changes. Treat unknown as Bluetooth so uncertainty
        // cannot reopen the microphone after an inaudible short cue.
        return microphoneIsBluetooth != false || outputIsBluetooth != false
            ? ColdStartTalkPermit
            : TalkPermit;
    }

    public static LocalTonePlaybackRequest ConnectionEstablished { get; } = new(
        Frequency: 1500,
        ToneDuration: TimeSpan.FromMilliseconds(80),
        Amplitude: 0.25,
        OutputWarmupDuration: TimeSpan.Zero,
        ReopenOutputAfterCueRelease: false,
        LeadSilenceDuration: TimeSpan.Zero,
        TailSilenceDuration: TimeSpan.FromMilliseconds(40),
        OutputPostDrainDuration: TimeSpan.Zero,
        MaximumPlaybackAttempts: 2,
        RoutePolicy: AudioOutputRoutePolicy.TransientRouteChanges);

    public static LocalTonePlaybackRequest ConnectionLost { get; } = new(
        Frequency: 500,
        ToneDuration: TimeSpan.FromMilliseconds(160),
        Amplitude: 0.25,
        OutputWarmupDuration: TimeSpan.Zero,
        ReopenOutputAfterCueRelease: false,
        LeadSilenceDuration: TimeSpan.Zero,
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
        => await PlayCoreAsync(
            request,
            _ => request,
            Task.CompletedTask,
            beforeCueAsync: null,
            cancellationToken).ConfigureAwait(false);

    public async Task<LocalTonePlaybackResult> PlayTalkPermitAsync(
        bool microphoneStartedCold,
        bool? microphoneIsBluetooth,
        Task? cueReleaseBarrier = null,
        Func<CancellationToken, Task>? beforeCueAsync = null,
        CancellationToken cancellationToken = default)
        => await PlayCoreAsync(
            LocalToneCues.TalkPermit,
            output => LocalToneCues.SelectTalkPermit(
                microphoneStartedCold,
                microphoneIsBluetooth,
                output.IsBluetooth),
            cueReleaseBarrier ?? Task.CompletedTask,
            beforeCueAsync,
            cancellationToken).ConfigureAwait(false);

    private async Task<LocalTonePlaybackResult> PlayCoreAsync(
        LocalTonePlaybackRequest routeRequest,
        Func<AudioDeviceInfo, LocalTonePlaybackRequest> selectRequest,
        Task cueReleaseBarrier,
        Func<CancellationToken, Task>? beforeCueAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(routeRequest);
        ArgumentNullException.ThrowIfNull(selectRequest);
        ArgumentNullException.ThrowIfNull(cueReleaseBarrier);
        if (routeRequest.MaximumPlaybackAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(routeRequest), "A local cue requires at least one playback attempt.");
        if (routeRequest.LeadSilenceDuration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(routeRequest), "A local cue cannot have negative lead silence.");
        if (routeRequest.OutputPostDrainDuration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(routeRequest), "A local cue cannot have a negative post-drain duration.");
        ObjectDisposedException.ThrowIf(disposed, this);
        var timing = Stopwatch.StartNew();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        TimeSpan gateAcquired = timing.Elapsed;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Exception? lastFailure = null;
            bool activationAttempted = false;
            for (int attempt = 1; attempt <= routeRequest.MaximumPlaybackAttempts; attempt++)
            {
                try
                {
                    return await PlayOnceAsync(
                        routeRequest,
                        selectRequest,
                        cueReleaseBarrier,
                        attempt,
                        timing,
                        gateAcquired,
                        async token =>
                        {
                            if (beforeCueAsync is null)
                                return;
                            activationAttempted = true;
                            await beforeCueAsync(token).ConfigureAwait(false);
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                catch when (cueReleaseBarrier.IsFaulted || cueReleaseBarrier.IsCanceled)
                {
                    throw;
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException &&
                    !activationAttempted &&
                    attempt < routeRequest.MaximumPlaybackAttempts)
                {
                    lastFailure = exception;
                    await delayAsync(routeRequest.RoutePolicy.RetryInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException &&
                    activationAttempted)
                {
                    throw new InvalidOperationException(
                        "The local cue failed after transmit activation; playback was not retried.",
                        exception);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    lastFailure = exception;
                }
            }

            throw new InvalidOperationException(
                $"The local cue could not complete after {routeRequest.MaximumPlaybackAttempts} playback attempts.",
                lastFailure);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<LocalTonePlaybackResult> PlayOnceAsync(
        LocalTonePlaybackRequest routeRequest,
        Func<AudioDeviceInfo, LocalTonePlaybackRequest> selectRequest,
        Task cueReleaseBarrier,
        int attempt,
        Stopwatch timing,
        TimeSpan gateAcquired,
        Func<CancellationToken, Task> beforeCueAsync,
        CancellationToken cancellationToken)
    {
        using IAudioBackend backend = createAudioBackend();
        string? requestedOutputDeviceId = getOutputDeviceId();
        AudioDeviceInfo output = await outputRouteResolver.ResolveAsync(
            backend,
            requestedOutputDeviceId,
            routeRequest.RoutePolicy,
            cancellationToken).ConfigureAwait(false);
        TimeSpan initialRouteResolved = timing.Elapsed;
        LocalTonePlaybackRequest request = selectRequest(output) ??
            throw new InvalidOperationException("The local cue route did not select a playback request.");
        IAudioPlayback? playback = backend.OpenPlayback(
            output,
            PcmAudioFormat.Voice8KhzMono16Bit);
        TimeSpan initialPlaybackOpened = timing.Elapsed;
        try
        {
            AudioDeviceInfo finalOutput = output;
            TimeSpan cueReleased;
            TimeSpan outputRouteConfirmed = initialRouteResolved;
            TimeSpan finalPlaybackOpened = initialPlaybackOpened;

            if (request.ReopenOutputAfterCueRelease)
            {
                // The caller releases this barrier only after actual selected-
                // microphone samples arrive and the channel presentation is TX.
                await cueReleaseBarrier.WaitAsync(cancellationToken).ConfigureAwait(false);
                cueReleased = timing.Elapsed;

                // A cold Bluetooth profile transition can keep the same device
                // ID while invalidating the already-open HAL stream. Always
                // replace it after capture readiness instead of trusting ID-only
                // route confirmation.
                await playback.DisposeAsync().ConfigureAwait(false);
                playback = null;
                finalOutput = await outputRouteResolver.ResolveAsync(
                    backend,
                    requestedOutputDeviceId,
                    routeRequest.RoutePolicy,
                    cancellationToken).ConfigureAwait(false);
                outputRouteConfirmed = timing.Elapsed;
                playback = backend.OpenPlayback(
                    finalOutput,
                    PcmAudioFormat.Voice8KhzMono16Bit);
                finalPlaybackOpened = timing.Elapsed;
            }
            else
            {
                cueReleased = TimeSpan.Zero;
            }

            IAudioPlayback activePlayback = playback ??
                throw new InvalidOperationException("The local cue output was not reopened.");
            var generator = new PcmToneGenerator();
            int? warmupQueued = null;
            int? warmupConsumed = null;
            long? warmupCallbacksBefore = request.OutputWarmupDuration > TimeSpan.Zero
                ? GetOutputCallbackCount(activePlayback)
                : null;
            if (request.OutputWarmupDuration > TimeSpan.Zero)
            {
                short[] warmup = generator.GenerateSilence(request.OutputWarmupDuration);
                await activePlayback.WriteAsync(warmup, cancellationToken).ConfigureAwait(false);
                warmupQueued = activePlayback.QueuedSamples;
                warmupConsumed = await activePlayback.DrainAsync(cancellationToken).ConfigureAwait(false);
                EnsurePlaybackDrained("warm-up", warmupQueued, warmupConsumed);
            }
            long? warmupCallbacksAfter = request.OutputWarmupDuration > TimeSpan.Zero
                ? GetOutputCallbackCount(activePlayback)
                : null;
            if (request.OutputWarmupDuration > TimeSpan.Zero)
            {
                EnsureOutputCallbackConsumption(
                    request,
                    "warm-up",
                    warmupCallbacksBefore,
                    warmupCallbacksAfter);
            }
            TimeSpan outputWarmupDrained = timing.Elapsed;

            if (!request.ReopenOutputAfterCueRelease)
            {
                await cueReleaseBarrier.WaitAsync(cancellationToken).ConfigureAwait(false);
                cueReleased = timing.Elapsed;

                if (request.OutputWarmupDuration > TimeSpan.Zero)
                {
                    AudioDeviceInfo confirmedOutput = await outputRouteResolver.ResolveAsync(
                        backend,
                        requestedOutputDeviceId,
                        routeRequest.RoutePolicy,
                        cancellationToken).ConfigureAwait(false);
                    outputRouteConfirmed = timing.Elapsed;
                    if (!confirmedOutput.Id.Equals(output.Id, StringComparison.OrdinalIgnoreCase) ||
                        confirmedOutput.IsBluetooth != output.IsBluetooth)
                    {
                        throw new IOException(
                            $"The local cue output changed from '{output.Name}' to '{confirmedOutput.Name}' during route warm-up.");
                    }
                    finalOutput = confirmedOutput;
                }
            }

            await beforeCueAsync(cancellationToken).ConfigureAwait(false);

            short[] tone = generator.GenerateTone(
                request.Frequency,
                request.ToneDuration,
                request.Amplitude);
            ApplyFade(tone, PcmAudioFormat.Voice8KhzMono16Bit.SampleRate / 200);
            short[] leadSilence = request.LeadSilenceDuration > TimeSpan.Zero
                ? generator.GenerateSilence(request.LeadSilenceDuration)
                : [];
            short[] tailSilence = request.TailSilenceDuration > TimeSpan.Zero
                ? generator.GenerateSilence(request.TailSilenceDuration)
                : [];
            short[] samples =
            [
                .. leadSilence,
                .. tone,
                .. tailSilence
            ];
            long? cueCallbacksBefore = GetOutputCallbackCount(activePlayback);
            await activePlayback.WriteAsync(samples, cancellationToken).ConfigureAwait(false);
            TimeSpan cueQueuedAt = timing.Elapsed;
            int? cueQueued = activePlayback.QueuedSamples;
            int? cueConsumed = await activePlayback.DrainAsync(cancellationToken).ConfigureAwait(false);
            EnsurePlaybackDrained("cue", cueQueued, cueConsumed);
            long? cueCallbacksAfter = GetOutputCallbackCount(activePlayback);
            EnsureOutputCallbackConsumption(
                request,
                "cue",
                cueCallbacksBefore,
                cueCallbacksAfter);
            TimeSpan cueDrainedAt = timing.Elapsed;

            AudioDeviceInfo completedOutput = await outputRouteResolver.ResolveAsync(
                backend,
                requestedOutputDeviceId,
                request.RoutePolicy,
                cancellationToken).ConfigureAwait(false);
            EnsureSameOutput(finalOutput, completedOutput);

            TimeSpan? measuredPresentationLatency = GetMeasuredPresentationLatency(
                request,
                activePlayback);
            TimeSpan postDrainWait = measuredPresentationLatency is TimeSpan latency
                ? AddPresentationSchedulingMargin(latency)
                : request.OutputPostDrainDuration;

            // Queue drainage establishes callback consumption, not physical
            // presentation. On the experimental cold-Bluetooth path, wait for
            // CoreAudio's measured device presentation interval. Other paths
            // retain their existing fixed policy.
            if (postDrainWait > TimeSpan.Zero)
                await delayAsync(postDrainWait, cancellationToken).ConfigureAwait(false);
            TimeSpan completedAt = timing.Elapsed;

            return new LocalTonePlaybackResult(
                finalOutput,
                AddSampleCounts(warmupQueued, cueQueued),
                AddSampleCounts(warmupConsumed, cueConsumed),
                attempt,
                measuredPresentationLatency,
                postDrainWait,
                new LocalTonePresentationEvidence(
                    warmupCallbacksBefore,
                    warmupCallbacksAfter,
                    cueCallbacksBefore,
                    cueCallbacksAfter),
                new LocalTonePlaybackTiming(
                    gateAcquired,
                    initialRouteResolved,
                    initialPlaybackOpened,
                    cueReleased,
                    outputRouteConfirmed,
                    finalPlaybackOpened,
                    outputWarmupDrained,
                    cueQueuedAt,
                    cueDrainedAt,
                    completedAt));
        }
        finally
        {
            if (playback is not null)
                await playback.DisposeAsync().ConfigureAwait(false);
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

    private static void EnsurePlaybackDrained(string phase, int? queuedSamples, int? consumedSamples)
    {
        if (queuedSamples is > 0 && consumedSamples is int consumed && consumed < queuedSamples.Value)
        {
            throw new IOException(
                $"The local cue {phase} queued {queuedSamples.Value} samples but consumed only {consumed}.");
        }
    }

    private static long? GetOutputCallbackCount(IAudioPlayback playback)
        => (playback as IAudioPlaybackCallbackDiagnostics)?.OutputCallbackCount;

    private static TimeSpan? GetMeasuredPresentationLatency(
        LocalTonePlaybackRequest request,
        IAudioPlayback playback)
    {
        if (!request.UseMeasuredOutputPresentationLatency ||
            playback is not IAudioPlaybackPresentationLatencyDiagnostics diagnostics)
            return null;

        TimeSpan latency = diagnostics.OutputPresentationLatency;
        return latency > TimeSpan.Zero && latency < TimeSpan.FromSeconds(5)
            ? latency
            : null;
    }

    private static TimeSpan AddPresentationSchedulingMargin(TimeSpan latency)
        => latency + TimeSpan.FromMilliseconds(20);

    private static void EnsureOutputCallbackConsumption(
        LocalTonePlaybackRequest request,
        string phase,
        long? callbacksBefore,
        long? callbacksAfter)
    {
        if (!request.RequireOutputCallbackEvidence)
            return;
        // Callback progress establishes consumption by the OS audio device.
        // It does not establish physical audibility; the cold-Bluetooth policy
        // separately uses the downstream-latency allowance.
        if (callbacksBefore is null || callbacksAfter is null)
        {
            throw new NotSupportedException(
                $"The local cue output cannot verify native render callbacks during {phase}.");
        }
        if (callbacksAfter <= callbacksBefore)
        {
            throw new IOException(
                $"The local cue output drained {phase} samples without a native render callback.");
        }
    }

    private static void EnsureSameOutput(AudioDeviceInfo expected, AudioDeviceInfo actual)
    {
        if (!actual.Id.Equals(expected.Id, StringComparison.OrdinalIgnoreCase) ||
            actual.IsBluetooth != expected.IsBluetooth)
        {
            throw new IOException(
                $"The local cue output changed from '{expected.Name}' to '{actual.Name}' during presentation.");
        }
    }
}
