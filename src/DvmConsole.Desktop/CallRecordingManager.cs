// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Runtime;
using DvmConsole.Media;
using System.Buffers;
using System.Threading.Channels;

namespace DvmConsole.Desktop;

/// <summary>
/// Compatibility facade for the desktop view model. Application owns RX/TX
/// recording lifecycle; the desktop store supplies path-based TAR persistence.
/// </summary>
public sealed class CallRecordingManager :
    IDisposable,
    IAsyncDisposable,
    IRecordingFinalizationHealthSource
{
    public const int DefaultRetentionDays = 7;
    private const int RecordingWorkCapacity = 2_048;

    private readonly DesktopRecordingStore store;
    private readonly CallRecordingService service;
    private readonly Action<ChannelId, Exception>? faultHandler;
    private readonly Channel<RecordingWorkItem> work;
    private readonly Task workLoop;
    private readonly ArrayPool<short> samplePool;
    private readonly int recordingWorkCapacity;
    private readonly object recordingStateSync = new();
    private readonly Dictionary<ChannelId, ChannelRecordingState> recordingStates = [];
    private readonly Dictionary<ChannelId, string> recordingFaults = [];
    private int queuedSampleWork;
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
            Task<RecordingFinalizationResult>>? finalizeRecording,
        int recordingWorkCapacity = RecordingWorkCapacity,
        ArrayPool<short>? samplePool = null)
    {
        if (recordingWorkCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(recordingWorkCapacity));
        this.faultHandler = faultHandler;
        this.samplePool = samplePool ?? ArrayPool<short>.Shared;
        this.recordingWorkCapacity = recordingWorkCapacity;
        store = new DesktopRecordingStore(
            rootPath,
            ReportFault,
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
        store.RecordingFinalized += HandleStoreRecordingFinalized;
        work = Channel.CreateUnbounded<RecordingWorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        workLoop = Task.Run(ProcessWorkAsync);
    }

    public event EventHandler<RecordingFinalizationResult>? RecordingFinalized
    {
        add => store.RecordingFinalized += value;
        remove => store.RecordingFinalized -= value;
    }

    internal event Action<ChannelId>? RecordingStateChanged;
    internal Task OwnershipReleased => store.OwnershipReleased;

    public int RetentionDays
    {
        get => service.RetentionDays;
        set
        {
            service.RetentionDays = value;
            store.RetentionDays = value;
        }
    }

    public IReadOnlyList<string> ActivePaths
    {
        get { Flush(); return store.ActivePaths; }
    }
    public string RootPath => store.RootPath;
    internal bool CanWriteRecordings => store.CanWriteRecordings;
    internal IRecordingStore Store => store;
    internal RecordingFinalizationSpoolHealth FinalizationHealth => store.FinalizationHealth;
    RecordingFinalizationSpoolHealth IRecordingFinalizationHealthSource.FinalizationHealth
        => store.FinalizationHealth;
    internal int ScheduledFinalizationCount => store.ScheduledFinalizationCount;

    internal Task DrainAcceptedWorkAsync(CancellationToken cancellationToken = default)
        => FlushAsync(cancellationToken);

    internal IReadOnlyDictionary<ChannelId, ChannelRecordingState> CaptureState(
        IEnumerable<ChannelRecordingDescriptor> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ChannelRecordingDescriptor[] snapshot = channels.DistinctBy(channel => channel.Id).ToArray();
        lock (recordingStateSync)
        {
            return snapshot.ToDictionary(
                channel => channel.Id,
                channel => recordingStates.GetValueOrDefault(channel.Id));
        }
    }

    public bool TrySetRootPath(string requestedPath, out string errorMessage)
    {
        Flush();
        return store.TrySetRootPath(requestedPath, out errorMessage);
    }

    public IReadOnlyList<CallRecordingMetadata> LoadRecordings()
    {
        Flush();
        return store.LoadRecordings();
    }

    public async Task<IReadOnlyList<CallRecordingMetadata>> LoadRecordingsAsync(
        CancellationToken cancellationToken = default)
    {
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        return await store.LoadRecordingsAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<RecordingCatalogScanResult> LoadAndPruneRecordingsAsync(
        bool pruneExpired,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        return await store.LoadAndPruneRecordingsAsync(pruneExpired, now, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static Task<RecordingCatalogScanResult> PreviewRecordingPolicyAsync(
        string rootPath,
        int retentionDays,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (retentionDays is < 0 or > 3650)
            throw new ArgumentOutOfRangeException(nameof(retentionDays));
        string normalizedRoot = Path.GetFullPath(rootPath.Trim());
        return Task.Run(
            () => new RecordingCatalogStore().Scan(
                normalizedRoot,
                retentionDays,
                now ?? DateTimeOffset.UtcNow,
                cancellationToken,
                deleteExpired: false),
            cancellationToken);
    }

    public int PruneExpired(DateTimeOffset? now = null)
    {
        Flush();
        return store.PruneExpired(now);
    }

    public bool DeleteRecording(CallRecordingMetadata metadata)
    {
        Flush();
        return store.DeleteRecording(metadata);
    }

    public bool TryGetRecordingPath(
        CallRecordingMetadata metadata,
        out string recordingPath)
    {
        Flush();
        return store.TryGetRecordingPath(metadata, out recordingPath);
    }

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
        EnqueueSamples(RecordingWorkItem.ReceiveSamples(
            channel,
            episodeStreamId,
            physicalStreamId,
            sourceId,
            receiveEpisodeId,
            RentSamples(samples.Span)));
    }

    public void WriteTransmitSamples(
        ChannelRecordingDescriptor channel,
        uint streamId,
        uint sourceId,
        ReadOnlyMemory<short> samples)
        => WriteTransmitSamples(channel, streamId, sourceId, samples.Span);

    public void WriteTransmitSamples(
        ChannelRecordingDescriptor channel,
        uint streamId,
        uint sourceId,
        ReadOnlySpan<short> samples)
    {
        ArgumentNullException.ThrowIfNull(channel);
        EnqueueSamples(RecordingWorkItem.TransmitSamples(
            channel,
            streamId,
            sourceId,
            RentSamples(samples)));
    }

    public bool ObserveTraffic(
        ChannelRecordingDescriptor channel,
        IRadioMediaFrame traffic)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(traffic);
        EnqueueOperation(
            channel.Id,
            () => service.ObserveReceiveTrafficAsync(
                channel,
                traffic.StreamId,
                traffic.StreamId,
                traffic));
        if (!RadioReceiveTrafficClassifier.IsTerminator(traffic))
            return false;

        bool wasRecording = channel.RecordingEnabled;
        EnqueueOperation(
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
        EnqueueOperation(
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
        EnqueueOperation(channel.Id, () => service.StopChannelAsync(channel.Id));
    }

    public void StopStream(ChannelRecordingDescriptor channel, uint streamId)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (streamId == 0)
            return;
        EnqueueOperation(
            channel.Id,
            () => service.StopReceiveEpisodeAsync(channel.Id, streamId));
    }

    public void StopEpisode(ChannelRecordingDescriptor channel, long receiveEpisodeId)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (receiveEpisodeId <= 0)
            return;
        EnqueueOperation(
            channel.Id,
            () => service.StopReceiveEpisodeAsync(channel.Id, receiveEpisodeId));
    }

    public void StopTransmit(ChannelRecordingDescriptor channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        EnqueueOperation(channel.Id, () => service.StopTransmitAsync(channel.Id));
    }

    public void Dispose()
        => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        work.Writer.TryComplete();
        try
        {
            await workLoop.ConfigureAwait(false);
            await service.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            store.RecordingFinalized -= HandleStoreRecordingFinalized;
            await store.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void EnqueueSamples(RecordingWorkItem item)
    {
        if (item.SampleCount == 0 || Volatile.Read(ref disposed) != 0)
        {
            item.ReturnSamples();
            return;
        }
        if (Interlocked.Increment(ref queuedSampleWork) <= recordingWorkCapacity &&
            work.Writer.TryWrite(item))
        {
            return;
        }

        Interlocked.Decrement(ref queuedSampleWork);
        item.ReturnSamples();
        ReportFault(
            item.ChannelId,
            new IOException("The bounded recording write queue is full; TAR was stopped to protect live audio."));
    }

    private void EnqueueOperation(ChannelId channelId, Func<ValueTask> operation)
    {
        if (Volatile.Read(ref disposed) != 0)
            return;
        _ = work.Writer.TryWrite(RecordingWorkItem.Operation(channelId, operation));
    }

    private void Flush()
    {
        // Compatibility reads are deliberately synchronous and never run on
        // the receive/audio producer path. All recording control and sample
        // work enters the ordered queue without blocking its producer.
        if (Volatile.Read(ref disposed) == 0)
            FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref disposed) != 0)
            return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!work.Writer.TryWrite(
                RecordingWorkItem.Operation(default, static () => ValueTask.CompletedTask, completion)))
        {
            if (Volatile.Read(ref disposed) != 0)
                return;
            throw new InvalidOperationException("The recording work queue is no longer accepting barriers.");
        }
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessWorkAsync()
    {
        Exception? terminalFailure = null;
        try
        {
            await foreach (RecordingWorkItem item in work.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                item.ReleaseQueueSlot(ref queuedSampleWork);
                Exception? failure = null;
                ChannelRecordingState before = CaptureWorkState(item.ChannelId);
                try
                {
                    await ExecuteWorkItemAsync(item).ConfigureAwait(false);
                    if (item.Kind != RecordingWorkKind.Operation && store.IsRecording(item.ChannelId))
                    {
                        lock (recordingStateSync)
                            recordingFaults.Remove(item.ChannelId);
                    }
                }
                catch (Exception exception)
                {
                    failure = exception;
                    ReportFault(item.ChannelId, exception);
                }
                finally
                {
                    PublishStateChangeIfNeeded(item.ChannelId, before);
                    item.ReturnSamples();
                }

                if (failure is null || IsRecordingStorageFailure(failure))
                    item.Completion?.TrySetResult();
                else
                    item.Completion?.TrySetException(failure);
            }
        }
        catch (Exception exception)
        {
            terminalFailure = exception;
            throw;
        }
        finally
        {
            work.Writer.TryComplete(terminalFailure);
            Exception abandonedFailure = terminalFailure ??
                new OperationCanceledException("The recording work queue stopped before completing this item.");
            while (work.Reader.TryRead(out RecordingWorkItem abandoned))
            {
                abandoned.ReleaseQueueSlot(ref queuedSampleWork);
                abandoned.ReturnSamples();
                abandoned.Completion?.TrySetException(abandonedFailure);
            }
        }
    }

    private void ReportFault(ChannelId channelId, Exception exception)
    {
        ChannelRecordingState before;
        lock (recordingStateSync)
        {
            before = recordingStates.GetValueOrDefault(channelId);
            recordingFaults[channelId] = exception.Message;
        }
        PublishStateChangeIfNeeded(channelId, before);
        try
        {
            faultHandler?.Invoke(channelId, exception);
        }
        catch
        {
            // Diagnostics cannot take down the single-owner recording queue or
            // strand its pooled sample buffers and barriers.
        }
    }

    private ChannelRecordingState CaptureWorkState(ChannelId channelId)
    {
        if (channelId == default)
            return default;
        string? fault;
        lock (recordingStateSync)
            fault = recordingFaults.GetValueOrDefault(channelId);
        return new ChannelRecordingState(store.IsRecording(channelId), store.IsFinalizing(channelId), fault);
    }

    private void PublishStateChangeIfNeeded(
        ChannelId channelId,
        ChannelRecordingState before)
    {
        if (channelId == default)
            return;
        ChannelRecordingState after = CaptureWorkState(channelId);
        if (after == before)
            return;

        lock (recordingStateSync)
        {
            if (after == default)
                recordingStates.Remove(channelId);
            else
                recordingStates[channelId] = after;
        }
        foreach (Action<ChannelId> observer in
                 RecordingStateChanged?.GetInvocationList().Cast<Action<ChannelId>>() ?? [])
        {
            try
            {
                observer(channelId);
            }
            catch
            {
                // Projection observers cannot interrupt recording ownership.
            }
        }
    }

    private void HandleStoreRecordingFinalized(
        object? sender,
        RecordingFinalizationResult result)
    {
        if (Volatile.Read(ref disposed) != 0 || result.ChannelId is not ChannelId channelId)
            return;

        ChannelRecordingState before;
        lock (recordingStateSync)
            before = recordingStates.GetValueOrDefault(channelId);
        PublishStateChangeIfNeeded(channelId, before);
    }

    private ValueTask ExecuteWorkItemAsync(RecordingWorkItem item)
        => item.Kind switch
        {
            RecordingWorkKind.ReceiveSamples => service.WriteReceiveSamplesAsync(
                item.Channel!,
                item.EpisodeStreamId,
                item.PhysicalStreamId,
                item.SourceId,
                item.SampleMemory,
                item.ReceiveEpisodeId),
            RecordingWorkKind.TransmitSamples => service.WriteTransmitSamplesAsync(
                item.Channel!,
                item.PhysicalStreamId,
                item.SourceId,
                item.SampleMemory),
            _ => item.DeferredOperation!()
        };

    private RentedSamples RentSamples(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty)
            return default;
        short[] buffer = samplePool.Rent(samples.Length);
        samples.CopyTo(buffer);
        return new RentedSamples(samplePool, buffer, samples.Length);
    }

    private enum RecordingWorkKind
    {
        Operation,
        ReceiveSamples,
        TransmitSamples
    }

    private readonly record struct RentedSamples(
        ArrayPool<short>? Pool,
        short[]? Buffer,
        int Count);

    private readonly record struct RecordingWorkItem(
        RecordingWorkKind Kind,
        ChannelId ChannelId,
        ChannelRecordingDescriptor? Channel,
        uint EpisodeStreamId,
        uint PhysicalStreamId,
        uint SourceId,
        long? ReceiveEpisodeId,
        RentedSamples Samples,
        Func<ValueTask>? DeferredOperation,
        TaskCompletionSource? Completion)
    {
        public int SampleCount => Samples.Count;
        public bool IsSample => Kind is RecordingWorkKind.ReceiveSamples or RecordingWorkKind.TransmitSamples;
        public ReadOnlyMemory<short> SampleMemory => Samples.Buffer.AsMemory(0, Samples.Count);

        public static RecordingWorkItem Operation(
            ChannelId channelId,
            Func<ValueTask> operation,
            TaskCompletionSource? completion = null)
            => new(RecordingWorkKind.Operation, channelId, null, 0, 0, 0, null, default, operation, completion);

        public static RecordingWorkItem ReceiveSamples(
            ChannelRecordingDescriptor channel,
            uint episodeStreamId,
            uint physicalStreamId,
            uint sourceId,
            long? receiveEpisodeId,
            RentedSamples samples)
            => new(
                RecordingWorkKind.ReceiveSamples,
                channel.Id,
                channel,
                episodeStreamId,
                physicalStreamId,
                sourceId,
                receiveEpisodeId,
                samples,
                null,
                null);

        public static RecordingWorkItem TransmitSamples(
            ChannelRecordingDescriptor channel,
            uint streamId,
            uint sourceId,
            RentedSamples samples)
            => new(
                RecordingWorkKind.TransmitSamples,
                channel.Id,
                channel,
                0,
                streamId,
                sourceId,
                null,
                samples,
                null,
                null);

        public void ReturnSamples()
        {
            if (Samples.Buffer is not null)
                Samples.Pool!.Return(Samples.Buffer);
        }

        public void ReleaseQueueSlot(ref int queuedSampleWork)
        {
            if (IsSample)
                Interlocked.Decrement(ref queuedSampleWork);
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

internal readonly record struct ChannelRecordingState(bool IsRecording, bool IsFinalizing, string? Fault = null);
