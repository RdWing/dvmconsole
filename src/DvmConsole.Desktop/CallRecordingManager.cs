using DvmConsole.Audio;
using DvmConsole.FneClient;
using DvmConsole.Media;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DvmConsole.Desktop;

// Owns per-channel receive and console-transmit recordings fed by PCM. A
// recording starts on the first frame for an active stream and is finalized
// on its terminator, call stop, channel stop, or application shutdown.
public sealed class CallRecordingManager : IDisposable, IAsyncDisposable
{
    public const int DefaultRetentionDays = 7;

    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        WriteIndented = true
    };
    private readonly object sync = new();
    private readonly OpusRecordingMetadataStore opusMetadataStore = new();
    private string rootPath;
    private readonly Action<ChannelViewModel, Exception>? faultHandler;
    private readonly Func<ChannelViewModel, uint, bool> shouldRecordSource;
    private int retentionDays;
    private readonly Dictionary<(ChannelViewModel Channel, uint StreamId), ActiveRecording> active = [];
    private readonly Dictionary<ChannelViewModel, ActiveRecording> activeTransmit = [];
    private readonly Dictionary<(ChannelViewModel Channel, uint StreamId), TrafficEncryptionMetadata> streamEncryption = [];
    private readonly RecordingFinalizationQueue finalizationQueue = new();
    private bool disposed;

    public CallRecordingManager(
        string rootPath,
        Action<ChannelViewModel, Exception>? faultHandler = null,
        int retentionDays = DefaultRetentionDays,
        Func<ChannelViewModel, uint, bool>? shouldRecordSource = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (retentionDays < 0)
            throw new ArgumentOutOfRangeException(nameof(retentionDays));

        this.rootPath = System.IO.Path.GetFullPath(rootPath);
        this.faultHandler = faultHandler;
        this.retentionDays = retentionDays;
        this.shouldRecordSource = shouldRecordSource ?? ((_, _) => true);
        finalizationQueue.Finalized += HandleRecordingFinalized;
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

            rootPath = normalizedPath;
        }

        return true;
    }

    public IReadOnlyList<CallRecordingMetadata> LoadRecordings()
        => LoadRecordings(CancellationToken.None);

    private IReadOnlyList<CallRecordingMetadata> LoadRecordings(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(rootPath))
            return [];

        List<CallRecordingMetadata> recordings = [];
        string[] opusPaths;
        string[] sidecarPaths;
        try
        {
            opusPaths = Directory.EnumerateFiles(rootPath, "*.opus", SearchOption.AllDirectories).ToArray();
            sidecarPaths = Directory.EnumerateFiles(rootPath, "*.json", SearchOption.AllDirectories).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        foreach (string opusPath in opusPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (opusMetadataStore.TryRead(opusPath, rootPath, out CallRecordingMetadata metadata))
                    recordings.Add(metadata);
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException or IOException or UnauthorizedAccessException)
            {
                // A damaged or unrelated Opus file must not hide the rest of
                // the recording catalog. A matching legacy sidecar may still
                // recover it in the pass below.
            }
        }

        foreach (string sidecarPath in sidecarPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                CallRecordingMetadata? metadata = JsonSerializer.Deserialize<CallRecordingMetadata>(
                    File.ReadAllText(sidecarPath),
                    MetadataJsonOptions);
                if (metadata is null || !TryNormalizeRecordingPath(sidecarPath, metadata))
                    continue;
                if (metadata.SchemaVersion < 2)
                    UpgradeLegacyMetadata(sidecarPath, metadata, cancellationToken);
                opusMetadataStore.TryMigrateSidecar(sidecarPath, metadata, rootPath);

                recordings.Add(metadata);
            }
            catch (JsonException)
            {
                // A partially written or unrelated JSON file must not hide the
                // rest of the recording catalog.
            }
            catch (IOException)
            {
                // Files can disappear while an operator is browsing recordings.
            }
            catch (UnauthorizedAccessException)
            {
                // The catalog remains usable when one folder is inaccessible.
            }
        }

        return recordings
            .OrderByDescending(recording => recording.UtcStartTime)
            .ThenBy(recording => recording.FileName, StringComparer.OrdinalIgnoreCase)
            .GroupBy(RecordingCatalogKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    public Task<IReadOnlyList<CallRecordingMetadata>> LoadRecordingsAsync(
        CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<CallRecordingMetadata>>(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return LoadRecordings(cancellationToken);
            },
            cancellationToken);

    private void UpgradeLegacyMetadata(
        string sidecarPath,
        CallRecordingMetadata metadata,
        CancellationToken cancellationToken)
    {
        string relativePath = Path.GetRelativePath(rootPath, metadata.FilePath)
            .Replace('\\', '/')
            .ToUpperInvariant();
        metadata.RecordingId = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(relativePath)))[..32];
        metadata.FileName = string.IsNullOrWhiteSpace(metadata.FileName)
            ? Path.GetFileName(metadata.FilePath)
            : metadata.FileName;

        try
        {
            LegacyAudioAnalysis analysis = AnalyzeLegacyAudioAsync(metadata.FilePath, cancellationToken).GetAwaiter().GetResult();
            metadata.FileSizeBytes = new FileInfo(metadata.FilePath).Length;
            metadata.SampleRate = analysis.SampleRate;
            metadata.BitsPerSample = 16;
            metadata.ChannelCount = 1;
            metadata.OriginalSampleCount = analysis.SampleCount;
            metadata.ActiveSampleCount = analysis.ActiveSampleCount;
            metadata.PeakAmplitude = analysis.PeakAmplitude;
            metadata.DurationMs = analysis.SampleRate > 0
                ? (long)Math.Round(analysis.SampleCount * 1000d / analysis.SampleRate, MidpointRounding.AwayFromZero)
                : metadata.DurationMs;
            metadata.PlaybackValidated = analysis.ActiveSampleCount > 0 && analysis.PeakAmplitude > 0;
            metadata.SchemaVersion = 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            // Keep the legacy schema retryable. A removable drive, partially
            // copied file, or transient decoder failure must not permanently
            // mark an otherwise valid recording as migrated and unplayable.
            metadata.PlaybackValidated = false;
            return;
        }

        string temporaryPath = $"{sidecarPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(metadata, MetadataJsonOptions));
            File.Move(temporaryPath, sidecarPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The in-memory migration remains stable even on read-only media.
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static async Task<LegacyAudioAnalysis> AnalyzeLegacyAudioAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream source = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using IAudioPcmStreamReader reader = await PcmStreamDecoder.OpenAsync(source).ConfigureAwait(false);
        short[] buffer = new short[4096];
        long sampleCount = 0;
        long activeSampleCount = 0;
        int peakAmplitude = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = await reader.ReadSamplesAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
                break;
            sampleCount += count;
            foreach (short sample in buffer.AsSpan(0, count))
            {
                int amplitude = sample == short.MinValue ? short.MaxValue : Math.Abs(sample);
                if (amplitude == 0)
                    continue;
                activeSampleCount++;
                peakAmplitude = Math.Max(peakAmplitude, amplitude);
            }
        }
        return new LegacyAudioAnalysis(reader.SampleRate, sampleCount, activeSampleCount, peakAmplitude);
    }

    private static string RecordingCatalogKey(CallRecordingMetadata metadata)
        => !string.IsNullOrWhiteSpace(metadata.RecordingId)
            ? metadata.RecordingId
            : metadata.FilePath;

    private readonly record struct LegacyAudioAnalysis(
        int SampleRate,
        long SampleCount,
        long ActiveSampleCount,
        int PeakAmplitude);

    public int PruneExpired(DateTimeOffset? now = null)
    {
        if (retentionDays <= 0)
            return 0;

        DateTimeOffset cutoff = (now ?? DateTimeOffset.UtcNow).ToUniversalTime().AddDays(-retentionDays);
        int deleted = 0;
        foreach (CallRecordingMetadata metadata in LoadRecordings())
        {
            if (metadata.UtcEndTime > cutoff || !DeleteRecording(metadata))
                continue;
            deleted++;
        }

        return deleted;
    }

    public bool DeleteRecording(CallRecordingMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (!TryGetRecordingPath(metadata, out string recordingPath))
            return false;

        string sidecarPath = Path.ChangeExtension(recordingPath, ".json");

        bool deleted = false;
        try
        {
            if (File.Exists(recordingPath))
            {
                File.Delete(recordingPath);
                deleted = true;
            }

            if (File.Exists(sidecarPath))
            {
                File.Delete(sidecarPath);
                deleted = true;
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        return deleted;
    }

    public bool TryGetRecordingPath(CallRecordingMetadata metadata, out string recordingPath)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        recordingPath = string.Empty;
        if (string.IsNullOrWhiteSpace(metadata.FilePath) ||
            !TryNormalizeRecordingPath(metadata.SidecarPath, metadata) ||
            !IsUnderRoot(metadata.FilePath))
        {
            return false;
        }

        recordingPath = metadata.FilePath;
        return true;
    }

    public void WriteSamples(ChannelViewModel channel, ReadOnlyMemory<short> samples)
        => WriteSamples(channel, channel.StreamId ?? 0, channel.SourceId ?? 0, samples);

    public void WriteSamples(
        ChannelViewModel channel,
        uint streamId,
        uint sourceId,
        ReadOnlyMemory<short> samples)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (samples.IsEmpty || !channel.IsRecordingEnabled || streamId == 0)
            return;

        if (sourceId != 0 && !shouldRecordSource(channel, sourceId))
        {
            lock (sync)
                CloseCore(channel, streamId);
            return;
        }

        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            try
            {
                var key = (channel, streamId);
                if (!active.TryGetValue(key, out ActiveRecording? recording))
                {
                    recording = CreateActiveRecording(
                        channel,
                        streamId,
                        sourceId == 0 ? null : sourceId,
                        "RX",
                        "InboundRadio");
                    active[key] = recording;
                }

                recording.Writer.Write(samples.Span);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                CloseCore(channel, streamId);
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

        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            try
            {
                if (!activeTransmit.TryGetValue(channel, out ActiveRecording? recording) ||
                    recording.StreamId != streamId)
                {
                    CloseTransmitCore(channel);
                    recording = CreateActiveRecording(
                        channel,
                        streamId,
                        sourceId == 0 ? null : sourceId,
                        "TX",
                        "ConsoleTx");
                    activeTransmit[channel] = recording;
                }

                recording.Writer.Write(samples.Span);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                CloseTransmitCore(channel);
                faultHandler?.Invoke(channel, exception);
            }
        }
    }

    public bool ObserveTraffic(ChannelViewModel channel, FneTrafficFrame traffic)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(traffic);
        lock (sync)
        {
            TrafficEncryptionMetadata? encryption = TrafficEncryptionMetadataResolver.TryResolve(traffic);
            if (encryption is TrafficEncryptionMetadata resolved)
            {
                streamEncryption[(channel, traffic.StreamId)] = resolved;
                if (active.TryGetValue((channel, traffic.StreamId), out ActiveRecording? current))
                    current.SetEncryption(resolved);
            }

            if (!IsTerminatingTraffic(traffic))
                return false;

            bool closed = active.ContainsKey((channel, traffic.StreamId));
            CloseCore(channel, traffic.StreamId);
            streamEncryption.Remove((channel, traffic.StreamId));
            return closed;
        }
    }

    public void StopChannel(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        lock (sync)
        {
            CloseCore(channel);
            CloseTransmitCore(channel);
        }
    }

    public void StopStream(ChannelViewModel channel, uint streamId)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (streamId == 0)
            return;
        lock (sync)
            CloseCore(channel, streamId);
    }

    public void StopTransmit(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        lock (sync)
            CloseTransmitCore(channel);
    }

    public void Dispose()
        => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposed)
                return;

            foreach (ChannelViewModel channel in active.Keys
                         .Select(key => key.Channel)
                         .Concat(activeTransmit.Keys)
                         .Distinct()
                         .ToArray())
            {
                CloseCore(channel);
                CloseTransmitCore(channel);
            }
            disposed = true;
        }

        await finalizationQueue.DisposeAsync().ConfigureAwait(false);
    }

    private void HandleRecordingFinalized(object? sender, RecordingFinalizationResult result)
    {
        if (result.Error is Exception exception && result.Channel is ChannelViewModel channel)
            faultHandler?.Invoke(channel, exception);
        RecordingFinalized?.Invoke(this, result);
    }

    private ActiveRecording CreateActiveRecording(
        ChannelViewModel channel,
        uint streamId,
        uint? sourceId,
        string direction,
        string recordingSourceType)
    {
        var recording = new ActiveRecording(
            streamId,
            DateTimeOffset.UtcNow,
            sourceId,
            direction.Equals("RX", StringComparison.OrdinalIgnoreCase) && sourceId is uint callerId
                ? channel.ResolveSubscriberAlias(callerId)
                : string.Empty,
            direction,
            recordingSourceType,
            new PcmWavFileWriter(CreateTemporaryWavePath(), PcmAudioFormat.Voice8KhzMono16Bit));

        if (direction.Equals("TX", StringComparison.OrdinalIgnoreCase))
        {
            bool secure = channel.Definition.IsEncrypted && channel.IsTransmitEncrypted;
            if (secure && EncryptionPresentation.TryParseConfiguredAlgorithm(
                    channel.Definition,
                    out byte algorithmId,
                    out ushort keyId))
            {
                recording.SetEncryption(new TrafficEncryptionMetadata(true, algorithmId, keyId));
            }
            else
            {
                recording.SetEncryption(new TrafficEncryptionMetadata(secure, 0, 0));
            }
        }
        else if (streamEncryption.TryGetValue((channel, streamId), out TrafficEncryptionMetadata encryption))
        {
            recording.SetEncryption(encryption);
        }
        else if (channel.Definition.IsEncrypted && EncryptionPresentation.TryParseConfiguredAlgorithm(
                     channel.Definition,
                     out byte algorithmId,
                     out ushort keyId))
        {
            recording.SetEncryption(new TrafficEncryptionMetadata(true, algorithmId, keyId));
        }

        return recording;
    }

    private string CreateTemporaryWavePath()
    {
        string directory = Path.Combine(rootPath, ".active");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.NewGuid():N}.wav");
    }

    private static string CreateRecordingPath(RecordingSnapshot snapshot)
    {
        DateTimeOffset localStart = snapshot.UtcStartTime.ToLocalTime();
        string dateFolder = localStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string security = snapshot.IsSecure
            ? EncryptionPresentation.AlgorithmAbbreviation(snapshot.Protocol, snapshot.EncryptionAlgorithmId) is string algorithm &&
              !string.IsNullOrEmpty(algorithm)
                ? $"SECURE_{algorithm}"
                : "SECURE"
            : "CLEAR";
        string filename = string.Join(
            "_",
            localStart.ToString("HHmmssfff", CultureInfo.InvariantCulture),
            SanitizeSegment(snapshot.SystemName),
            snapshot.TalkgroupId.ToString(CultureInfo.InvariantCulture),
            (snapshot.SourceId ?? 0).ToString(CultureInfo.InvariantCulture),
            security,
            snapshot.StreamId.ToString(CultureInfo.InvariantCulture));
        string directory = System.IO.Path.Combine(snapshot.RootPath, dateFolder, SanitizeSegment(snapshot.SystemName));
        Directory.CreateDirectory(directory);

        string path = System.IO.Path.Combine(directory, $"{filename}.opus");
        int suffix = 1;
        while (File.Exists(path))
            path = System.IO.Path.Combine(directory, $"{filename}-{suffix++}.opus");
        return path;
    }

    private void CloseCore(ChannelViewModel channel)
    {
        foreach (uint streamId in active.Keys
                     .Where(key => ReferenceEquals(key.Channel, channel))
                     .Select(key => key.StreamId)
                     .ToArray())
        {
            CloseCore(channel, streamId);
        }
    }

    private void CloseCore(ChannelViewModel channel, uint streamId)
    {
        if (!active.Remove((channel, streamId), out ActiveRecording? recording))
            return;
        streamEncryption.Remove((channel, streamId));
        EnqueueFinalization(channel, recording);
    }

    private void EnqueueFinalization(ChannelViewModel channel, ActiveRecording recording)
    {
        try
        {
            recording.Writer.Dispose();
            RecordingSnapshot snapshot = CreateSnapshot(channel, recording);
            finalizationQueue.EnqueueAsync(new RecordingFinalizationJob(
                recording.StreamId,
                cancellationToken => FinalizeRecordingAsync(snapshot, channel, cancellationToken)))
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            TryDelete(recording.Writer.Path);
            faultHandler?.Invoke(channel, exception);
        }
    }

    private RecordingSnapshot CreateSnapshot(ChannelViewModel channel, ActiveRecording recording)
    {
        FneTrafficProtocol protocol = channel.Definition.Mode switch
        {
            "p25" => FneTrafficProtocol.P25,
            "nxdn" => FneTrafficProtocol.Nxdn,
            "analog" => FneTrafficProtocol.Analog,
            _ => FneTrafficProtocol.Dmr
        };
        return new RecordingSnapshot(
            rootPath,
            recording.Writer.Path,
            recording.Writer.Format,
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
            recording.IsSecure,
            recording.EncryptionAlgorithmId,
            recording.EncryptionKeyId,
            retentionDays > 0 ? retentionDays : null);
    }

    private async Task<RecordingFinalizationResult> FinalizeRecordingAsync(
        RecordingSnapshot snapshot,
        ChannelViewModel channel,
        CancellationToken cancellationToken)
    {
        string? finalPath = null;
        string? temporaryOpusPath = null;
        bool completed = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            PcmWavTrimResult trim = PcmWavSilenceTrimmer.TrimFile(snapshot.WavePath, snapshot.Format);
            if (trim.OutputSamples <= 0 || trim.ActiveSampleCount <= 0 || trim.PeakAmplitude <= 0)
            {
                return new RecordingFinalizationResult(
                    null,
                    snapshot.StreamId,
                    "Recording contained no playable voice activity.",
                    null)
                { Channel = channel };
            }

            finalPath = CreateRecordingPath(snapshot);
            CallRecordingMetadata metadata = CreateMetadata(snapshot, trim, finalPath);
            temporaryOpusPath = $"{finalPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            await OpusRecordingEncoder.EncodeWaveFileAsync(
                snapshot.WavePath,
                temporaryOpusPath,
                opusMetadataStore.CreateTags(metadata),
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
            if (!opusMetadataStore.TryRead(finalPath, snapshot.RootPath, out CallRecordingMetadata persistedMetadata) ||
                !RecordingCatalogKey(persistedMetadata).Equals(
                    RecordingCatalogKey(metadata),
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
            TryDelete(snapshot.WavePath);
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

    private static CallRecordingMetadata CreateMetadata(
        RecordingSnapshot snapshot,
        PcmWavTrimResult trim,
        string recordingPath)
    {
        return new CallRecordingMetadata
        {
            SchemaVersion = 2,
            Protocol = snapshot.ProtocolText,
            Direction = snapshot.Direction,
            RecordingSourceType = snapshot.RecordingSourceType,
            UtcStartTime = snapshot.UtcStartTime,
            UtcEndTime = snapshot.UtcEndTime,
            DurationMs = (long)Math.Round(
                trim.OutputSamples * 1000d / snapshot.Format.SampleRate,
                MidpointRounding.AwayFromZero),
            FilePath = recordingPath,
            FileName = Path.GetFileName(recordingPath),
            FileSizeBytes = 0,
            SampleRate = snapshot.Format.SampleRate,
            BitsPerSample = snapshot.Format.BitsPerSample,
            ChannelCount = snapshot.Format.Channels,
            OriginalSampleCount = trim.OriginalSamples,
            ActiveSampleCount = trim.ActiveSampleCount,
            PeakAmplitude = trim.PeakAmplitude,
            TrimLeadMs = trim.TrimLeadMs,
            TrimTailMs = trim.TrimTailMs,
            SystemName = snapshot.SystemName,
            ChannelName = snapshot.ChannelName,
            TalkgroupId = snapshot.TalkgroupId,
            SubscriberId = snapshot.SourceId,
            SubscriberAlias = snapshot.SubscriberAlias,
            StreamId = snapshot.StreamId,
            IsEncrypted = snapshot.IsSecure,
            EncryptionAlgorithm = EncryptionPresentation.AlgorithmAbbreviation(
                snapshot.Protocol,
                snapshot.EncryptionAlgorithmId),
            EncryptionKeyId = snapshot.IsSecure && snapshot.EncryptionKeyId is ushort keyId
                ? $"0x{keyId:X}"
                : null,
            RetentionDaysAtRecordTime = snapshot.RetentionDays,
            PlaybackValidated = true
        };
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

    private bool TryNormalizeRecordingPath(string sidecarPath, CallRecordingMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata.FilePath))
        {
            string opusPath = Path.ChangeExtension(sidecarPath, ".opus");
            metadata.FilePath = File.Exists(opusPath)
                ? opusPath
                : Path.ChangeExtension(sidecarPath, ".wav");
        }
        else if (!Path.IsPathRooted(metadata.FilePath))
            metadata.FilePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sidecarPath) ?? rootPath, metadata.FilePath));
        else
            metadata.FilePath = Path.GetFullPath(metadata.FilePath);

        if (string.IsNullOrWhiteSpace(metadata.FileName))
            metadata.FileName = Path.GetFileName(metadata.FilePath);

        return IsUnderRoot(metadata.FilePath) && File.Exists(metadata.FilePath);
    }

    private bool IsUnderRoot(string path)
    {
        string normalizedPath = Path.GetFullPath(path);
        string normalizedRoot = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeSegment(string value)
    {
        char[] invalid = System.IO.Path.GetInvalidFileNameChars();
        string sanitized = new(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        sanitized = string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized.Trim();
        return sanitized.Length <= 64 ? sanitized : sanitized[..64];
    }

    private static bool IsTerminatingTraffic(FneTrafficFrame traffic)
        => traffic.FrameType.Equals("TERMINATOR", StringComparison.OrdinalIgnoreCase) ||
           (traffic.Protocol == FneTrafficProtocol.Dmr &&
            traffic.Subtype.Equals("TERMINATOR_WITH_LC", StringComparison.OrdinalIgnoreCase)) ||
           (traffic.Protocol == FneTrafficProtocol.P25 &&
            (traffic.Subtype.Equals("TDU", StringComparison.OrdinalIgnoreCase) ||
             traffic.Subtype.Equals("TDULC", StringComparison.OrdinalIgnoreCase)));

    private sealed class ActiveRecording(
        uint streamId,
        DateTimeOffset utcStartTime,
        uint? sourceId,
        string subscriberAlias,
        string direction,
        string recordingSourceType,
        PcmWavFileWriter writer)
    {
        public uint StreamId { get; } = streamId;
        public DateTimeOffset UtcStartTime { get; } = utcStartTime;
        public uint? SourceId { get; } = sourceId;
        public string SubscriberAlias { get; } = subscriberAlias;
        public string Direction { get; } = direction;
        public string RecordingSourceType { get; } = recordingSourceType;
        public PcmWavFileWriter Writer { get; } = writer;
        public bool IsSecure { get; private set; }
        public byte? EncryptionAlgorithmId { get; private set; }
        public ushort? EncryptionKeyId { get; private set; }

        public void SetEncryption(TrafficEncryptionMetadata encryption)
        {
            IsSecure = encryption.Secure;
            EncryptionAlgorithmId = encryption.Secure ? encryption.AlgorithmId : null;
            EncryptionKeyId = encryption.Secure ? encryption.KeyId : null;
        }
    }

    private sealed record RecordingSnapshot(
        string RootPath,
        string WavePath,
        PcmAudioFormat Format,
        FneTrafficProtocol Protocol,
        string ProtocolText,
        string Direction,
        string RecordingSourceType,
        DateTimeOffset UtcStartTime,
        DateTimeOffset UtcEndTime,
        string SystemName,
        string ChannelName,
        uint TalkgroupId,
        uint? SourceId,
        string SubscriberAlias,
        uint StreamId,
        bool IsSecure,
        byte? EncryptionAlgorithmId,
        ushort? EncryptionKeyId,
        int? RetentionDays);

    private void CloseTransmitCore(ChannelViewModel channel)
    {
        if (!activeTransmit.Remove(channel, out ActiveRecording? recording))
            return;
        EnqueueFinalization(channel, recording);
    }
}
