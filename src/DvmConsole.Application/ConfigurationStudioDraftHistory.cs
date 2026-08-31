using DvmConsole.Core.Settings;
using System.Security.Cryptography;
using System.Text;

namespace DvmConsole.Application;

internal sealed record ConfigurationStudioReferencedFilesSnapshot(
    string? KeyFileIdentifier,
    string? KeyFileHash,
    string KeyFileBaseline,
    string? LoadedKeyReference,
    string? KeyFileLoadError,
    bool KeyFileLoadIsWarning,
    string KeyFileContent,
    IReadOnlyDictionary<string, string> AliasContents,
    IReadOnlyDictionary<string, string> AliasFileHashes,
    IReadOnlyDictionary<string, string> AliasFileBaselines,
    IReadOnlyList<string> AliasLoadErrors,
    IReadOnlyList<string> AliasLoadWarnings,
    string LoadedAliasReference);

internal sealed record ConfigurationStudioDraftSnapshot(
    string Yaml,
    ConfigurationDraftIdentityLayout IdentityLayout,
    ConfigurationStudioReferencedFilesSnapshot ReferencedFiles,
    IReadOnlyDictionary<Guid, WidgetPositionSetting> WidgetPositions,
    IReadOnlyDictionary<Guid, string> ZoneSystemAssignments,
    IReadOnlySet<Guid> CallPrioritySystemIds,
    string Fingerprint)
{
    public static string ComputeFingerprint(IEnumerable<string> components)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string component in components)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(component);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}

internal sealed class ConfigurationStudioDraftHistory
{
    private readonly Stack<ConfigurationStudioDraftSnapshot> undo = [];
    private readonly Stack<ConfigurationStudioDraftSnapshot> redo = [];

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    public void Record(ConfigurationStudioDraftSnapshot before, ConfigurationStudioDraftSnapshot after)
    {
        if (string.Equals(before.Fingerprint, after.Fingerprint, StringComparison.Ordinal))
            return;
        undo.Push(before);
        redo.Clear();
    }

    public ConfigurationStudioDraftSnapshot? Undo(ConfigurationStudioDraftSnapshot current)
    {
        if (undo.Count == 0)
            return null;
        redo.Push(current);
        return undo.Pop();
    }

    public ConfigurationStudioDraftSnapshot? Redo(ConfigurationStudioDraftSnapshot current)
    {
        if (redo.Count == 0)
            return null;
        undo.Push(current);
        return redo.Pop();
    }

    public void Clear()
    {
        undo.Clear();
        redo.Clear();
    }
}
