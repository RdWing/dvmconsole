using DvmConsole.Audio;
using DvmConsole.FneClient;
using DvmConsole.Media;
using System.Text.Json;

namespace DvmConsole.Desktop;

// Owns per-channel receive and console-transmit recordings fed by PCM. A
// recording starts on the first frame for an active stream and is finalized
// after its confirmed terminator, call stop, channel stop, or application
// shutdown.
public sealed class CallRecordingManager : IDisposable, IAsyncDisposable
{
    public const int DefaultRetentionDays = 7;

    private readonly object sync = new();
    private readonly RecordingCatalogStore catalogStore = new();
    private readonly RecordingPathPolicy pathPolicy = new();
    private readonly RecordingMetadataFactory metadataFactory = new();
    private string rootPath;
    private readonly Action<ChannelViewModel, Exception>? faultHandler;
    private readonly Func<ChannelViewModel, uint, bool> shouldRecordSource;
    private int retentionDays;
    private readonly Dictionary<(ChannelViewModel Channel, long EpisodeId), ActiveRecording> active = [];
    private readonly Dictionary<ChannelViewModel, ActiveRecording> activeTransmit = [];
    private readonly Dictionary<(ChannelViewModel Channel, long EpisodeId), TrafficEncryptionObservationState> encryptionStates = [];
    private readonly object finalizationScheduleSync = new();
    private readonly HashSet<Guid> scheduledFinalizations = [];
    private readonly RecordingFinalizationQueue finalizationQueue;
    private readonly Func<
        RecordingFinalizationDescriptor,
        ChannelViewModel?,
        CancellationToken,
        Task<RecordingFinalizationResult>> finalizeRecording;
    private RecordingFinalizationSpool finalizationSpool;
    private bool disposed;

    public CallRecordingManager(
        string rootPath,
        Action<ChannelViewModel, Exception>? faultHandler = null,
        int retentionDays = DefaultRetentionDays,
        Func<ChannelViewModel, uint, bool>? shouldRecordSource = null)
        : this(
            rootPath,
            faultHandler,
            retentionDays,
            shouldRecordSource,
            RecordingFinalizationQueue.DefaultCapacity,
            finalizeRecording: null)
    {
    }

    internal CallRecordingManager(
        string rootPath,
        Action<ChannelViewModel, Exception>? faultHandler,
        int retentionDays,
        Func<ChannelViewModel, uint, bool>? shouldRecordSource,
        int finalizationQueueCapacity,
        Func<
            RecordingFinalizationDescriptor,
            ChannelViewModel?,
            CancellationToken,
            Task<RecordingFinalizationResult>>? finalizeRecording)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (retentionDays < 0)
            throw new ArgumentOutOfRangeException(nameof(retentionDays));

