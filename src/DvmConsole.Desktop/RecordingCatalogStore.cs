using System.Diagnostics;
using System.Text.Json;

namespace DvmConsole.Desktop;

internal sealed class RecordingCatalogStore
{
    private readonly OpusRecordingMetadataStore metadataStore = new();
    private readonly IRecordingCatalogScanSource scanSource;

    public RecordingCatalogStore()
    {
        scanSource = new FileRecordingCatalogScanSource(metadataStore);
    }

    internal RecordingCatalogStore(IRecordingCatalogScanSource scanSource)
    {
        this.scanSource = scanSource ?? throw new ArgumentNullException(nameof(scanSource));
    }

    public IReadOnlyList<CallRecordingMetadata> Load(
        string rootPath,
        CancellationToken cancellationToken)
        => Scan(rootPath, retentionDays: 0, DateTimeOffset.UtcNow, cancellationToken).Recordings;

    public RecordingCatalogScanResult Scan(
        string rootPath,
        int retentionDays,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (retentionDays < 0)
            throw new ArgumentOutOfRangeException(nameof(retentionDays));

        long startedAt = Stopwatch.GetTimestamp();
        cancellationToken.ThrowIfCancellationRequested();
        if (!scanSource.RootExists(rootPath))
            return RecordingCatalogScanResult.Empty with
            {
                Duration = Stopwatch.GetElapsedTime(startedAt)
            };

        DateTimeOffset? cutoff = retentionDays == 0
            ? null
            : now.ToUniversalTime().AddDays(-retentionDays);
        var recordingsByKey = new Dictionary<string, CallRecordingMetadata>(StringComparer.OrdinalIgnoreCase);
        int scannedFiles = 0;
        int prunedFiles = 0;
        int damagedFiles = 0;
        int inaccessiblePaths = 0;
        long candidateVisits = 0;
        long metadataReads = 0;
        long retentionEvaluations = 0;
        long keyLookups = 0;
        long keyWrites = 0;

        foreach (string opusPath in scanSource.EnumerateOpusFiles(
                     rootPath,
                     cancellationToken,
                     () => inaccessiblePaths++))
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidateVisits++;
            scannedFiles++;
            try
            {
                metadataReads++;
                if (!scanSource.TryRead(opusPath, rootPath, out CallRecordingMetadata metadata))
                {
                    damagedFiles++;
                    continue;
                }

                retentionEvaluations++;
                if (cutoff is not null && metadata.UtcEndTime <= cutoff.Value)
                {
                    if (scanSource.TryDelete(opusPath))
                        prunedFiles++;
                    else
                        inaccessiblePaths++;
                    continue;
                }

                string key = GetCatalogKey(metadata);
                keyLookups++;
                if (!recordingsByKey.TryGetValue(key, out CallRecordingMetadata? existing) ||
                    metadata.UtcStartTime > existing.UtcStartTime)
                {
                    keyWrites++;
                    recordingsByKey[key] = metadata;
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException or IOException or UnauthorizedAccessException)
            {
                // A damaged or unrelated Opus file must not hide the rest of
                // the recording catalog.
                damagedFiles++;
            }
        }

        CallRecordingMetadata[] recordings = recordingsByKey.Values
            .OrderByDescending(recording => recording.UtcStartTime)
            .ThenBy(recording => recording.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new RecordingCatalogScanResult(
            recordings,
            scannedFiles,
            prunedFiles,
            damagedFiles,
            inaccessiblePaths,
            Stopwatch.GetElapsedTime(startedAt),
            new RecordingCatalogOperationMetrics(
                candidateVisits,
                metadataReads,
                retentionEvaluations,
                keyLookups,
                keyWrites));
    }

    public bool Delete(string rootPath, CallRecordingMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (!TryGetExistingPath(rootPath, metadata, out string recordingPath))
            return false;

        bool deleted = false;
        try
        {
            if (File.Exists(recordingPath))
            {
                File.Delete(recordingPath);
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

    public bool TryGetExistingPath(
        string rootPath,
        CallRecordingMetadata metadata,
        out string recordingPath)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        recordingPath = string.Empty;
        if (string.IsNullOrWhiteSpace(metadata.FilePath))
            return false;

        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(metadata.FilePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!normalizedPath.EndsWith(".opus", StringComparison.OrdinalIgnoreCase) ||
            !IsUnderRoot(rootPath, normalizedPath) ||
            !File.Exists(normalizedPath))
        {
            return false;
        }

        recordingPath = normalizedPath;
        return true;
    }

    public IReadOnlyDictionary<string, string> CreateTags(CallRecordingMetadata metadata)
        => metadataStore.CreateTags(metadata);

    public bool TryRead(
        string path,
        string rootPath,
        out CallRecordingMetadata metadata)
        => metadataStore.TryRead(path, rootPath, out metadata);

    public static string GetCatalogKey(CallRecordingMetadata metadata)
        => !string.IsNullOrWhiteSpace(metadata.RecordingId)
            ? metadata.RecordingId
            : metadata.FilePath;

    private static bool IsUnderRoot(string rootPath, string path)
    {
        string normalizedPath = Path.GetFullPath(path);
        string normalizedRoot = rootPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}

internal interface IRecordingCatalogScanSource
{
    bool RootExists(string rootPath);

    IEnumerable<string> EnumerateOpusFiles(
        string rootPath,
        CancellationToken cancellationToken,
        Action inaccessiblePathObserved);

    bool TryRead(
        string opusPath,
        string rootPath,
        out CallRecordingMetadata metadata);

    bool TryDelete(string path);
}

internal sealed class FileRecordingCatalogScanSource(
    OpusRecordingMetadataStore metadataStore) : IRecordingCatalogScanSource
{
    public bool RootExists(string rootPath)
        => Directory.Exists(rootPath);

    public IEnumerable<string> EnumerateOpusFiles(
        string rootPath,
        CancellationToken cancellationToken,
        Action inaccessiblePathObserved)
    {
        var pending = new Stack<string>();
        pending.Push(rootPath);
        while (pending.TryPop(out string? directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] files;
            string[] directories;
            try
            {
                files = Directory.GetFiles(directory, "*.opus", SearchOption.TopDirectoryOnly);
                directories = Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                inaccessiblePathObserved();
                continue;
            }

            foreach (string file in files)
                yield return file;
            foreach (string child in directories)
                pending.Push(child);
        }
    }

    public bool TryRead(
        string opusPath,
        string rootPath,
        out CallRecordingMetadata metadata)
        => metadataStore.TryRead(opusPath, rootPath, out metadata);

    public bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

internal sealed record RecordingCatalogScanResult(
    IReadOnlyList<CallRecordingMetadata> Recordings,
    int ScannedFiles,
    int PrunedFiles,
    int DamagedFiles,
    int InaccessiblePaths,
    TimeSpan Duration,
    RecordingCatalogOperationMetrics Operations)
{
    public static RecordingCatalogScanResult Empty { get; } = new(
        [],
        0,
        0,
        0,
        0,
        TimeSpan.Zero,
        RecordingCatalogOperationMetrics.Empty);
}

internal sealed record RecordingCatalogOperationMetrics(
    long CandidateVisits,
    long MetadataReads,
    long RetentionEvaluations,
    long KeyLookups,
    long KeyWrites)
{
    public static RecordingCatalogOperationMetrics Empty { get; } = new(0, 0, 0, 0, 0);

    // Sorting is reported separately by elapsed scan duration. These counters
    // cover the one-pass traversal, retention decision, and keyed reconciliation
    // whose work must stay bounded by a constant amount per source recording.
    public long TraversalAndReconciliationWork =>
        CandidateVisits + MetadataReads + RetentionEvaluations + KeyLookups + KeyWrites;
}
