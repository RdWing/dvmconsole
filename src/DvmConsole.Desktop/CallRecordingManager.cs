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
                    recording = new ActiveRecording(
                        streamId,
                        DateTimeOffset.UtcNow,
                        sourceId == 0 ? null : sourceId,
                        "RX",
                        "InboundRadio",
                        new PcmWavFileWriter(CreateRecordingPath(channel, streamId, "RX"), PcmAudioFormat.Voice8KhzMono16Bit));
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
                    recording = new ActiveRecording(
                        streamId,
                        DateTimeOffset.UtcNow,
                        sourceId == 0 ? null : sourceId,
                        "TX",
                        "ConsoleTx",
                        new PcmWavFileWriter(CreateRecordingPath(channel, streamId, "TX"), PcmAudioFormat.Voice8KhzMono16Bit));
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

    public void ObserveTraffic(ChannelViewModel channel, FneTrafficFrame traffic)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(traffic);
        if (!traffic.FrameType.Equals("TERMINATOR", StringComparison.OrdinalIgnoreCase))
            return;

        lock (sync)
        {
            if (active.TryGetValue(channel, out ActiveRecording? recording) && recording.StreamId == traffic.StreamId)
                CloseCore(channel);
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

    private string CreateRecordingPath(ChannelViewModel channel, uint streamId, string direction)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        string dateFolder = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string directionSuffix = direction.Equals("RX", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $"_{SanitizeSegment(direction)}";
        string filename = string.Join(
            "_",
            now.ToString("HHmmssfff", CultureInfo.InvariantCulture),
            SanitizeSegment(channel.Definition.SystemName),
            SanitizeSegment(channel.Name),
            streamId.ToString(CultureInfo.InvariantCulture)) + directionSuffix;
        string directory = System.IO.Path.Combine(rootPath, dateFolder, SanitizeSegment(channel.Definition.SystemName));
        Directory.CreateDirectory(directory);

        string path = System.IO.Path.Combine(directory, $"{filename}.wav");
        int suffix = 1;
        while (File.Exists(path))
            path = System.IO.Path.Combine(directory, $"{filename}-{suffix++}.wav");
        return path;
    }

    private void CloseCore(ChannelViewModel channel)
    {
        if (!active.Remove(channel, out ActiveRecording? recording))
            return;

        try
        {
            recording.Writer.Dispose();
            PcmWavTrimResult trim = PcmWavSilenceTrimmer.TrimFile(
                recording.Writer.Path,
                recording.Writer.Format);
            WriteMetadata(channel, recording, trim);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            faultHandler?.Invoke(channel, exception);
        }
    }

    private void WriteMetadata(ChannelViewModel channel, ActiveRecording recording, PcmWavTrimResult trim)
    {
        DateTimeOffset end = DateTimeOffset.UtcNow;
        FileInfo fileInfo = new(recording.Writer.Path);
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
            FilePath = recording.Writer.Path,
            FileName = Path.GetFileName(recording.Writer.Path),
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
            IsEncrypted = channel.Definition.IsEncrypted,
            EncryptionAlgorithm = channel.Definition.EncryptionAlgorithm,
            EncryptionKeyId = channel.Definition.EncryptionKeyId,
            RetentionDaysAtRecordTime = retentionDays > 0 ? retentionDays : null
        };

        string sidecarPath = Path.ChangeExtension(recording.Writer.Path, ".json");
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
            metadata.FilePath = Path.ChangeExtension(sidecarPath, ".wav");
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

    private sealed record ActiveRecording(
        uint StreamId,
        DateTimeOffset UtcStartTime,
        uint? SourceId,
        string Direction,
        string RecordingSourceType,
        PcmWavFileWriter Writer);

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
            WriteMetadata(channel, recording, trim);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            faultHandler?.Invoke(channel, exception);
        }
    }
}
