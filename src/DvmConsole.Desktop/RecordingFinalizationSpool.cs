using DvmConsole.Audio;
using DvmConsole.FneClient;
using System.Text.Json;

namespace DvmConsole.Desktop;

internal sealed record RecordingFinalizationDescriptor(
    Guid JobId,
    DateTimeOffset CreatedAt,
    string RootPath,
    string WavePath,
    string OutputPath,
    int SampleRate,
    int Channels,
    int BitsPerSample,
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
    IReadOnlyList<uint> StreamIds,
    bool IsSecure,
    byte? EncryptionAlgorithmId,
    ushort? EncryptionKeyId,
    int? RetentionDays)
{
    public PcmAudioFormat Format => new(SampleRate, Channels, BitsPerSample);
}

internal sealed record RecordingFinalizationSpoolHealth(
    int PendingJobs,
    int QuarantinedJobs,
    TimeSpan? OldestAge,
    string? LastError);

/// <summary>
/// Durable, internal-only TAR work queue metadata. It is deliberately distinct
/// from the public recording catalog and accepts paths only inside its root.
/// </summary>
internal sealed class RecordingFinalizationSpool
{
    private const string DescriptorSuffix = ".finalize.json";
    private readonly string rootPath;
    private readonly string activePath;
    private readonly string quarantinePath;
    private readonly object sync = new();
    private readonly Dictionary<Guid, RecordingFinalizationDescriptor> descriptors = [];
    private readonly HashSet<Guid> readyToFinalize = [];
    private string? lastError;
    private int quarantinedJobs;
    private int recoveredOrphanedWaveFiles;
    private bool discovered;

    public RecordingFinalizationSpool(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        this.rootPath = Path.GetFullPath(rootPath);
        activePath = Path.Combine(this.rootPath, ".active");
        quarantinePath = Path.Combine(activePath, "quarantine");
    }

    /// <summary>
    /// Persists recovery metadata for a recording that is still being captured.
    /// The current process must not finalize the recording until its writer is
    /// closed and the descriptor is explicitly marked ready.
    /// </summary>
    public string PersistCaptureSnapshot(RecordingFinalizationDescriptor descriptor)
        => Persist(descriptor, isReadyToFinalize: false);

    /// <summary>
    /// Persists a closed recording and makes it eligible for finalization.
    /// </summary>
    public string PersistReady(RecordingFinalizationDescriptor descriptor)
        => Persist(descriptor, isReadyToFinalize: true);

    private string Persist(
        RecordingFinalizationDescriptor descriptor,
        bool isReadyToFinalize)
    {
        Validate(descriptor, requireFiles: true);
        Directory.CreateDirectory(activePath);
        string path = GetDescriptorPath(descriptor.JobId);
        string temporaryPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(
                    descriptor,
                    DesktopSettingsJsonContext.Default.RecordingFinalizationDescriptor));
            File.Move(temporaryPath, path, overwrite: true);
            lock (sync)
            {
                EnsureDiscoveredCore();
                descriptors[descriptor.JobId] = descriptor;
                if (isReadyToFinalize)
                    readyToFinalize.Add(descriptor.JobId);
                else
                    readyToFinalize.Remove(descriptor.JobId);
            }
            return path;
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    public IReadOnlyList<RecordingFinalizationDescriptor> LoadReadyFinalizations()
    {
        lock (sync)
        {
            EnsureDiscoveredCore();
            return descriptors.Values
                .Where(descriptor => readyToFinalize.Contains(descriptor.JobId))
                .OrderBy(descriptor => descriptor.CreatedAt)
                .ThenBy(descriptor => descriptor.JobId)
                .ToArray();
        }
    }

    /// <summary>
    /// Preserves WAV files left without a durable descriptor by an interrupted
    /// active-recording startup. This is deliberately called during session
    /// construction, before a live writer can create a new source in the spool.
    /// Orphans are quarantined for manual recovery rather than deleted.
    /// </summary>
    public int RecoverOrphanedWaveFiles()
    {
        lock (sync)
        {
            EnsureDiscoveredCore();
            return recoveredOrphanedWaveFiles;
        }
    }

    public void Complete(RecordingFinalizationDescriptor descriptor)
    {
        Validate(descriptor, requireFiles: false);
        lock (sync)
        {
            EnsureDiscoveredCore();
            descriptors.Remove(descriptor.JobId);
            readyToFinalize.Remove(descriptor.JobId);
            TryDelete(GetDescriptorPath(descriptor.JobId));
            TryDelete(descriptor.WavePath);
        }
    }

