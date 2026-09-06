// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Buffers;
using DvmConsole.Audio;

namespace DvmConsole.Application;

public static class GeneratedAudioMonitorSession
{
    public static async Task<Exception?> RunAsync(
        bool monitorEnabled,
        Func<CancellationToken, Task> monitorAsync,
        Func<Task> transmitAsync)
    {
        ArgumentNullException.ThrowIfNull(monitorAsync);
        ArgumentNullException.ThrowIfNull(transmitAsync);

        if (!monitorEnabled)
        {
            await transmitAsync().ConfigureAwait(false);
            return null;
        }

        using var monitorCancellation = new CancellationTokenSource();
        Task<Exception?> monitorTask = ObserveMonitorAsync(
            monitorAsync,
            monitorCancellation.Token);
        try
        {
            await transmitAsync().ConfigureAwait(false);
        }
        catch
        {
            monitorCancellation.Cancel();
            await monitorTask.ConfigureAwait(false);
            throw;
        }

        return await monitorTask.ConfigureAwait(false);
    }

    private static async Task<Exception?> ObserveMonitorAsync(
        Func<CancellationToken, Task> monitorAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            await monitorAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}

/// <summary>
/// Presents an attenuated copy of generated 8 kHz PCM on an operator output.
/// Transmission remains independent and the source samples are never mutated.
/// </summary>
public sealed class GeneratedAudioMonitor : IAsyncDisposable
{
    private const int PlaybackPrefetchSamples = 1_600;
    private const int PlaybackChunkSamples = 160;
    internal const double OutputGain = 0.70;
    private const int OutputRouteMaximumAttempts = 12;
    private static readonly TimeSpan OutputRouteRetryInterval = TimeSpan.FromMilliseconds(50);
    private readonly Func<IAudioBackend> createAudioBackend;
    private readonly Func<PcmAudioFormat, IPcmPlaybackPacer> createPacer;
    private readonly Func<string?> getOutputDeviceId;
    private readonly Func<IAudioBackend, string?, CancellationToken, Task<AudioDeviceInfo>> resolveOutput;
    private readonly IApplicationDelay delay;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;

    public GeneratedAudioMonitor(
        Func<IAudioBackend> createAudioBackend,
        Func<string?> getOutputDeviceId,
        Func<IAudioBackend, string?, CancellationToken, Task<AudioDeviceInfo>>? resolveOutput = null,
        IApplicationDelay? delay = null,
        Func<PcmAudioFormat, IPcmPlaybackPacer>? createPacer = null)
    {
        this.createAudioBackend = createAudioBackend ??
            throw new ArgumentNullException(nameof(createAudioBackend));
        this.getOutputDeviceId = getOutputDeviceId ??
            throw new ArgumentNullException(nameof(getOutputDeviceId));
        this.delay = delay ?? SystemApplicationDelay.Instance;
        this.resolveOutput = resolveOutput ?? ResolveOutputAsync;
        this.createPacer = createPacer ?? (format => new RealtimePcmPlaybackPacer(format));
    }

    public async Task PlayAsync(
        ReadOnlyMemory<short> samples,
        CancellationToken cancellationToken = default)
    {
        if (samples.IsEmpty)
            throw new ArgumentException("Generated monitor audio cannot be empty.", nameof(samples));

        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using IAudioBackend backend = createAudioBackend();
            AudioDeviceInfo output = await resolveOutput(
                backend,
                getOutputDeviceId(),
                cancellationToken).ConfigureAwait(false);
            await using IAudioPlayback playback = backend.OpenPlayback(
                output,
                PcmAudioFormat.Voice8KhzMono16Bit);

            IPcmPlaybackPacer pacer = createPacer(playback.Format);
            short[] outputBuffer = ArrayPool<short>.Shared.Rent(PlaybackPrefetchSamples);
            try
            {
                // Seed a 200 ms safety window, then replenish it at the 20 ms
                // media cadence. This keeps scheduler jitter from creating
                // audible silence gaps at coarse burst boundaries.
                int prefetchCount = Math.Min(PlaybackPrefetchSamples, samples.Length);
                await WriteAsync(0, prefetchCount, waitForPacer: false).ConfigureAwait(false);
                for (int offset = prefetchCount; offset < samples.Length; offset += PlaybackChunkSamples)
                {
                    int count = Math.Min(PlaybackChunkSamples, samples.Length - offset);
                    await WriteAsync(offset, count, waitForPacer: true).ConfigureAwait(false);
                }

                async ValueTask WriteAsync(int offset, int count, bool waitForPacer)
                {
                    if (waitForPacer)
                        await pacer.WaitBeforeWriteAsync(cancellationToken).ConfigureAwait(false);
                    ApplyOutputGain(
                        samples.Span.Slice(offset, count),
                        outputBuffer.AsSpan(0, count));
                    await playback.WriteAsync(
                        outputBuffer.AsMemory(0, count),
                        cancellationToken).ConfigureAwait(false);
                    pacer.ObserveWrittenSamples(count);
                }
            }
            finally
            {
                ArrayPool<short>.Shared.Return(outputBuffer);
            }

            int? queuedSamples = playback.QueuedSamples;
            int? consumedSamples = await playback.DrainAsync(cancellationToken).ConfigureAwait(false);
            if (queuedSamples is > 0 &&
                consumedSamples is int consumed &&
                consumed < queuedSamples.Value)
            {
                throw new IOException(
                    $"The generated-audio monitor queued {queuedSamples.Value} samples " +
                    $"but consumed only {consumed}.");
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
        }
    }

    private static void ApplyOutputGain(ReadOnlySpan<short> input, Span<short> output)
    {
        for (int index = 0; index < input.Length; index++)
        {
            output[index] = (short)Math.Round(
                input[index] * OutputGain,
                MidpointRounding.AwayFromZero);
        }
    }

    private async Task<AudioDeviceInfo> ResolveOutputAsync(
        IAudioBackend backend,
        string? requestedDeviceId,
        CancellationToken cancellationToken)
    {
        bool hasSpecificRequest = !string.IsNullOrWhiteSpace(requestedDeviceId) &&
            !requestedDeviceId.Equals("default", StringComparison.OrdinalIgnoreCase);
        AudioDeviceInfo? previousCandidate = null;
        int stableObservations = 0;
        for (int attempt = 0; attempt < OutputRouteMaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<AudioDeviceInfo> devices = backend.EnumerateDevices(AudioDirection.Output);
            AudioDeviceInfo? candidate = hasSpecificRequest
                ? devices.FirstOrDefault(device =>
                    device.Id.Equals(requestedDeviceId, StringComparison.OrdinalIgnoreCase))
                : devices.FirstOrDefault(device => device.IsDefault)
                    ?? (devices.Count > 0 ? devices[0] : null);

            if (candidate is not null &&
                previousCandidate is not null &&
                candidate.Id.Equals(previousCandidate.Id, StringComparison.OrdinalIgnoreCase))
            {
                if (++stableObservations >= 2)
                    return candidate;
            }
            else
            {
                previousCandidate = candidate;
                stableObservations = candidate is null ? 0 : 1;
            }

            if (attempt + 1 < OutputRouteMaximumAttempts)
                await delay.DelayAsync(OutputRouteRetryInterval, cancellationToken).ConfigureAwait(false);
        }

        string route = hasSpecificRequest
            ? $"selected audio output '{requestedDeviceId}'"
            : "system-default audio output";
        throw new InvalidOperationException(
            $"The {route} did not become stable while the audio route was changing.");
    }
}
