using DvmConsole.Audio;
using System.Text.Json;

namespace DvmConsole.Desktop;

// Owns the on-disk representation and migration of TAR metadata. Recording
// lifecycle code can work with CallRecordingMetadata without knowing how an
// OpusTags packet is encoded or rewritten.
internal sealed class OpusRecordingMetadataStore
{
    internal const string MetadataTag = "DVMCONSOLE_METADATA";
    private const int MaximumEncodedMetadataLength = 32_768;
    private static readonly JsonSerializerOptions JsonOptions = new();

    public IReadOnlyDictionary<string, string> CreateTags(CallRecordingMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return new Dictionary<string, string>
        {
            [MetadataTag] = Serialize(metadata)
        };
    }

    public bool TryRead(
        string opusPath,
        string recordingRoot,
        out CallRecordingMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(opusPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingRoot);
        metadata = null!;

        OggOpusTagSet tags = OggOpusTags.Read(opusPath);
        if (!tags.Fields.TryGetValue(MetadataTag, out string? encoded) ||
            string.IsNullOrWhiteSpace(encoded) ||
            encoded.Length > MaximumEncodedMetadataLength)
        {
            return false;
        }

        byte[] json = DecodeBase64Url(encoded);
        CallRecordingMetadata? deserialized = JsonSerializer.Deserialize<CallRecordingMetadata>(json, JsonOptions);
        if (deserialized is null)
            return false;

        string fullPath = Path.GetFullPath(opusPath);
        if (!IsUnderRoot(fullPath, recordingRoot))
            return false;

        deserialized.FilePath = fullPath;
        deserialized.FileName = Path.GetFileName(fullPath);
        deserialized.FileSizeBytes = new FileInfo(fullPath).Length;
        metadata = deserialized;
        return true;
    }

    public void TryMigrateSidecar(
        string sidecarPath,
        CallRecordingMetadata metadata,
        string recordingRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sidecarPath);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingRoot);
        if (!metadata.FilePath.EndsWith(".opus", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(metadata.FilePath))
        {
            return;
        }

        try
        {
            if (TryRead(metadata.FilePath, recordingRoot, out CallRecordingMetadata existing))
            {
                if (HasSameIdentity(existing, metadata))
                    File.Delete(sidecarPath);
                return;
            }

            OggOpusTags.Set(metadata.FilePath, MetadataTag, Serialize(metadata));
            if (!TryRead(metadata.FilePath, recordingRoot, out CallRecordingMetadata migrated) ||
                !HasSameIdentity(migrated, metadata))
            {
                return;
            }

            metadata.FileSizeBytes = migrated.FileSizeBytes;
            File.Delete(sidecarPath);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException or IOException or UnauthorizedAccessException)
        {
            // Preserve the sidecar and retry during a later catalog scan.
        }
    }

    private static string Serialize(CallRecordingMetadata metadata)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions);
        string encoded = Convert.ToBase64String(json)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        if (encoded.Length > MaximumEncodedMetadataLength)
            throw new InvalidDataException("The TAR metadata is too large to embed in the Opus file.");
        return encoded;
    }

    private static byte[] DecodeBase64Url(string encoded)
    {
        string base64 = encoded.Replace('-', '+').Replace('_', '/');
        base64 = (base64.Length % 4) switch
        {
            0 => base64,
            2 => $"{base64}==",
            3 => $"{base64}=",
            _ => throw new FormatException("The embedded TAR metadata encoding is invalid.")
        };
        return Convert.FromBase64String(base64);
    }

    private static bool HasSameIdentity(CallRecordingMetadata left, CallRecordingMetadata right)
        => CatalogKey(left).Equals(CatalogKey(right), StringComparison.OrdinalIgnoreCase);

    private static string CatalogKey(CallRecordingMetadata metadata)
        => !string.IsNullOrWhiteSpace(metadata.RecordingId)
            ? metadata.RecordingId
            : metadata.FilePath;

    private static bool IsUnderRoot(string path, string root)
    {
        string normalizedPath = Path.GetFullPath(path);
        string normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
