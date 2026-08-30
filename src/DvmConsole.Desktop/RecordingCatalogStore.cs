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
                     () => inaccessiblePaths++,
                     cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidateVisits++;
            scannedFiles++;
            if (!scanSource.IsSafePath(opusPath, rootPath))
            {
                inaccessiblePaths++;
                continue;
            }

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
                    if (scanSource.TryDelete(opusPath, rootPath))
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
        => FileSystemPathIdentity.IsUnderRoot(rootPath, path);
}

internal interface IRecordingCatalogScanSource
{
    bool RootExists(string rootPath);

    IEnumerable<string> EnumerateOpusFiles(
        string rootPath,
        Action inaccessiblePathObserved,
        CancellationToken cancellationToken);

    bool TryRead(
        string opusPath,
        string rootPath,
        out CallRecordingMetadata metadata);

    bool IsSafePath(string opusPath, string rootPath);

    bool TryDelete(string path, string rootPath);
}

internal sealed class FileRecordingCatalogScanSource(
    OpusRecordingMetadataStore metadataStore) : IRecordingCatalogScanSource
{
    public bool RootExists(string rootPath)
        => Directory.Exists(rootPath);

    public IEnumerable<string> EnumerateOpusFiles(
        string rootPath,
        Action inaccessiblePathObserved,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<(string Path, bool IsRoot)>();
        pending.Push((rootPath, true));
        while (pending.TryPop(out (string Path, bool IsRoot) entry))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entry.IsRoot && IsDirectoryLink(entry.Path, inaccessiblePathObserved))
                continue;

            string directory = entry.Path;
            bool inaccessible = false;
            foreach (string file in EnumerateAccessible(
                         () => Directory.EnumerateFiles(
                             directory,
                             "*.opus",
                             SearchOption.TopDirectoryOnly),
                         ObserveInaccessiblePath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return file;
            }
            if (inaccessible)
                continue;

            foreach (string child in EnumerateAccessible(
                         () => Directory.EnumerateDirectories(
                             directory,
                             "*",
                             SearchOption.TopDirectoryOnly),
                         ObserveInaccessiblePath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsDirectoryLink(child, inaccessiblePathObserved))
                    continue;

                pending.Push((child, false));
            }

            void ObserveInaccessiblePath()
            {
                inaccessible = true;
                inaccessiblePathObserved();
            }
        }
    }

    private static bool IsDirectoryLink(
        string path,
        Action inaccessiblePathObserved)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            return directory.LinkTarget is not null ||
                   (directory.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            inaccessiblePathObserved();
            return true;
        }
    }

    private static IEnumerable<string> EnumerateAccessible(
        Func<IEnumerable<string>> enumerate,
        Action inaccessiblePathObserved)
    {
        IEnumerator<string>? enumerator;
        try
        {
            enumerator = enumerate().GetEnumerator();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            inaccessiblePathObserved();
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                string current;
                try
                {
                    if (!enumerator.MoveNext())
                        yield break;
                    current = enumerator.Current;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    inaccessiblePathObserved();
                    yield break;
                }

                yield return current;
            }
        }
    }

    public bool TryRead(
        string opusPath,
        string rootPath,
        out CallRecordingMetadata metadata)
        => metadataStore.TryRead(opusPath, rootPath, out metadata);

    public bool IsSafePath(string opusPath, string rootPath)
        => IsPathContainedWithoutLinks(opusPath, rootPath);

    public bool TryDelete(string path, string rootPath)
    {
        if (!IsPathContainedWithoutLinks(path, rootPath))
            return false;

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

    private static bool IsPathContainedWithoutLinks(string path, string rootPath)
    {
        try
        {
            string normalizedRoot = Path.GetFullPath(rootPath).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string normalizedPath = Path.GetFullPath(path);
            string rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!normalizedPath.StartsWith(rootPrefix, comparison))
                return false;

            string relativePath = Path.GetRelativePath(normalizedRoot, normalizedPath);
            string[] segments = relativePath.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            string current = normalizedRoot;
            for (int index = 0; index < segments.Length - 1; index++)
            {
                current = Path.Combine(current, segments[index]);
                if (IsDirectoryLink(current, static () => { }))
                    return false;
            }

            var file = new FileInfo(normalizedPath);
            return file.LinkTarget is null &&
                   (file.Attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            IOException or
            NotSupportedException or
            UnauthorizedAccessException)
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
