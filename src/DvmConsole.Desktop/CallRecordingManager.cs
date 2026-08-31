using DvmConsole.Application;
using DvmConsole.Core.Runtime;
using DvmConsole.Media;

namespace DvmConsole.Desktop;

/// <summary>
/// Compatibility facade for the desktop view model. Application owns RX/TX
/// recording lifecycle; the desktop store supplies path-based TAR persistence.
/// </summary>
public sealed class CallRecordingManager : IDisposable, IAsyncDisposable
{
    public const int DefaultRetentionDays = 7;

    private readonly DesktopRecordingStore store;
    private readonly CallRecordingService service;
    private readonly Action<ChannelId, Exception>? faultHandler;
    private int disposed;

    public CallRecordingManager(
        string rootPath,
        Action<ChannelId, Exception>? faultHandler = null,
        int retentionDays = DefaultRetentionDays,
        Func<ChannelId, uint, bool>? shouldRecordSource = null,
        Func<ChannelId, uint, string>? resolveSubscriberAlias = null)
        : this(
            rootPath,
            faultHandler,
            retentionDays,
            shouldRecordSource,
            resolveSubscriberAlias,
            RecordingFinalizationQueue.DefaultCapacity,
            finalizeRecording: null)
    {
    }

    internal CallRecordingManager(
        string rootPath,
        Action<ChannelId, Exception>? faultHandler,
        int retentionDays,
        Func<ChannelId, uint, bool>? shouldRecordSource,
        Func<ChannelId, uint, string>? resolveSubscriberAlias,
        int finalizationQueueCapacity,
        Func<
            RecordingFinalizationDescriptor,
            ChannelId?,
            CancellationToken,
            Task<RecordingFinalizationResult>>? finalizeRecording)
    {
        this.faultHandler = faultHandler;
        store = new DesktopRecordingStore(
            rootPath,
            faultHandler,
            retentionDays,
            finalizationQueueCapacity,
            finalizeRecording);
        try
        {
            service = new CallRecordingService(
                store,
                retentionDays: retentionDays,
                shouldRecordSource: shouldRecordSource,
                resolveSubscriberAlias: resolveSubscriberAlias);
        }
        catch
        {
            store.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    public event EventHandler<RecordingFinalizationResult>? RecordingFinalized
    {
        add => store.RecordingFinalized += value;
        remove => store.RecordingFinalized -= value;
    }

    public int RetentionDays
    {
        get => service.RetentionDays;
        set
        {
            service.RetentionDays = value;
            store.RetentionDays = value;
        }
    }

    public IReadOnlyList<string> ActivePaths => store.ActivePaths;
    public string RootPath => store.RootPath;
    internal IRecordingStore Store => store;
    internal RecordingFinalizationSpoolHealth FinalizationHealth => store.FinalizationHealth;
    internal int ScheduledFinalizationCount => store.ScheduledFinalizationCount;

    internal bool IsRecording(ChannelRecordingDescriptor channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return store.IsRecording(channel.Id);
    }

    internal bool IsFinalizing(ChannelRecordingDescriptor channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return store.IsFinalizing(channel.Id);
    }

    public bool TrySetRootPath(string requestedPath, out string errorMessage)
        => store.TrySetRootPath(requestedPath, out errorMessage);

    public IReadOnlyList<CallRecordingMetadata> LoadRecordings()
        => store.LoadRecordings();

    public Task<IReadOnlyList<CallRecordingMetadata>> LoadRecordingsAsync(
        CancellationToken cancellationToken = default)
        => store.LoadRecordingsAsync(cancellationToken);

    internal Task<RecordingCatalogScanResult> LoadAndPruneRecordingsAsync(
        bool pruneExpired,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
        => store.LoadAndPruneRecordingsAsync(pruneExpired, now, cancellationToken);

    public int PruneExpired(DateTimeOffset? now = null)
        => store.PruneExpired(now);

    public bool DeleteRecording(CallRecordingMetadata metadata)
        => store.DeleteRecording(metadata);

    public bool TryGetRecordingPath(
        CallRecordingMetadata metadata,
        out string recordingPath)
        => store.TryGetRecordingPath(metadata, out recordingPath);

    public void WriteSamples(
        ChannelRecordingDescriptor channel,
        ReadOnlyMemory<short> samples)
        => WriteSamples(
            channel,
            channel.ActiveStreamId ?? 0,
            channel.ActiveSourceId ?? 0,
            samples);

    public void WriteSamples(
        ChannelRecordingDescriptor channel,
        uint streamId,
        uint sourceId,
        ReadOnlyMemory<short> samples)
        => WriteEpisodeSamples(channel, streamId, streamId, sourceId, samples);

    public void WriteEpisodeSamples(
        ChannelRecordingDescriptor channel,
        uint episodeStreamId,
        uint physicalStreamId,
        uint sourceId,
        ReadOnlyMemory<short> samples,
        long? receiveEpisodeId = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        Execute(
            channel.Id,
            () => service.WriteReceiveSamplesAsync(
                channel,
                episodeStreamId,
                physicalStreamId,
                sourceId,
                samples,
                receiveEpisodeId));
    }

    public void WriteTransmitSamples(
        ChannelRecordingDescriptor channel,
        uint streamId,
        uint sourceId,
        ReadOnlyMemory<short> samples)
    {
        ArgumentNullException.ThrowIfNull(channel);
        Execute(
            channel.Id,
            () => service.WriteTransmitSamplesAsync(
                channel,
                streamId,
                sourceId,
                samples));
    }

    public bool ObserveTraffic(
        ChannelRecordingDescriptor channel,
        IRadioMediaFrame traffic)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(traffic);
        Execute(
            channel.Id,
            () => service.ObserveReceiveTrafficAsync(
                channel,
                traffic.StreamId,
                traffic.StreamId,
                traffic));
        if (!RadioReceiveTrafficClassifier.IsTerminator(traffic))
            return false;

        bool wasRecording = store.IsRecordingEpisode(channel.Id, traffic.StreamId);
        Execute(
            channel.Id,
            () => service.StopReceiveEpisodeAsync(channel.Id, traffic.StreamId));
        return wasRecording;
    }

    public void ObserveEpisodeTraffic(
        ChannelRecordingDescriptor channel,
        uint episodeStreamId,
        uint physicalStreamId,
        IRadioMediaFrame traffic,
        long? receiveEpisodeId = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(traffic);
        if (episodeStreamId == 0)
            return;
        Execute(
            channel.Id,
            () => service.ObserveReceiveTrafficAsync(
                channel,
                receiveEpisodeId ?? episodeStreamId,
                physicalStreamId,
                traffic));
    }

    public void StopChannel(ChannelRecordingDescriptor channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        Execute(channel.Id, () => service.StopChannelAsync(channel.Id));
    }

    public void StopStream(ChannelRecordingDescriptor channel, uint streamId)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (streamId == 0)
            return;
        Execute(
            channel.Id,
            () => service.StopReceiveEpisodeAsync(channel.Id, streamId));
    }

    public void StopEpisode(ChannelRecordingDescriptor channel, long receiveEpisodeId)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (receiveEpisodeId <= 0)
            return;
        Execute(
            channel.Id,
            () => service.StopReceiveEpisodeAsync(channel.Id, receiveEpisodeId));
    }

    public void StopTransmit(ChannelRecordingDescriptor channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        Execute(channel.Id, () => service.StopTransmitAsync(channel.Id));
    }

    public void Dispose()
        => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        try
        {
            await service.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await store.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void Execute(ChannelId channelId, Func<ValueTask> operation)
    {
        try
        {
            operation().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception) when (IsRecordingStorageFailure(exception))
        {
            faultHandler?.Invoke(channelId, exception);
        }
    }

    private static bool IsRecordingStorageFailure(Exception exception)
        => exception is IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            InvalidDataException or
            ArgumentException or
            NotSupportedException;
}
