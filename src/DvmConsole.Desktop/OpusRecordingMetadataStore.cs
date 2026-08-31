using DvmConsole.Audio;
using System.Text.Json;

namespace DvmConsole.Desktop;

// Owns the embedded on-disk representation of TAR metadata. Recording lifecycle
// code can work with CallRecordingMetadata without knowing how an OpusTags
// packet is encoded or read.
internal sealed class OpusRecordingMetadataStore
{
    internal const string MetadataTag = "DVMCONSOLE_METADATA";
    private const int MaximumEncodedMetadataLength = 32_768;
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

        OggOpusTagSet tags = DesktopRecordingFileCodec.ReadOpusTags(opusPath);
        if (!tags.Fields.TryGetValue(MetadataTag, out string? encoded) ||
            string.IsNullOrWhiteSpace(encoded) ||
            encoded.Length > MaximumEncodedMetadataLength)
        {
            return false;
        }

        byte[] json = DecodeBase64Url(encoded);
        CallRecordingMetadata? deserialized = JsonSerializer.Deserialize(
            json,
            RecordingMetadataJsonContext.Default.CallRecordingMetadata);
        if (deserialized is null)
            return false;
        deserialized.NormalizeCompatibilityFields();

        string fullPath = Path.GetFullPath(opusPath);
        if (!IsUnderRoot(fullPath, recordingRoot))
            return false;

        deserialized.FilePath = fullPath;
        deserialized.FileName = Path.GetFileName(fullPath);
        deserialized.FileSizeBytes = new FileInfo(fullPath).Length;
        metadata = deserialized;
        return true;
    }

    private static string Serialize(CallRecordingMetadata metadata)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            metadata,
            RecordingMetadataJsonContext.Default.CallRecordingMetadata);
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

    private static bool IsUnderRoot(string path, string root)
        => FileSystemPathIdentity.IsUnderRoot(root, path);
}