        this.rootPath = System.IO.Path.GetFullPath(rootPath);
        this.faultHandler = faultHandler;
        this.retentionDays = retentionDays;
        this.shouldRecordSource = shouldRecordSource ?? ((_, _) => true);
        this.finalizeRecording = finalizeRecording ?? FinalizeRecordingAsync;
        finalizationSpool = new RecordingFinalizationSpool(this.rootPath);
        finalizationSpool.RecoverOrphanedWaveFiles();
        finalizationQueue = new RecordingFinalizationQueue(finalizationQueueCapacity);
        finalizationQueue.Finalized += HandleRecordingFinalized;
        try
        {
            ResumePendingFinalizations();
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
        get => retentionDays;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            retentionDays = value;
        }
    }

    public IReadOnlyList<string> ActivePaths
    {
        get
        {
            lock (sync)
                return active.Values
                    .Concat(activeTransmit.Values)
                    .Select(recording => recording.Writer.Path)
                    .ToArray();
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

    internal RecordingFinalizationSpoolHealth FinalizationHealth
        => finalizationSpool.GetHealth();

    internal int ScheduledFinalizationCount
    {
        get
        {
            lock (finalizationScheduleSync)
                return scheduledFinalizations.Count;
        }
    }

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
            if (active.Count > 0 || activeTransmit.Count > 0)
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
        ResumePendingFinalizations();

        return true;
    }

    public IReadOnlyList<CallRecordingMetadata> LoadRecordings()
        => LoadRecordings(CancellationToken.None);

    private IReadOnlyList<CallRecordingMetadata> LoadRecordings(CancellationToken cancellationToken)
        => catalogStore.Load(rootPath, cancellationToken);

    public Task<IReadOnlyList<CallRecordingMetadata>> LoadRecordingsAsync(
        CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<CallRecordingMetadata>>(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return LoadRecordings(cancellationToken);
            },
            cancellationToken);

    internal Task<RecordingCatalogScanResult> LoadAndPruneRecordingsAsync(
        bool pruneExpired,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
        => Task.Run(
            () => catalogStore.Scan(
                rootPath,
                pruneExpired ? retentionDays : 0,
                now ?? DateTimeOffset.UtcNow,
                cancellationToken),
            cancellationToken);

    public int PruneExpired(DateTimeOffset? now = null)
    {
        if (retentionDays <= 0)
            return 0;

        return catalogStore.Scan(
            rootPath,
            retentionDays,
            now ?? DateTimeOffset.UtcNow,
            CancellationToken.None).PrunedFiles;
    }

    public bool DeleteRecording(CallRecordingMetadata metadata)
        => catalogStore.Delete(rootPath, metadata);

    public bool TryGetRecordingPath(CallRecordingMetadata metadata, out string recordingPath)
        => catalogStore.TryGetExistingPath(rootPath, metadata, out recordingPath);

    public void WriteSamples(ChannelViewModel channel, ReadOnlyMemory<short> samples)
        => WriteSamples(channel, channel.StreamId ?? 0, channel.SourceId ?? 0, samples);

    public void WriteSamples(
        ChannelViewModel channel,
        uint streamId,
        uint sourceId,
        ReadOnlyMemory<short> samples)
        => WriteEpisodeSamples(channel, streamId, streamId, sourceId, samples);

    public void WriteEpisodeSamples(
        ChannelViewModel channel,
        uint episodeStreamId,
        uint physicalStreamId,
        uint sourceId,
        ReadOnlyMemory<short> samples,
        long? receiveEpisodeId = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (samples.IsEmpty || !channel.IsRecordingEnabled || episodeStreamId == 0)
            return;

        long recordingEpisodeId = receiveEpisodeId ?? episodeStreamId;
        if (sourceId != 0 && !shouldRecordSource(channel, sourceId))
        {
            PendingFinalization? finalization;
            lock (sync)
                finalization = DetachRecording(channel, recordingEpisodeId);
            EnqueueFinalization(finalization);
            return;
        }

        ActiveRecording? recording = null;
        PendingFinalization? setupFinalization = null;
        Exception? setupFailure = null;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            try
            {
                var key = (channel, recordingEpisodeId);
                if (!active.TryGetValue(key, out recording))
                {
                    recording = CreateActiveRecording(
                        channel,
                        episodeStreamId,
                        sourceId == 0 ? null : sourceId,
                        "RX",
                        "InboundRadio",
                        receiveEpisodeId);
                    active[key] = recording;
                }

                bool streamIdentityChanged = recording.ObservePhysicalStream(physicalStreamId);
                if (streamIdentityChanged)
                    TryPersistActiveSnapshot(channel, recording);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                setupFinalization = DetachRecording(channel, recordingEpisodeId, recording);
                setupFailure = exception;
            }
        }
        EnqueueFinalization(setupFinalization);
        if (setupFailure is not null)
        {
            faultHandler?.Invoke(channel, setupFailure);
            return;
        }

        try
        {
            recording!.Write(samples.Span);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            PendingFinalization? failed;
            lock (sync)
                failed = DetachRecording(channel, recordingEpisodeId, recording);
            if (failed is not null)
            {
                EnqueueFinalization(failed);
                faultHandler?.Invoke(channel, exception);
            }
        }
    }

    public void WriteTransmitSamples(
        ChannelViewModel channel,
        uint streamId,
        uint sourceId,
        ReadOnlyMemory<short> samples)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (samples.IsEmpty || !channel.IsRecordingEnabled || streamId == 0)
            return;

        var finalizations = new List<PendingFinalization>(2);
        ActiveRecording? recording = null;
        Exception? setupFailure = null;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            try
            {
                if (!activeTransmit.TryGetValue(channel, out recording) ||
                    recording.StreamId != streamId)
                {
                    if (DetachTransmitRecording(channel) is PendingFinalization previous)
                        finalizations.Add(previous);
                    recording = CreateActiveRecording(
                        channel,
                        streamId,
                        sourceId == 0 ? null : sourceId,
                        "TX",
                        "ConsoleTx");
                    activeTransmit[channel] = recording;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                if (DetachTransmitRecording(channel, recording) is PendingFinalization failed)
                    finalizations.Add(failed);
                setupFailure = exception;
            }
        }
        foreach (PendingFinalization finalization in finalizations)
            EnqueueFinalization(finalization);
        if (setupFailure is not null)
        {
            faultHandler?.Invoke(channel, setupFailure);
            return;
        }

        try
        {
            recording!.Write(samples.Span);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            PendingFinalization? failed;
            lock (sync)
                failed = DetachTransmitRecording(channel, recording);
            if (failed is not null)
            {
                EnqueueFinalization(failed);
                faultHandler?.Invoke(channel, exception);
            }
        }
    }

    public bool ObserveTraffic(ChannelViewModel channel, FneTrafficFrame traffic)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(traffic);
        PendingFinalization? finalization = null;
        bool closed;
        lock (sync)
        {
            TrafficEncryptionObservationState encryptionState = GetEncryptionState(
                channel,
                traffic.StreamId);
            if (encryptionState.Observe(traffic))
            {
                if (active.TryGetValue((channel, (long)traffic.StreamId), out ActiveRecording? current) &&
                    current.SetEncryption(encryptionState.Encryption))
                {
                    TryPersistActiveSnapshot(channel, current);
                }
            }

            if (!ReceiveTrafficClassifier.IsTerminator(traffic))
                return false;

            finalization = DetachRecording(channel, traffic.StreamId);
            closed = finalization is not null;
            encryptionStates.Remove((channel, traffic.StreamId));
        }
        EnqueueFinalization(finalization);
        return closed;
    }

    public void ObserveEpisodeTraffic(
        ChannelViewModel channel,
        uint episodeStreamId,
        uint physicalStreamId,
        FneTrafficFrame traffic,
        long? receiveEpisodeId = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(traffic);
        if (episodeStreamId == 0)
            return;

        long recordingEpisodeId = receiveEpisodeId ?? episodeStreamId;
        lock (sync)
        {
            TrafficEncryptionObservationState encryptionState = GetEncryptionState(
                channel,
                recordingEpisodeId);
            if (encryptionState.Observe(traffic))
            {
                if (active.TryGetValue((channel, recordingEpisodeId), out ActiveRecording? current) &&
                    current.SetEncryption(encryptionState.Encryption))
                {
                    TryPersistActiveSnapshot(channel, current);
                }
            }

            if (active.TryGetValue((channel, recordingEpisodeId), out ActiveRecording? recording) &&
                recording.ObservePhysicalStream(physicalStreamId))
            {
                TryPersistActiveSnapshot(channel, recording);
            }
        }
    }

    public void StopChannel(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        List<PendingFinalization> finalizations;
        lock (sync)
        {
            finalizations = DetachRecordings(channel);
            foreach ((ChannelViewModel Channel, long EpisodeId) key in encryptionStates.Keys
                         .Where(key => ReferenceEquals(key.Channel, channel))
                         .ToArray())
            {
                encryptionStates.Remove(key);
            }
            if (DetachTransmitRecording(channel) is PendingFinalization transmit)
                finalizations.Add(transmit);
        }
        foreach (PendingFinalization finalization in finalizations)
            EnqueueFinalization(finalization);
    }

    public void StopStream(ChannelViewModel channel, uint streamId)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (streamId == 0)
            return;
        PendingFinalization? finalization;
        lock (sync)
            finalization = DetachRecording(channel, streamId);
        EnqueueFinalization(finalization);
    }

    public void StopEpisode(ChannelViewModel channel, long receiveEpisodeId)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (receiveEpisodeId <= 0)
            return;
        PendingFinalization? finalization;
        lock (sync)
            finalization = DetachRecording(channel, receiveEpisodeId);
        EnqueueFinalization(finalization);
    }

    public void StopTransmit(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        PendingFinalization? finalization;
        lock (sync)
            finalization = DetachTransmitRecording(channel);
        EnqueueFinalization(finalization);
    }

    public void Dispose()
        => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        List<PendingFinalization> finalizations;
        lock (sync)
        {
            if (disposed)
                return;

            finalizations = active
                .Select(entry => new PendingFinalization(entry.Key.Channel, entry.Value))
                .Concat(activeTransmit.Select(entry => new PendingFinalization(entry.Key, entry.Value)))
                .ToList();
            active.Clear();
            activeTransmit.Clear();
            encryptionStates.Clear();
            disposed = true;
        }

        foreach (PendingFinalization finalization in finalizations)
            EnqueueFinalization(finalization);
        await finalizationQueue.DisposeAsync().ConfigureAwait(false);
    }

    private void HandleRecordingFinalized(object? sender, RecordingFinalizationResult result)
    {
        if (result.Descriptor is RecordingFinalizationDescriptor descriptor)
        {
            lock (finalizationScheduleSync)
                scheduledFinalizations.Remove(descriptor.JobId);
            if (result.Error is null)
            {
                finalizationSpool.Complete(descriptor);
            }
            else if (result.Error is not (IOException or UnauthorizedAccessException))
            {
                finalizationSpool.Quarantine(descriptor, result.Diagnostic ?? result.Error.Message);
            }
        }
        if (result.Error is Exception exception && result.Channel is ChannelViewModel channel)
            faultHandler?.Invoke(channel, exception);

        bool isDisposed;
        lock (sync)
            isDisposed = disposed;
        if (!isDisposed)
        {
            SchedulePendingFinalizations(
                result.Error is null ? null : result.Descriptor?.JobId);
        }

        // Notify observers after internal queue bookkeeping is settled so they
        // see a consistent pending/scheduled state.
        RecordingFinalized?.Invoke(this, result);
    }

    private void ResumePendingFinalizations()
        => SchedulePendingFinalizations(excludedJobId: null);

    private void SchedulePendingFinalizations(Guid? excludedJobId)
    {
        foreach (RecordingFinalizationDescriptor descriptor in finalizationSpool.LoadReadyFinalizations())
        {
            if (descriptor.JobId == excludedJobId)
                continue;
            if (!TryScheduleFinalization(descriptor, channel: null))
                return;
        }
    }

    private bool TryScheduleFinalization(
        RecordingFinalizationDescriptor descriptor,
        ChannelViewModel? channel)
    {
        lock (finalizationScheduleSync)
        {
            if (!scheduledFinalizations.Add(descriptor.JobId))
                return true;

            try
            {
                bool scheduled = finalizationQueue.TryEnqueue(new RecordingFinalizationJob(
                    descriptor.StreamId,
                    async cancellationToken => (await finalizeRecording(
                        descriptor,
                        channel,
                        cancellationToken).ConfigureAwait(false)) with
                    {
                        Descriptor = descriptor
                    }));
                if (!scheduled)
                    scheduledFinalizations.Remove(descriptor.JobId);
                return scheduled;
            }
            catch
            {
                scheduledFinalizations.Remove(descriptor.JobId);
                throw;
            }
        }
    }

    private TrafficEncryptionObservationState GetEncryptionState(
        ChannelViewModel channel,
        long episodeId)
    {
        var key = (channel, episodeId);
        if (encryptionStates.TryGetValue(key, out TrafficEncryptionObservationState? state))
            return state;
        state = new TrafficEncryptionObservationState();
        encryptionStates.Add(key, state);
        return state;
    }

    private ActiveRecording CreateActiveRecording(
        ChannelViewModel channel,
        uint streamId,
        uint? sourceId,
        string direction,
        string recordingSourceType,
        long? receiveEpisodeId = null)
    {
        Guid jobId = Guid.NewGuid();
        var recording = new ActiveRecording(
            jobId,
            streamId,
            DateTimeOffset.UtcNow,
            sourceId,
            direction.Equals("RX", StringComparison.OrdinalIgnoreCase) && sourceId is uint callerId
                ? channel.ResolveSubscriberAlias(callerId)
                : string.Empty,
            direction,
            recordingSourceType,
            receiveEpisodeId,
            new PcmWavFileWriter(CreateTemporaryWavePath(jobId), PcmAudioFormat.Voice8KhzMono16Bit));

        try
        {
            if (direction.Equals("TX", StringComparison.OrdinalIgnoreCase))
            {
                bool secure = channel.Definition.IsEncrypted && channel.IsTransmitEncrypted;
                if (secure && EncryptionPresentation.TryParseConfiguredAlgorithm(
                        channel.Definition,
                        out byte algorithmId,
                        out ushort keyId))
                {
                    recording.SetEncryption(EncryptionSnapshot.FromConfiguration(
                        secure: true,
                        algorithmId,
                        keyId));
                }
                else
                {
                    recording.SetEncryption(EncryptionSnapshot.FromConfiguration(secure));
                }
            }
            else if (encryptionStates.TryGetValue(
                         (channel, receiveEpisodeId ?? streamId),
                         out TrafficEncryptionObservationState? encryptionState) &&
                     encryptionState.Encryption.IsKnown)
            {
                recording.SetEncryption(encryptionState.Encryption);
            }
            else if (FneTrafficProtocolMapper.FromChannelProtocol(channel.Definition.Protocol) ==
                     FneTrafficProtocol.Analog)
            {
                recording.SetEncryption(EncryptionSnapshot.FromConfiguration(secure: false));
            }

            PersistActiveSnapshot(channel, recording);
            return recording;
        }
        catch
        {
            // Closing repairs and flushes the WAV. Never delete a valid source
            // merely because its durable descriptor could not be written.
            recording.Writer.Dispose();
            throw;
        }
    }

    private string CreateTemporaryWavePath(Guid jobId)
    {
        string directory = Path.Combine(rootPath, ".active");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{jobId:N}.wav");
    }

    private List<PendingFinalization> DetachRecordings(ChannelViewModel channel)
    {
        var finalizations = new List<PendingFinalization>();
        foreach (long episodeId in active.Keys
                     .Where(key => ReferenceEquals(key.Channel, channel))
                     .Select(key => key.EpisodeId)
                     .ToArray())
        {
            if (DetachRecording(channel, episodeId) is PendingFinalization finalization)
                finalizations.Add(finalization);
        }
        return finalizations;
    }

    private PendingFinalization? DetachRecording(
        ChannelViewModel channel,
        long episodeId,
        ActiveRecording? expected = null)
    {
        if (!active.TryGetValue((channel, episodeId), out ActiveRecording? recording) ||
            (expected is not null && !ReferenceEquals(recording, expected)))
        {
            return null;
        }
        active.Remove((channel, episodeId));
        encryptionStates.Remove((channel, episodeId));
        return new PendingFinalization(channel, recording);
    }

    private void EnqueueFinalization(PendingFinalization? finalization)
    {
        if (finalization is null)
            return;

        ChannelViewModel channel = finalization.Channel;
        ActiveRecording recording = finalization.Recording;
        try
        {
            recording.CloseWriter();
            RecordingFinalizationDescriptor snapshot = CreatePersistableSnapshot(channel, recording);
            finalizationSpool.PersistReady(snapshot);
            TryScheduleFinalization(snapshot, channel);
        }
        catch (Exception exception) when (IsRecordingStorageFailure(exception))
        {
            // Report storage and validation failures without allowing a TAR
            // problem to escape through the operator's PTT event callback.
            // Any surviving capture snapshot remains available on restart.
            faultHandler?.Invoke(channel, exception);
        }
    }

    private void PersistActiveSnapshot(ChannelViewModel channel, ActiveRecording recording)
    {
        RecordingFinalizationDescriptor snapshot = CreatePersistableSnapshot(channel, recording);
        finalizationSpool.PersistCaptureSnapshot(snapshot);
    }

    private RecordingFinalizationDescriptor CreatePersistableSnapshot(
        ChannelViewModel channel,
        ActiveRecording recording)
    {
        RecordingFinalizationDescriptor snapshot = CreateSnapshot(channel, recording);
        recording.OutputPath = pathPolicy.CreatePath(snapshot);
        return snapshot with { OutputPath = recording.OutputPath };
    }

    private void TryPersistActiveSnapshot(ChannelViewModel channel, ActiveRecording recording)
    {
        try
        {
            PersistActiveSnapshot(channel, recording);
        }
        catch (Exception exception) when (IsRecordingStorageFailure(exception))
        {
            // Continue writing the source WAV. Its last durable descriptor is
            // safer than terminating and deleting an otherwise valid capture.
            faultHandler?.Invoke(channel, exception);
        }
    }

    private static bool IsRecordingStorageFailure(Exception exception)
        => exception is IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            InvalidDataException or
            JsonException or
            ArgumentException or
            NotSupportedException;

    private RecordingFinalizationDescriptor CreateSnapshot(ChannelViewModel channel, ActiveRecording recording)
    {
        FneTrafficProtocol protocol = FneTrafficProtocolMapper.FromChannelProtocol(
            channel.Definition.Protocol);
        return new RecordingFinalizationDescriptor(
            recording.JobId,
            DateTimeOffset.UtcNow,
            rootPath,
            recording.Writer.Path,
            recording.OutputPath,
            recording.Writer.Format.SampleRate,
            recording.Writer.Format.Channels,
            recording.Writer.Format.BitsPerSample,
            protocol,
            channel.Definition.Mode.ToUpperInvariant(),
            recording.Direction,
            recording.RecordingSourceType,
            recording.UtcStartTime,
            DateTimeOffset.UtcNow,
            channel.Definition.SystemName,
            channel.Definition.Name,
            channel.Definition.DestinationId,
            recording.SourceId,
            recording.SubscriberAlias,
            recording.StreamId,
            recording.StreamIds
                .OrderBy(streamId => streamId == recording.StreamId ? 0 : 1)
                .ThenBy(streamId => streamId)
                .ToArray(),
            recording.Encryption.IsSecure,
            recording.Encryption.AlgorithmId,
            recording.Encryption.KeyId,
            retentionDays > 0 ? retentionDays : null,
            recording.Encryption.IsKnown,
            recording.ReceiveEpisodeId);
    }

    private async Task<RecordingFinalizationResult> FinalizeRecordingAsync(
        RecordingFinalizationDescriptor snapshot,
        ChannelViewModel? channel,
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
                    { Channel = channel };
                }
                TryDelete(snapshot.OutputPath);
            }
            if (!File.Exists(snapshot.WavePath))
                throw new InvalidDataException("The finalization source WAV is missing.");
            PcmWavFileWriter.RepairInterruptedFile(snapshot.WavePath, snapshot.Format);
            PcmWavTrimAnalysis trimAnalysis = PcmWavSilenceTrimmer.AnalyzeFile(
                snapshot.WavePath,
                snapshot.Format);
            PcmWavTrimResult trim = trimAnalysis.Result;
            if (trim.OutputSamples <= 0 || trim.ActiveSampleCount <= 0 || trim.PeakAmplitude <= 0)
            {
                return new RecordingFinalizationResult(
                    null,
                    snapshot.StreamId,
                    "Recording contained no playable voice activity.",
                    null)
                { Channel = channel };
            }

            TimeSpan sampleDuration = TimeSpan.FromSeconds(
                trim.OriginalSamples / (double)(snapshot.Format.SampleRate * snapshot.Format.Channels));
            DateTimeOffset sampleDerivedEnd = snapshot.UtcStartTime.Add(sampleDuration);
            if (snapshot.UtcEndTime < sampleDerivedEnd)
                snapshot = snapshot with { UtcEndTime = sampleDerivedEnd };

            finalPath = snapshot.OutputPath;
            CallRecordingMetadata metadata = metadataFactory.Create(snapshot, trim, finalPath);
            temporaryOpusPath = $"{finalPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            await OpusRecordingEncoder.EncodeWaveFileRangeAsync(
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
                { Channel = channel };
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
            { Channel = channel };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or JsonException or FormatException or NotSupportedException)
        {
            return new RecordingFinalizationResult(null, snapshot.StreamId, exception.Message, exception)
            { Channel = channel };
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

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class ActiveRecording(
        Guid jobId,
        uint streamId,
        DateTimeOffset utcStartTime,
        uint? sourceId,
        string subscriberAlias,
        string direction,
        string recordingSourceType,
        long? receiveEpisodeId,
        PcmWavFileWriter writer)
    {
        private readonly object writerSync = new();
        private readonly Queue<uint> replacementStreamOrder = [];
        private bool writerClosed;
        public Guid JobId { get; } = jobId;
        public uint StreamId { get; } = streamId;
        public HashSet<uint> StreamIds { get; } = [streamId];
        public DateTimeOffset UtcStartTime { get; } = utcStartTime;
        public uint? SourceId { get; } = sourceId;
        public string SubscriberAlias { get; } = subscriberAlias;
        public string Direction { get; } = direction;
        public string RecordingSourceType { get; } = recordingSourceType;
        public long? ReceiveEpisodeId { get; } = receiveEpisodeId;
        public PcmWavFileWriter Writer { get; } = writer;
        public string OutputPath { get; set; } = string.Empty;
        public EncryptionSnapshot Encryption { get; private set; }

        public bool SetEncryption(EncryptionSnapshot encryption)
        {
            bool changed = !Encryption.HasSameMetadata(encryption) ||
                Encryption.IsKnown != encryption.IsKnown;
            Encryption = encryption;
            return changed;
        }

        public bool ObservePhysicalStream(uint streamId)
        {
            if (streamId == 0 || StreamIds.Contains(streamId))
                return false;

            if (StreamIds.Count >= ReceiveCallEpisodeTracker.MaximumStreamsPerEpisode)
            {
                uint oldestReplacement = replacementStreamOrder.Dequeue();
                StreamIds.Remove(oldestReplacement);
            }

            StreamIds.Add(streamId);
            replacementStreamOrder.Enqueue(streamId);
            return true;
        }

        public void Write(ReadOnlySpan<short> samples)
        {
            lock (writerSync)
            {
                if (writerClosed)
                    throw new InvalidOperationException("The recording writer is already closed.");
                Writer.Write(samples);
            }
        }

        public void CloseWriter()
        {
            lock (writerSync)
            {
                if (writerClosed)
                    return;
                Writer.Dispose();
                writerClosed = true;
            }
        }
    }

    private PendingFinalization? DetachTransmitRecording(
        ChannelViewModel channel,
        ActiveRecording? expected = null)
    {
        if (!activeTransmit.TryGetValue(channel, out ActiveRecording? recording) ||
            (expected is not null && !ReferenceEquals(recording, expected)))
        {
            return null;
        }
        activeTransmit.Remove(channel);
        return new PendingFinalization(channel, recording);
    }

    private sealed record PendingFinalization(
        ChannelViewModel Channel,
        ActiveRecording Recording);
}
