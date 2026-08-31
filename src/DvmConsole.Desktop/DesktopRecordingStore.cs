using DvmConsole.Application;
using DvmConsole.Audio;
using DvmConsole.Core.Runtime;
using DvmConsole.Media;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DvmConsole.Desktop;

/// <summary>
/// Desktop TAR persistence behind the portable recording-store contract.
/// Capture lifecycle belongs to Application; this adapter owns paths, crash
/// recovery, Opus finalization, embedded metadata, and catalog access.
/// </summary>
internal sealed class DesktopRecordingStore : IRecordingStore, IAsyncDisposable
{
    private readonly object sync = new();
    private readonly object finalizationScheduleSync = new();
    private readonly RecordingCatalogStore catalogStore = new();
    private readonly RecordingPathPolicy pathPolicy = new();
    private readonly RecordingMetadataFactory metadataFactory = new();
    private readonly Dictionary<RecordingId, WriteHandle> active = [];
    private readonly HashSet<Guid> scheduledFinalizations = [];
    private readonly Dictionary<Guid, ChannelId> scheduledFinalizationChannels = [];
    private readonly RecordingFinalizationQueue finalizationQueue;
    private readonly Func<
        RecordingFinalizationDescriptor,
        ChannelId?,
        CancellationToken,
        Task<RecordingFinalizationResult>> finalizeRecording;
    private readonly Action<ChannelId, Exception>? faultHandler;
    private RecordingFinalizationSpool finalizationSpool;
    private string rootPath;
    private int retentionDays;
    private int disposed;

