using DvmConsole.Audio;
using DvmConsole.FneClient;
using DvmConsole.Media;
using System.Globalization;
using System.Text.Json;

namespace DvmConsole.Desktop;

// Owns per-channel receive and console-transmit recordings fed by PCM. A
// recording starts on the first frame for an active stream and is finalized
// on its terminator, call stop, channel stop, or application shutdown.
public sealed class CallRecordingManager : IDisposable
{
    public const int DefaultRetentionDays = 7;

    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object sync = new();
    private string rootPath;
    private readonly Action<ChannelViewModel, Exception>? faultHandler;
    private readonly Func<ChannelViewModel, uint, bool> shouldRecordSource;
    private int retentionDays;
    private readonly Dictionary<ChannelViewModel, ActiveRecording> active = [];
    private readonly Dictionary<ChannelViewModel, ActiveRecording> activeTransmit = [];
    private readonly Dictionary<(ChannelViewModel Channel, uint StreamId), TrafficEncryptionMetadata> streamEncryption = [];
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
    }

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
    {
        if (!Directory.Exists(rootPath))
            return [];

        List<CallRecordingMetadata> recordings = [];
        string[] sidecarPaths;
        try
        {
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

        foreach (string sidecarPath in sidecarPaths)
        {
            try
            {
                CallRecordingMetadata? metadata = JsonSerializer.Deserialize<CallRecordingMetadata>(
                    File.ReadAllText(sidecarPath),
                    MetadataJsonOptions);
                if (metadata is null || !TryNormalizeRecordingPath(sidecarPath, metadata))
                    continue;

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
            .ToArray();
    }

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
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (samples.IsEmpty || !channel.IsRecordingEnabled)
            return;

        uint streamId = channel.StreamId ?? 0;
        if (streamId == 0)
            return;

        uint sourceId = channel.SourceId ?? 0;
        if (sourceId != 0 && !shouldRecordSource(channel, sourceId))
        {
            lock (sync)
                CloseCore(channel);
            return;
        }

        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            try
            {
                if (!active.TryGetValue(channel, out ActiveRecording? recording) || recording.StreamId != streamId)
                {
                    CloseCore(channel);
                    recording = CreateActiveRecording(
                        channel,
                        streamId,
                        sourceId == 0 ? null : sourceId,
                        "RX",
                        "InboundRadio");
                    active[channel] = recording;
                }

                recording.Writer.Write(samples.Span);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                CloseCore(channel);
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
                if (active.TryGetValue(channel, out ActiveRecording? current) && current.StreamId == traffic.StreamId)
                    current.SetEncryption(resolved);
            }

            if (!IsTerminatingTraffic(traffic))
                return false;

            bool closed = false;
            if (active.TryGetValue(channel, out ActiveRecording? recording) && recording.StreamId == traffic.StreamId)
            {
                CloseCore(channel);
                closed = true;
            }
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

    public void StopTransmit(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        lock (sync)
            CloseTransmitCore(channel);
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;

            foreach (ChannelViewModel channel in active.Keys.Concat(activeTransmit.Keys).Distinct().ToArray())
            {
                CloseCore(channel);
                CloseTransmitCore(channel);
            }
            disposed = true;
        }
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

    private string CreateRecordingPath(ChannelViewModel channel, ActiveRecording recording)
    {
        DateTimeOffset localStart = recording.UtcStartTime.ToLocalTime();
        string dateFolder = localStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        FneTrafficProtocol protocol = channel.Definition.Mode switch
        {
            "p25" => FneTrafficProtocol.P25,
            "nxdn" => FneTrafficProtocol.Nxdn,
            "analog" => FneTrafficProtocol.Analog,
            _ => FneTrafficProtocol.Dmr
        };
        string security = recording.IsSecure
            ? EncryptionPresentation.AlgorithmAbbreviation(protocol, recording.EncryptionAlgorithmId) is string algorithm &&
              !string.IsNullOrEmpty(algorithm)
                ? $"SECURE_{algorithm}"
                : "SECURE"
            : "CLEAR";
        string filename = string.Join(
            "_",
            localStart.ToString("HHmmssfff", CultureInfo.InvariantCulture),
            SanitizeSegment(channel.Definition.SystemName),
            channel.Definition.DestinationId.ToString(CultureInfo.InvariantCulture),
            (recording.SourceId ?? 0).ToString(CultureInfo.InvariantCulture),
            security,
            recording.StreamId.ToString(CultureInfo.InvariantCulture));
        string directory = System.IO.Path.Combine(rootPath, dateFolder, SanitizeSegment(channel.Definition.SystemName));
        Directory.CreateDirectory(directory);

        string path = System.IO.Path.Combine(directory, $"{filename}.opus");
        int suffix = 1;
        while (File.Exists(path))
            path = System.IO.Path.Combine(directory, $"{filename}-{suffix++}.opus");
        return path;
    }

    private void CloseCore(ChannelViewModel channel)
    {
        if (!active.Remove(channel, out ActiveRecording? recording))
            return;
        streamEncryption.Remove((channel, recording.StreamId));

        try
        {
            recording.Writer.Dispose();
            PcmWavTrimResult trim = PcmWavSilenceTrimmer.TrimFile(
                recording.Writer.Path,
                recording.Writer.Format);
            FinalizeOpusRecording(channel, recording, trim);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            faultHandler?.Invoke(channel, exception);
        }
    }

    private void FinalizeOpusRecording(
        ChannelViewModel channel,
        ActiveRecording recording,
        PcmWavTrimResult trim)
    {
        string finalPath = CreateRecordingPath(channel, recording);
        string temporaryOpusPath = $"{finalPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            OpusRecordingEncoder.EncodeWaveFileAsync(
                    recording.Writer.Path,
                    temporaryOpusPath)
                .GetAwaiter()
                .GetResult();
            File.Move(temporaryOpusPath, finalPath);
            WriteMetadata(channel, recording, trim, finalPath);
        }
        finally
        {
            if (File.Exists(temporaryOpusPath))
                File.Delete(temporaryOpusPath);
            if (File.Exists(recording.Writer.Path))
                File.Delete(recording.Writer.Path);
        }
    }

    private void WriteMetadata(
        ChannelViewModel channel,
        ActiveRecording recording,
        PcmWavTrimResult trim,
        string recordingPath)
    {
        DateTimeOffset end = DateTimeOffset.UtcNow;
        FileInfo fileInfo = new(recordingPath);
        FneTrafficProtocol protocol = channel.Definition.Mode switch
        {
            "p25" => FneTrafficProtocol.P25,
            "nxdn" => FneTrafficProtocol.Nxdn,
            "analog" => FneTrafficProtocol.Analog,
            _ => FneTrafficProtocol.Dmr
        };
        CallRecordingMetadata metadata = new()
        {
            Protocol = channel.Definition.Mode.ToUpperInvariant(),
            Direction = recording.Direction,
            RecordingSourceType = recording.RecordingSourceType,
            UtcStartTime = recording.UtcStartTime,
            UtcEndTime = end,
            DurationMs = (long)Math.Round(
                trim.OutputSamples * 1000d / recording.Writer.Format.SampleRate,
                MidpointRounding.AwayFromZero),
            FilePath = recordingPath,
            FileName = Path.GetFileName(recordingPath),
            FileSizeBytes = fileInfo.Length,
            SampleRate = recording.Writer.Format.SampleRate,
            BitsPerSample = recording.Writer.Format.BitsPerSample,
            ChannelCount = recording.Writer.Format.Channels,
            OriginalSampleCount = trim.OriginalSamples,
            ActiveSampleCount = trim.ActiveSampleCount,
            PeakAmplitude = trim.PeakAmplitude,
            TrimLeadMs = trim.TrimLeadMs,
            TrimTailMs = trim.TrimTailMs,
            SystemName = channel.Definition.SystemName,
            ChannelName = channel.Definition.Name,
            TalkgroupId = channel.Definition.DestinationId,
            SubscriberId = recording.SourceId,
            SubscriberAlias = recording.Direction.Equals("RX", StringComparison.OrdinalIgnoreCase) &&
                              recording.SourceId is uint sourceId &&
                              !string.Equals(
                                  channel.LastCallerText,
                                  sourceId.ToString(CultureInfo.InvariantCulture),
                                  StringComparison.Ordinal)
                ? channel.LastCallerText
                : string.Empty,
            StreamId = recording.StreamId,
            IsEncrypted = recording.IsSecure,
            EncryptionAlgorithm = EncryptionPresentation.AlgorithmAbbreviation(
                protocol,
                recording.EncryptionAlgorithmId),
            EncryptionKeyId = recording.IsSecure && recording.EncryptionKeyId is ushort keyId
                ? $"0x{keyId:X}"
                : null,
            RetentionDaysAtRecordTime = retentionDays > 0 ? retentionDays : null
        };

        string sidecarPath = Path.ChangeExtension(recordingPath, ".json");
        string temporaryPath = $"{sidecarPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(metadata, MetadataJsonOptions));
            File.Move(temporaryPath, sidecarPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
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
        string direction,
        string recordingSourceType,
        PcmWavFileWriter writer)
    {
        public uint StreamId { get; } = streamId;
        public DateTimeOffset UtcStartTime { get; } = utcStartTime;
        public uint? SourceId { get; } = sourceId;
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

    private void CloseTransmitCore(ChannelViewModel channel)
    {
        if (!activeTransmit.Remove(channel, out ActiveRecording? recording))
            return;

        try
        {
            recording.Writer.Dispose();
            PcmWavTrimResult trim = PcmWavSilenceTrimmer.TrimFile(
                recording.Writer.Path,
                recording.Writer.Format);
            FinalizeOpusRecording(channel, recording, trim);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            faultHandler?.Invoke(channel, exception);
        }
    }
}
