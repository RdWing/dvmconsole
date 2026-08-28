using DvmConsole.Audio;

namespace DvmConsole.Desktop;

internal static class GeneratedAudioMonitorSession
{
    public static async Task<Exception?> RunAsync(
        Func<CancellationToken, Task> monitorAsync,
        Func<Task> transmitAsync)
    {
        ArgumentNullException.ThrowIfNull(monitorAsync);
        ArgumentNullException.ThrowIfNull(transmitAsync);

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

// Presents the exact generated 8 kHz PCM sent to radio channels on the
// operator's configured output. Transmission remains owned by
// ToneTransmitCoordinator; this class owns only the local monitor stream.
internal sealed class GeneratedAudioMonitor : IAsyncDisposable
{
    private const int PlaybackChunkSamples = 1_600;
    private readonly Func<IAudioBackend> createAudioBackend;
    private readonly Func<string?> getOutputDeviceId;
    private readonly IAudioOutputRouteResolver outputRouteResolver;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;

    public GeneratedAudioMonitor(
        Func<IAudioBackend> createAudioBackend,
        Func<string?> getOutputDeviceId,
        IAudioOutputRouteResolver? outputRouteResolver = null)
    {
        this.createAudioBackend = createAudioBackend ??
            throw new ArgumentNullException(nameof(createAudioBackend));
        this.getOutputDeviceId = getOutputDeviceId ??
            throw new ArgumentNullException(nameof(getOutputDeviceId));
        this.outputRouteResolver = outputRouteResolver ?? new AudioOutputRouteResolver();
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
            AudioDeviceInfo output = await outputRouteResolver.ResolveAsync(
                backend,
                getOutputDeviceId(),
                AudioOutputRoutePolicy.TransientRouteChanges,
                cancellationToken).ConfigureAwait(false);
            await using IAudioPlayback playback = backend.OpenPlayback(
                output,
                PcmAudioFormat.Voice8KhzMono16Bit);

            var pacer = new RealtimePcmPlaybackPacer(playback.Format);
            for (int offset = 0; offset < samples.Length; offset += PlaybackChunkSamples)
            {
                await pacer.WaitBeforeWriteAsync(cancellationToken).ConfigureAwait(false);
                int count = Math.Min(PlaybackChunkSamples, samples.Length - offset);
                await playback.WriteAsync(samples.Slice(offset, count), cancellationToken)
                    .ConfigureAwait(false);
                pacer.ObserveWrittenSamples(count);
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
            gate.Dispose();
        }
    }
}
