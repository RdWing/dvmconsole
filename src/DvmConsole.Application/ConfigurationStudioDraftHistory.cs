// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

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
    IReadOnlyDictionary<string, string> AliasReferenceIdentifiers,
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
    private const int MaximumEntries = 100;
    private const long MaximumRetainedBytes = 64L * 1024 * 1024;
    private readonly List<HistoryEntry> undo = [];
    private readonly List<HistoryEntry> redo = [];
    private long retainedBytes;

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    public void Record(ConfigurationStudioDraftSnapshot before, ConfigurationStudioDraftSnapshot after)
    {
        if (string.Equals(before.Fingerprint, after.Fingerprint, StringComparison.Ordinal))
            return;
        Add(undo, before);
        Clear(redo);
        TrimToLimits();
    }

    public ConfigurationStudioDraftSnapshot? Undo(ConfigurationStudioDraftSnapshot current)
    {
        if (undo.Count == 0)
            return null;
        Add(redo, current);
        ConfigurationStudioDraftSnapshot snapshot = RemoveLast(undo);
        TrimToLimits();
        return snapshot;
    }

    public ConfigurationStudioDraftSnapshot? Redo(ConfigurationStudioDraftSnapshot current)
    {
        if (redo.Count == 0)
            return null;
        Add(undo, current);
        ConfigurationStudioDraftSnapshot snapshot = RemoveLast(redo);
        TrimToLimits();
        return snapshot;
    }

    public void Clear()
    {
        Clear(undo);
        Clear(redo);
    }

    private void Add(List<HistoryEntry> destination, ConfigurationStudioDraftSnapshot snapshot)
    {
        var entry = new HistoryEntry(snapshot, EstimateRetainedBytes(snapshot));
        destination.Add(entry);
        retainedBytes += entry.RetainedBytes;
    }

    private ConfigurationStudioDraftSnapshot RemoveLast(List<HistoryEntry> source)
    {
        int index = source.Count - 1;
        HistoryEntry entry = source[index];
        source.RemoveAt(index);
        retainedBytes -= entry.RetainedBytes;
        return entry.Snapshot;
    }

    private void Clear(List<HistoryEntry> source)
    {
        foreach (HistoryEntry entry in source)
            retainedBytes -= entry.RetainedBytes;
        source.Clear();
    }

    private void TrimToLimits()
    {
        while (undo.Count + redo.Count > MaximumEntries || retainedBytes > MaximumRetainedBytes)
        {
            List<HistoryEntry> source = undo.Count > 0 ? undo : redo;
            HistoryEntry removed = source[0];
            source.RemoveAt(0);
            retainedBytes -= removed.RetainedBytes;
        }
    }

    private static long EstimateRetainedBytes(ConfigurationStudioDraftSnapshot snapshot)
    {
        long characters = snapshot.Yaml.Length + snapshot.Fingerprint.Length;
        ConfigurationStudioReferencedFilesSnapshot files = snapshot.ReferencedFiles;
        characters += StringLength(files.KeyFileIdentifier) + StringLength(files.KeyFileHash) +
            files.KeyFileBaseline.Length + StringLength(files.LoadedKeyReference) +
            StringLength(files.KeyFileLoadError) + files.KeyFileContent.Length +
            files.LoadedAliasReference.Length;
        characters += DictionaryCharacters(files.AliasContents) +
            DictionaryCharacters(files.AliasReferenceIdentifiers) +
            DictionaryCharacters(files.AliasFileHashes) +
            DictionaryCharacters(files.AliasFileBaselines) +
            files.AliasLoadErrors.Sum(static value => value.Length) +
            files.AliasLoadWarnings.Sum(static value => value.Length) +
            snapshot.ZoneSystemAssignments.Values.Sum(static value => value.Length);
        long structuralBytes =
            snapshot.IdentityLayout.SystemIds.Count * 16L +
            snapshot.IdentityLayout.Zones.Count * 32L +
            snapshot.WidgetPositions.Count * 64L +
            snapshot.CallPrioritySystemIds.Count * 16L;
        return checked(characters * sizeof(char) + structuralBytes);
    }

    private static int StringLength(string? value) => value?.Length ?? 0;

    private static long DictionaryCharacters(IReadOnlyDictionary<string, string> values)
        => values.Sum(static entry => (long)entry.Key.Length + entry.Value.Length);

    private sealed record HistoryEntry(
        ConfigurationStudioDraftSnapshot Snapshot,
        long RetainedBytes);
}
