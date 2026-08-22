using System.Text.Json;

namespace DvmConsole.Desktop;

internal sealed class RecordingCatalogStore
{
    private readonly OpusRecordingMetadataStore metadataStore = new();

    public IReadOnlyList<CallRecordingMetadata> Load(
        string rootPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(rootPath))
            return [];

        List<CallRecordingMetadata> recordings = [];
        string[] opusPaths;
        try
        {
            opusPaths = Directory.EnumerateFiles(rootPath, "*.opus", SearchOption.AllDirectories).ToArray();
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
                if (metadataStore.TryRead(opusPath, rootPath, out CallRecordingMetadata metadata))
                    recordings.Add(metadata);
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException or IOException or UnauthorizedAccessException)
            {
                // A damaged or unrelated Opus file must not hide the rest of
                // the recording catalog.
            }
        }

        return recordings
            .OrderByDescending(recording => recording.UtcStartTime)
            .ThenBy(recording => recording.FileName, StringComparer.OrdinalIgnoreCase)
            .GroupBy(GetCatalogKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
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