    public void Quarantine(RecordingFinalizationDescriptor descriptor, string diagnostic)
    {
        Validate(descriptor, requireFiles: false);
        lock (sync)
        {
            EnsureDiscoveredCore();
            descriptors.Remove(descriptor.JobId);
            readyToFinalize.Remove(descriptor.JobId);
            lastError = string.IsNullOrWhiteSpace(diagnostic)
                ? "Finalization failed permanently."
                : diagnostic;
            QuarantineDescriptor(GetDescriptorPath(descriptor.JobId));
            try
            {
                if (!File.Exists(descriptor.WavePath))
                    return;
                Directory.CreateDirectory(quarantinePath);
                string destination = Path.Combine(
                    quarantinePath,
                    $"{descriptor.JobId:N}.wav.bad");
                File.Move(descriptor.WavePath, destination, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                lastError = exception.Message;
            }
        }
    }

    public RecordingFinalizationSpoolHealth GetHealth(DateTimeOffset? now = null)
    {
        lock (sync)
        {
            EnsureDiscoveredCore();
            DateTimeOffset capturedAt = now ?? DateTimeOffset.UtcNow;
            TimeSpan? oldestAge = readyToFinalize.Count == 0
                ? null
                : capturedAt - readyToFinalize
                    .Select(jobId => descriptors[jobId].CreatedAt)
                    .Min();
            return new RecordingFinalizationSpoolHealth(
                readyToFinalize.Count,
                quarantinedJobs,
                oldestAge < TimeSpan.Zero ? TimeSpan.Zero : oldestAge,
                lastError);
        }
    }

    private void EnsureDiscoveredCore()
    {
        if (discovered)
            return;
        discovered = true;
        if (!Directory.Exists(activePath))
            return;

        var describedWavePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string descriptorPath in Directory.EnumerateFiles(
                     activePath,
                     $"*{DescriptorSuffix}",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                RecordingFinalizationDescriptor descriptor =
                    JsonSerializer.Deserialize<RecordingFinalizationDescriptor>(
                        File.ReadAllText(descriptorPath),
                        DesktopSettingsJsonContext.Default.RecordingFinalizationDescriptor)
                    ?? throw new InvalidDataException("The finalization descriptor was empty.");
                Validate(descriptor, requireFiles: true);
                if (!Path.GetFullPath(descriptorPath).Equals(
                        GetDescriptorPath(descriptor.JobId),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "The finalization descriptor name does not match its job identifier.");
                }
                descriptors[descriptor.JobId] = descriptor;
                // Discovery runs once, before this process creates live writers.
                // Every descriptor already on disk therefore belongs to an
                // interrupted process and is safe to resume.
                readyToFinalize.Add(descriptor.JobId);
                describedWavePaths.Add(Path.GetFullPath(descriptor.WavePath));
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
            {
                lastError = exception.Message;
                QuarantineDescriptor(descriptorPath);
            }
        }

        foreach (string wavePath in Directory.EnumerateFiles(
                     activePath,
                     "*.wav",
                     SearchOption.TopDirectoryOnly))
        {
            if (describedWavePaths.Contains(Path.GetFullPath(wavePath)))
                continue;
            try
            {
                Directory.CreateDirectory(quarantinePath);
                string destination = Path.Combine(
                    quarantinePath,
                    $"{Path.GetFileNameWithoutExtension(wavePath)}.wav.orphan");
                File.Move(wavePath, destination, overwrite: true);
                recoveredOrphanedWaveFiles++;
                quarantinedJobs++;
                lastError = "Recovered an active recording whose finalization descriptor was missing.";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                lastError = exception.Message;
            }
        }
    }

    private void Validate(RecordingFinalizationDescriptor descriptor, bool requireFiles)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.JobId == Guid.Empty)
            throw new InvalidDataException("A finalization job identifier is required.");
        string descriptorRoot = Path.GetFullPath(descriptor.RootPath);
        if (!descriptorRoot.Equals(rootPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The finalization job belongs to a different recording root.");
        string wavePath = Path.GetFullPath(descriptor.WavePath);
        string outputPath = Path.GetFullPath(descriptor.OutputPath);
        if (!IsUnderRoot(activePath, wavePath) ||
            !wavePath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The finalization source must be a WAV file inside the active spool.");
        }
        if (!IsUnderRoot(rootPath, outputPath) ||
            IsUnderRoot(activePath, outputPath) ||
            !outputPath.EndsWith(".opus", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The final recording must be an Opus file inside the recording root.");
        }
        if (requireFiles && !File.Exists(wavePath) && !File.Exists(outputPath))
            throw new InvalidDataException("Neither the source WAV nor completed Opus file exists.");
        _ = descriptor.Format;
    }

    private void QuarantineDescriptor(string descriptorPath)
    {
        try
        {
            Directory.CreateDirectory(quarantinePath);
            string destination = Path.Combine(
                quarantinePath,
                $"{Path.GetFileName(descriptorPath)}.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.bad");
            File.Move(descriptorPath, destination, overwrite: true);
            quarantinedJobs++;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            lastError = exception.Message;
        }
    }

    private string GetDescriptorPath(Guid jobId)
        => Path.Combine(activePath, $"{jobId:N}{DescriptorSuffix}");

    private static bool IsUnderRoot(string root, string path)
    {
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A later startup scan can retry cleanup.
        }
    }
}