    public DesktopRecordingStore(
        string rootPath,
        Action<ChannelId, Exception>? faultHandler,
        int retentionDays,
        int finalizationQueueCapacity = RecordingFinalizationQueue.DefaultCapacity,
        Func<
            RecordingFinalizationDescriptor,
            ChannelId?,
            CancellationToken,
            Task<RecordingFinalizationResult>>? finalizeRecording = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (retentionDays < 0)
            throw new ArgumentOutOfRangeException(nameof(retentionDays));

        this.rootPath = Path.GetFullPath(rootPath);
        this.faultHandler = faultHandler;
        this.retentionDays = retentionDays;
        this.finalizeRecording = finalizeRecording ?? FinalizeRecordingAsync;
        finalizationSpool = new RecordingFinalizationSpool(this.rootPath);
        finalizationSpool.RecoverOrphanedWaveFiles();
        finalizationQueue = new RecordingFinalizationQueue(finalizationQueueCapacity);
        finalizationQueue.Finalized += HandleRecordingFinalized;
        try
        {
            SchedulePendingFinalizations(excludedJobId: null);
        }
        catch (Exception constructionException)
        {
            finalizationQueue.Finalized -= HandleRecordingFinalized;
            try
            {
                finalizationQueue.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    "Recording recovery and finalization-queue rollback both failed.",
                    constructionException,
                    cleanupException);
            }
            throw;
        }
    }

    public event EventHandler<RecordingFinalizationResult>? RecordingFinalized;

    public int RetentionDays
    {
        get => Volatile.Read(ref retentionDays);
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            Volatile.Write(ref retentionDays, value);
        }
    }

    public string RootPath
    {
        get
        {
            lock (sync)
                return rootPath;
        }
    }

    public IReadOnlyList<string> ActivePaths
    {
        get
        {
            lock (sync)
                return active.Values.Select(handle => handle.WavePath).ToArray();
        }
    }

    public RecordingFinalizationSpoolHealth FinalizationHealth
        => finalizationSpool.GetHealth();

    public int ScheduledFinalizationCount
    {
        get
        {
            lock (finalizationScheduleSync)
                return scheduledFinalizations.Count;
        }
    }

    public bool IsRecording(ChannelId channelId)
    {
        lock (sync)
            return active.Values.Any(handle => handle.ChannelId == channelId);
    }

    public bool IsRecordingEpisode(ChannelId channelId, long episodeId)
    {
        lock (sync)
            return active.Values.Any(handle =>
                handle.ChannelId == channelId &&
                handle.Context is RecordingCaptureContext context &&
                (context.ReceiveEpisodeId ?? context.StreamId) == episodeId);
    }

    public bool IsFinalizing(ChannelId channelId)
    {
        lock (finalizationScheduleSync)
            return scheduledFinalizationChannels.ContainsValue(channelId);
    }

    public ValueTask<IRecordingWriteHandle> CreateAsync(
        CallId callId,
        ChannelId channelId,
        DateTimeOffset startedAt,
        string mediaType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        RecordingId id = RecordingId.New();
        string captureRoot;
        lock (sync)
            captureRoot = rootPath;
        string activeDirectory = Path.Combine(captureRoot, ".active");
        Directory.CreateDirectory(activeDirectory);
        string wavePath = Path.Combine(activeDirectory, $"{id.Value:N}.wav");
        var stream = new FileStream(
            wavePath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous);
        var handle = new WriteHandle(
            this,
            id,
            callId,
            channelId,
            startedAt,
            mediaType.Trim(),
            captureRoot,
            wavePath,
            stream);
        lock (sync)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                stream.Dispose();
                throw new ObjectDisposedException(nameof(DesktopRecordingStore));
            }
            active.Add(id, handle);
        }
        return ValueTask.FromResult<IRecordingWriteHandle>(handle);
    }

    public ValueTask<Stream> OpenReadAsync(
        RecordingId id,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallRecordingMetadata metadata = LoadRecordings(cancellationToken)
            .FirstOrDefault(item => RecordingIdentityEquals(item, id))
            ?? throw new KeyNotFoundException($"Recording '{id}' is not in the desktop catalog.");
        if (!TryGetRecordingPath(metadata, out string path))
            throw new FileNotFoundException("The recording media is unavailable.", metadata.FilePath);
        Stream stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous);
        return ValueTask.FromResult(stream);
    }

    public async IAsyncEnumerable<RecordingDescriptor> ListAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (CallRecordingMetadata metadata in LoadRecordings(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParse(metadata.RecordingId, out Guid id))
                continue;
            yield return new RecordingDescriptor(
                new RecordingId(id),
                new CallId(id),
                default,
                metadata.UtcStartTime,
                TimeSpan.FromMilliseconds(Math.Max(0, metadata.DurationMs)),
                "audio/ogg; codecs=opus",
                Math.Max(0, metadata.FileSizeBytes),
                metadata.IsPlayable,
                metadata.IsPlayable ? null : "Recording media is not playable.");
            await Task.Yield();
        }
    }

    public IReadOnlyList<CallRecordingMetadata> LoadRecordings(
        CancellationToken cancellationToken = default)
        => catalogStore.Load(RootPath, cancellationToken);

    public Task<IReadOnlyList<CallRecordingMetadata>> LoadRecordingsAsync(
        CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<CallRecordingMetadata>>(
            () => LoadRecordings(cancellationToken),
            cancellationToken);

    public Task<RecordingCatalogScanResult> LoadAndPruneRecordingsAsync(
        bool pruneExpired,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
        => Task.Run(
            () => catalogStore.Scan(
                RootPath,
                pruneExpired ? retentionDays : 0,
                now ?? DateTimeOffset.UtcNow,
                cancellationToken),
            cancellationToken);

    public int PruneExpired(DateTimeOffset? now = null)
    {
        int days = retentionDays;
        return days <= 0
            ? 0
            : catalogStore.Scan(
                RootPath,
                days,
                now ?? DateTimeOffset.UtcNow,
                CancellationToken.None).PrunedFiles;
    }

    public bool DeleteRecording(CallRecordingMetadata metadata)
        => catalogStore.Delete(RootPath, metadata);

    public bool TryGetRecordingPath(CallRecordingMetadata metadata, out string recordingPath)
        => catalogStore.TryGetExistingPath(RootPath, metadata, out recordingPath);

    public bool TrySetRootPath(string requestedPath, out string errorMessage)
    {
        errorMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            errorMessage = "The recording folder cannot be empty.";
            return false;
        }

        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(requestedPath.Trim());
            Directory.CreateDirectory(normalizedPath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or PathTooLongException)
        {
            errorMessage = $"The recording folder is unavailable: {exception.Message}";
            return false;
        }

        lock (sync)
        {
            if (active.Count > 0)
            {
                errorMessage = "Stop active recordings before changing the recording folder.";
                return false;
            }
            if (finalizationSpool.GetHealth().PendingJobs > 0)
            {
                errorMessage = "Wait for pending recording finalization before changing the recording folder.";
                return false;
            }
            rootPath = normalizedPath;
            finalizationSpool = new RecordingFinalizationSpool(rootPath);
        }
        finalizationSpool.RecoverOrphanedWaveFiles();
        SchedulePendingFinalizations(excludedJobId: null);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        WriteHandle[] abandoned;
        lock (sync)
        {
            abandoned = active.Values.ToArray();
            active.Clear();
        }
        foreach (WriteHandle handle in abandoned)
            await handle.CloseStreamAsync(CancellationToken.None).ConfigureAwait(false);

        await finalizationQueue.DisposeAsync().ConfigureAwait(false);
        finalizationQueue.Finalized -= HandleRecordingFinalized;
    }

    private void UpdateContext(WriteHandle handle, RecordingCaptureContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (handle.Sync)
        {
            handle.Context = context;
            RecordingFinalizationDescriptor descriptor = CreateSnapshot(handle, context.ObservedAt);
            handle.OutputPath = pathPolicy.CreatePath(descriptor);
            descriptor = descriptor with { OutputPath = handle.OutputPath };
            finalizationSpool.PersistCaptureSnapshot(descriptor);
        }
    }

    private async ValueTask CommitAsync(
        WriteHandle handle,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await handle.CloseStreamAsync(cancellationToken).ConfigureAwait(false);
        RemoveActive(handle);
        RecordingCaptureContext context = handle.Context
            ?? throw new InvalidOperationException("Recording capture metadata was not supplied.");
        DateTimeOffset sampleEnd = handle.StartedAt.Add(duration);
        DateTimeOffset endedAt = context.ObservedAt > sampleEnd ? context.ObservedAt : sampleEnd;
        RecordingFinalizationDescriptor descriptor = CreateSnapshot(handle, endedAt);
        handle.OutputPath = pathPolicy.CreatePath(descriptor);
        descriptor = descriptor with { OutputPath = handle.OutputPath };
        finalizationSpool.PersistReady(descriptor);
        TryScheduleFinalization(descriptor, handle.ChannelId);
    }

    private async ValueTask AbortAsync(
        WriteHandle handle,
        CancellationToken cancellationToken)
    {
        await handle.CloseStreamAsync(cancellationToken).ConfigureAwait(false);
        RemoveActive(handle);
    }

    private void RemoveActive(WriteHandle handle)
    {
        lock (sync)
        {
            if (active.TryGetValue(handle.Id, out WriteHandle? current) &&
                ReferenceEquals(current, handle))
            {
                active.Remove(handle.Id);
            }
        }
    }

    private RecordingFinalizationDescriptor CreateSnapshot(
        WriteHandle handle,
        DateTimeOffset endedAt)
    {
        RecordingCaptureContext context = handle.Context
            ?? throw new InvalidOperationException("Recording capture metadata was not supplied.");
        RadioMediaProtocol protocol = context.Channel.Protocol switch
        {
            ChannelProtocol.Dmr => RadioMediaProtocol.Dmr,
            ChannelProtocol.P25 => RadioMediaProtocol.P25,
            ChannelProtocol.Nxdn => RadioMediaProtocol.Nxdn,
            ChannelProtocol.Analog => RadioMediaProtocol.Analog,
            _ => throw new ArgumentOutOfRangeException(
                nameof(handle),
                context.Channel.Protocol,
                "The recording protocol is not supported.")
        };
        return new RecordingFinalizationDescriptor(
            handle.Id.Value,
            context.ObservedAt,
            handle.CaptureRoot,
            handle.WavePath,
            handle.OutputPath,
            PcmAudioFormat.Voice8KhzMono16Bit.SampleRate,
            PcmAudioFormat.Voice8KhzMono16Bit.Channels,
            PcmAudioFormat.Voice8KhzMono16Bit.BitsPerSample,
            protocol,
            context.Channel.Mode.ToUpperInvariant(),
            context.Direction,
            context.RecordingSourceType,
            handle.StartedAt,
            endedAt,
            context.Channel.SystemName,
            context.Channel.Name,
            context.Channel.DestinationId,
            context.SourceId,
            context.SubscriberAlias,
            context.StreamId,
            context.StreamIds,
            context.Encryption.IsSecure,
            context.Encryption.AlgorithmId,
            context.Encryption.KeyId,
            context.RetentionDays,
            context.Encryption.IsKnown,
            context.ReceiveEpisodeId,
            handle.Id.Value);
    }

    private void HandleRecordingFinalized(object? sender, RecordingFinalizationResult result)
    {
        if (result.Descriptor is RecordingFinalizationDescriptor descriptor)
        {
            lock (finalizationScheduleSync)
            {
                scheduledFinalizations.Remove(descriptor.JobId);
                scheduledFinalizationChannels.Remove(descriptor.JobId);
            }
            if (result.Error is null)
                finalizationSpool.Complete(descriptor);
            else if (result.Error is not (IOException or UnauthorizedAccessException))
                finalizationSpool.Quarantine(descriptor, result.Diagnostic ?? result.Error.Message);
        }
        if (result.Error is Exception exception && result.ChannelId is ChannelId channelId)
            faultHandler?.Invoke(channelId, exception);

        if (Volatile.Read(ref disposed) == 0)
            SchedulePendingFinalizations(result.Error is null ? null : result.Descriptor?.JobId);

        RecordingFinalized?.Invoke(this, result);
    }

    private void SchedulePendingFinalizations(Guid? excludedJobId)
    {
        foreach (RecordingFinalizationDescriptor descriptor in finalizationSpool.LoadReadyFinalizations())
        {
            if (descriptor.JobId == excludedJobId)
                continue;
            if (!TryScheduleFinalization(descriptor, channelId: null))
                return;
        }
    }

    private bool TryScheduleFinalization(
        RecordingFinalizationDescriptor descriptor,
        ChannelId? channelId)
    {
        lock (finalizationScheduleSync)
        {
            if (!scheduledFinalizations.Add(descriptor.JobId))
                return true;
            if (channelId is not null)
                scheduledFinalizationChannels[descriptor.JobId] = channelId.Value;
            try
            {
                bool scheduled = finalizationQueue.TryEnqueue(new RecordingFinalizationJob(
                    descriptor.StreamId,
                    async cancellationToken => (await finalizeRecording(
                        descriptor,
                        channelId,
                        cancellationToken).ConfigureAwait(false)) with
                    {
                        Descriptor = descriptor
                    }));
                if (!scheduled)
                {
                    scheduledFinalizations.Remove(descriptor.JobId);
                    scheduledFinalizationChannels.Remove(descriptor.JobId);
                }
                return scheduled;
            }
            catch
            {
                scheduledFinalizations.Remove(descriptor.JobId);
                scheduledFinalizationChannels.Remove(descriptor.JobId);
                throw;
            }
        }
    }

    private async Task<RecordingFinalizationResult> FinalizeRecordingAsync(
        RecordingFinalizationDescriptor snapshot,
        ChannelId? channelId,
        CancellationToken cancellationToken)
    {
        string? finalPath = null;
        string? temporaryOpusPath = null;
        bool completed = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(snapshot.OutputPath))
            {
                if (catalogStore.TryRead(
                        snapshot.OutputPath,
                        snapshot.RootPath,
                        out CallRecordingMetadata completedMetadata))
                {
                    completed = true;
                    return new RecordingFinalizationResult(
                        completedMetadata,
                        snapshot.StreamId,
                        "Recovered a completed recording from the finalization spool.",
                        null)
                    { ChannelId = channelId };
                }
                TryDelete(snapshot.OutputPath);
            }
            if (!File.Exists(snapshot.WavePath))
                throw new InvalidDataException("The finalization source WAV is missing.");
            PcmWavTrimAnalysis trimAnalysis;
            using (var wave = new FileStream(
                       snapshot.WavePath,
                       FileMode.Open,
                       FileAccess.ReadWrite,
                       FileShare.Read))
            {
                PcmWavFileWriter.RepairInterruptedStream(wave, snapshot.Format);
                wave.Flush(flushToDisk: true);
                trimAnalysis = PcmWavSilenceTrimmer.Analyze(wave, snapshot.Format);
            }
            PcmWavTrimResult trim = trimAnalysis.Result;
            if (trim.OutputSamples <= 0 || trim.ActiveSampleCount <= 0 || trim.PeakAmplitude <= 0)
            {
                return new RecordingFinalizationResult(
                    null,
                    snapshot.StreamId,
                    "Recording contained no playable voice activity.",
                    null)
                { ChannelId = channelId };
            }

            TimeSpan sampleDuration = TimeSpan.FromSeconds(
                trim.OriginalSamples / (double)(snapshot.Format.SampleRate * snapshot.Format.Channels));
            DateTimeOffset sampleDerivedEnd = snapshot.UtcStartTime.Add(sampleDuration);
            if (snapshot.UtcEndTime < sampleDerivedEnd)
                snapshot = snapshot with { UtcEndTime = sampleDerivedEnd };

            finalPath = snapshot.OutputPath;
            CallRecordingMetadata metadata = metadataFactory.Create(snapshot, trim, finalPath);
            temporaryOpusPath = $"{finalPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            await DesktopRecordingFileCodec.EncodeWaveRangeAsync(
                snapshot.WavePath,
                temporaryOpusPath,
                trimAnalysis.StartSample,
                trim.OutputSamples,
                tags: catalogStore.CreateTags(metadata),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!await ContainsDecodableAudioAsync(temporaryOpusPath, cancellationToken).ConfigureAwait(false))
            {
                return new RecordingFinalizationResult(
                    null,
                    snapshot.StreamId,
                    "Encoded recording did not contain decodable audio.",
                    null)
                { ChannelId = channelId };
            }

            File.Move(temporaryOpusPath, finalPath);
            temporaryOpusPath = null;
            if (!catalogStore.TryRead(finalPath, snapshot.RootPath, out CallRecordingMetadata persistedMetadata) ||
                !RecordingCatalogStore.GetCatalogKey(persistedMetadata).Equals(
                    RecordingCatalogStore.GetCatalogKey(metadata),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The embedded TAR metadata could not be verified.");
            }
            metadata.FileSizeBytes = new FileInfo(finalPath).Length;
            completed = true;
            return new RecordingFinalizationResult(metadata, snapshot.StreamId, null, null)
            { ChannelId = channelId };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or JsonException or FormatException or NotSupportedException)
        {
            return new RecordingFinalizationResult(null, snapshot.StreamId, exception.Message, exception)
            { ChannelId = channelId };
        }
        finally
        {
            if (temporaryOpusPath is not null)
                TryDelete(temporaryOpusPath);
            if (!completed && finalPath is not null)
                TryDelete(finalPath);
        }
    }

    private static async Task<bool> ContainsDecodableAudioAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length <= 0)
            return false;
        await using FileStream source = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using IAudioPcmStreamReader reader = await PcmStreamDecoder.OpenAsync(source, cancellationToken).ConfigureAwait(false);
        short[] samples = new short[1600];
        while (true)
        {
            int count = await reader.ReadSamplesAsync(samples, cancellationToken).ConfigureAwait(false);
            if (count == 0)
                return false;
            if (samples.AsSpan(0, count).IndexOfAnyExcept((short)0) >= 0)
                return true;
        }
    }

    private static bool RecordingIdentityEquals(CallRecordingMetadata metadata, RecordingId id)
        => Guid.TryParse(metadata.RecordingId, out Guid parsed) && parsed == id.Value;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class WriteHandle : IRecordingWriteHandle
    {
        private readonly DesktopRecordingStore owner;
        private readonly FileStream stream;
        private int completed;
        private int streamClosed;

        public WriteHandle(
            DesktopRecordingStore owner,
            RecordingId id,
            CallId callId,
            ChannelId channelId,
            DateTimeOffset startedAt,
            string mediaType,
            string captureRoot,
            string wavePath,
            FileStream stream)
        {
            this.owner = owner;
            Id = id;
            CallId = callId;
            ChannelId = channelId;
            StartedAt = startedAt;
            MediaType = mediaType;
            CaptureRoot = captureRoot;
            WavePath = wavePath;
            this.stream = stream;
        }

        public object Sync { get; } = new();
        public RecordingId Id { get; }
        public CallId CallId { get; }
        public ChannelId ChannelId { get; }
        public DateTimeOffset StartedAt { get; }
        public string MediaType { get; }
        public string CaptureRoot { get; }
        public string WavePath { get; }
        public string OutputPath { get; set; } = string.Empty;
        public RecordingCaptureContext? Context { get; set; }
        public Stream Stream => stream;

        public void UpdateContext(RecordingCaptureContext context)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref completed) != 0, this);
            owner.UpdateContext(this, context);
        }

        public ValueTask CommitAsync(
            TimeSpan duration,
            CancellationToken cancellationToken = default)
        {
            if (duration < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(duration));
            if (Interlocked.CompareExchange(ref completed, 1, 0) != 0)
                return ValueTask.CompletedTask;
            return owner.CommitAsync(this, duration, cancellationToken);
        }

        public ValueTask AbortAsync(
            string? fault,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref completed, 1, 0) != 0)
                return ValueTask.CompletedTask;
            return owner.AbortAsync(this, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref completed, 1, 0) == 0)
                await owner.AbortAsync(this, CancellationToken.None).ConfigureAwait(false);
            else
                await CloseStreamAsync(CancellationToken.None).ConfigureAwait(false);
        }

        public async ValueTask CloseStreamAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref streamClosed, 1) != 0)
                return;
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
