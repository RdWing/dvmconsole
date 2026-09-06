// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Configuration;

namespace DvmConsole.Presentation;

/// <summary>
/// Owns the mutable key and alias companion-file state for one Studio draft.
/// Persistence remains a host responsibility; this type only tracks parsed
/// content, source hashes, dirty baselines, and reference resolution.
/// </summary>
internal sealed class ConfigurationStudioCompanionState
{
    private const string NewlySelectedBaseline = "\0selected-managed-companion";
    private readonly Dictionary<string, List<RadioAlias>> aliasTables =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> aliasReferenceIdentifiers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> aliasFileHashes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> aliasFileBaselines =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> serializedAliasContents =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> dirtyAliasContents = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> aliasLoadErrors = [];
    private readonly List<string> aliasLoadWarnings = [];

    public KeyContainer Keys { get; private set; } = new();
    public string? KeyFileIdentifier { get; private set; }
    public string? KeyFileHash { get; private set; }
    public string? KeyFileLoadError { get; private set; }
    public bool KeyFileLoadIsWarning { get; private set; }
    public IReadOnlyDictionary<string, List<RadioAlias>> AliasTables => aliasTables;
    public IReadOnlyList<string> AliasLoadErrors => aliasLoadErrors;
    public IReadOnlyList<string> AliasLoadWarnings => aliasLoadWarnings;
    public bool HasKeyFile => KeyFileIdentifier is not null;
    public bool IsKeyFileDirty => KeyFileIdentifier is not null &&
        !string.Equals(keyFileBaseline, GetSerializedKeys(), StringComparison.Ordinal);
    public bool AliasFilesDirty => aliasTables.Any(entry =>
        !aliasFileBaselines.TryGetValue(entry.Key, out string? baseline) ||
        !string.Equals(baseline, GetSerializedAliases(entry.Key, entry.Value), StringComparison.Ordinal));

    private string keyFileBaseline = string.Empty;
    private string serializedKeys = KeyFileLoader.Serialize(new KeyContainer());
    private bool keysContentDirty;
    private string? loadedKeyReference;
    private string loadedAliasReference = string.Empty;
    private ConfigurationStudioReferencedFilesSnapshot? cachedSnapshot;

    public bool ReferencesMatch(ConsoleConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return string.Equals(loadedKeyReference, configuration.KeyFile, StringComparison.Ordinal) &&
               string.Equals(loadedAliasReference, CreateAliasReference(configuration), StringComparison.Ordinal);
    }

    public void Load(
        ConfigurationStudioCompanionSnapshot snapshot,
        ConsoleConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(configuration);

        Keys = new KeyContainer();
        KeyFileIdentifier = null;
        KeyFileHash = null;
        keyFileBaseline = string.Empty;
        serializedKeys = KeyFileLoader.Serialize(Keys);
        keysContentDirty = false;
        KeyFileLoadError = null;
        KeyFileLoadIsWarning = false;
        loadedKeyReference = configuration.KeyFile;
        if (snapshot.KeyFile is not null)
        {
            KeyFileIdentifier = snapshot.KeyFile.Identifier;
            KeyFileHash = snapshot.KeyFile.ContentHash;
            KeyFileLoadError = snapshot.KeyFile.LoadIssue;
            KeyFileLoadIsWarning = snapshot.KeyFile.LoadIssueIsWarning;
            if (snapshot.KeyFile.Content is not null)
            {
                Keys = KeyFileLoader.Parse(snapshot.KeyFile.Content);
                serializedKeys = KeyFileLoader.Serialize(Keys);
                keyFileBaseline = serializedKeys;
            }
        }

        aliasTables.Clear();
        aliasReferenceIdentifiers.Clear();
        aliasFileHashes.Clear();
        aliasFileBaselines.Clear();
        serializedAliasContents.Clear();
        dirtyAliasContents.Clear();
        aliasLoadErrors.Clear();
        aliasLoadWarnings.Clear();
        loadedAliasReference = CreateAliasReference(configuration);
        foreach (ConfigurationStudioAliasCompanion aliasFile in snapshot.AliasFiles)
        {
            List<RadioAlias> aliases = string.IsNullOrEmpty(aliasFile.Content)
                ? []
                : AliasFileLoader.Parse(aliasFile.Content);
            aliasTables[aliasFile.Identifier] = aliases;
            foreach (string reference in aliasFile.References)
                aliasReferenceIdentifiers[reference] = aliasFile.Identifier;
            if (aliasFile.ContentHash is not null)
                aliasFileHashes[aliasFile.Identifier] = aliasFile.ContentHash;
            string serialized = AliasFileLoader.Serialize(aliases);
            serializedAliasContents[aliasFile.Identifier] = serialized;
            aliasFileBaselines[aliasFile.Identifier] = serialized;
        }
        aliasLoadErrors.AddRange(snapshot.AliasErrors);
        aliasLoadWarnings.AddRange(snapshot.AliasWarnings);
        cachedSnapshot = null;
    }

    public string AttachKeyFile(
        ConsoleConfiguration configuration,
        string suggestedName,
        string content)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedName);
        ArgumentNullException.ThrowIfNull(content);

        KeyContainer parsedKeys = KeyFileLoader.Parse(content);
        string reference = ConfigurationStudioAliasEditor.CreateManagedCompanionReference(
            suggestedName,
            configuration.Systems.Select(system => system.AliasPath));
        configuration.KeyFile = reference;
        Keys = parsedKeys;
        KeyFileIdentifier = reference;
        KeyFileHash = null;
        keyFileBaseline = NewlySelectedBaseline;
        serializedKeys = KeyFileLoader.Serialize(Keys);
        keysContentDirty = false;
        KeyFileLoadError = null;
        KeyFileLoadIsWarning = false;
        loadedKeyReference = reference;
        cachedSnapshot = null;
        return reference;
    }

    public string AttachAliasFile(
        ConsoleConfiguration configuration,
        SystemConfiguration system,
        string suggestedName,
        string content)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedName);
        ArgumentNullException.ThrowIfNull(content);

        List<RadioAlias> selectedAliases = AliasFileLoader.Parse(content);
        string? previousIdentifier = FindAliasTableIdentifier(system.AliasPath);
        string reference = ConfigurationStudioAliasEditor.CreateManagedCompanionReference(
            suggestedName,
            configuration.Systems
                .Where(candidate => !ReferenceEquals(candidate, system))
                .Select(candidate => candidate.AliasPath)
                .Append(configuration.KeyFile));
        system.AliasPath = reference;
        aliasTables[reference] = selectedAliases;
        aliasReferenceIdentifiers[reference] = reference;
        aliasFileHashes.Remove(reference);
        aliasFileBaselines[reference] = NewlySelectedBaseline;
        serializedAliasContents[reference] = AliasFileLoader.Serialize(selectedAliases);
        dirtyAliasContents.Remove(reference);
        bool previousIdentifierStillReferenced = previousIdentifier is not null &&
            configuration.Systems.Any(candidate =>
                !ReferenceEquals(candidate, system) &&
                string.Equals(
                    FindAliasTableIdentifier(candidate.AliasPath),
                    previousIdentifier,
                    StringComparison.OrdinalIgnoreCase));
        if (previousIdentifier is not null &&
            !string.Equals(previousIdentifier, reference, StringComparison.OrdinalIgnoreCase) &&
            !previousIdentifierStillReferenced)
        {
            aliasTables.Remove(previousIdentifier);
            aliasFileHashes.Remove(previousIdentifier);
            aliasFileBaselines.Remove(previousIdentifier);
            serializedAliasContents.Remove(previousIdentifier);
            dirtyAliasContents.Remove(previousIdentifier);
            foreach (string mappedReference in aliasReferenceIdentifiers
                         .Where(entry => string.Equals(
                             entry.Value,
                             previousIdentifier,
                             StringComparison.OrdinalIgnoreCase))
                         .Select(entry => entry.Key)
                         .ToArray())
            {
                aliasReferenceIdentifiers.Remove(mappedReference);
            }
        }
        aliasLoadErrors.RemoveAll(message =>
            message.Contains($"'{system.Name}'", StringComparison.OrdinalIgnoreCase));
        aliasLoadWarnings.RemoveAll(message =>
            message.Contains($"'{system.Name}'", StringComparison.OrdinalIgnoreCase));
        loadedAliasReference = CreateAliasReference(configuration);
        cachedSnapshot = null;
        return reference;
    }

    public void EnsureKeyFile(ConsoleConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (HasKeyFile)
            return;

        string reference = ConfigurationStudioAliasEditor.CreateManagedCompanionReference(
            "keys.clear",
            configuration.Systems.Select(system => system.AliasPath));
        configuration.KeyFile = reference;
        Keys = new KeyContainer();
        KeyFileIdentifier = reference;
        KeyFileHash = null;
        keyFileBaseline = NewlySelectedBaseline;
        serializedKeys = KeyFileLoader.Serialize(Keys);
        keysContentDirty = false;
        KeyFileLoadError = null;
        KeyFileLoadIsWarning = false;
        loadedKeyReference = reference;
        cachedSnapshot = null;
    }

    public void AddKey(KeyEntry key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Keys.Keys.Add(key);
        keysContentDirty = true;
        cachedSnapshot = null;
        KeyFileLoadError = null;
        KeyFileLoadIsWarning = false;
    }

    public void RemoveKey(KeyEntry key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Keys.Keys.Remove(key);
        keysContentDirty = true;
        cachedSnapshot = null;
    }

    public (string Identifier, RadioAlias Alias) AddAlias(
        ConsoleConfiguration configuration,
        SystemConfiguration system)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(system);

        string? identifier = FindAliasTableIdentifier(system.AliasPath);
        if (identifier is null)
        {
            identifier = ConfigurationStudioAliasEditor.CreateManagedCompanionReference(
                "aliases.yml",
                configuration.Systems
                    .Where(candidate => !ReferenceEquals(candidate, system))
                    .Select(candidate => candidate.AliasPath)
                    .Append(configuration.KeyFile));
            system.AliasPath = identifier;
            aliasTables[identifier] = [];
            aliasReferenceIdentifiers[identifier] = identifier;
            aliasFileBaselines[identifier] = NewlySelectedBaseline;
            serializedAliasContents[identifier] = AliasFileLoader.Serialize(aliasTables[identifier]);
            loadedAliasReference = CreateAliasReference(configuration);
        }

        List<RadioAlias> table = aliasTables[identifier];
        var alias = new RadioAlias
        {
            Rid = ConfigurationStudioAliasEditor.NextAvailableRid(table),
            Alias = string.Empty
        };
        table.Add(alias);
        dirtyAliasContents.Add(identifier);
        cachedSnapshot = null;
        return (identifier, alias);
    }

    public bool RemoveAlias(ConfigurationAliasRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!aliasTables.TryGetValue(row.Identifier, out List<RadioAlias>? table) ||
            !table.Remove(row.Alias))
        {
            return false;
        }

        dirtyAliasContents.Add(row.Identifier);
        cachedSnapshot = null;
        return true;
    }

    public void ApplySystemRename(string previousName, string currentName)
    {
        bool changed = false;
        foreach (KeyEntry key in Keys.Keys)
        {
            if (string.Equals(key.System, previousName, StringComparison.OrdinalIgnoreCase))
            {
                key.System = currentName;
                changed = true;
            }
        }
        if (changed)
        {
            keysContentDirty = true;
            cachedSnapshot = null;
        }
    }

    public void MarkKeyContentChanged()
    {
        keysContentDirty = true;
        cachedSnapshot = null;
    }

    public void MarkAliasContentChanged(string identifier)
    {
        if (aliasTables.ContainsKey(identifier))
        {
            dirtyAliasContents.Add(identifier);
            cachedSnapshot = null;
        }
    }

    public string? FindAliasTableIdentifier(string reference)
        => ConfigurationStudioAliasEditor.FindTableIdentifier(
            reference,
            aliasTables,
            aliasReferenceIdentifiers);

    public IReadOnlyList<ConfigurationAliasRow> ProjectAliasRows()
        => ConfigurationStudioAliasEditor.ProjectRows(aliasTables);

    public ConfigurationStudioSaveState CaptureSaveState(
        string yaml,
        IReadOnlyList<ConfigurationValidationIssue> issues)
        => new(
            yaml,
            KeyFileIdentifier,
            KeyFileHash,
            IsKeyFileDirty,
            GetSerializedKeys(),
            aliasTables.ToDictionary(
                entry => entry.Key,
                entry => GetSerializedAliases(entry.Key, entry.Value),
                StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(aliasFileHashes, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(aliasFileBaselines, StringComparer.OrdinalIgnoreCase),
            issues);

    public IReadOnlyDictionary<string, string> CaptureExportContents(
        ConsoleConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var contents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (KeyFileIdentifier is not null && !string.IsNullOrWhiteSpace(configuration.KeyFile))
            contents[configuration.KeyFile] = GetSerializedKeys();
        foreach (SystemConfiguration system in configuration.Systems)
        {
            if (string.IsNullOrWhiteSpace(system.AliasPath))
                continue;
            string? identifier = FindAliasTableIdentifier(system.AliasPath);
            if (identifier is not null && aliasTables.TryGetValue(identifier, out List<RadioAlias>? aliases))
                contents[system.AliasPath] = GetSerializedAliases(identifier, aliases);
        }
        return contents;
    }

    public ConfigurationStudioReferencedFilesSnapshot CaptureSnapshot()
        => cachedSnapshot ??= new(
            KeyFileIdentifier,
            KeyFileHash,
            keyFileBaseline,
            loadedKeyReference,
            KeyFileLoadError,
            KeyFileLoadIsWarning,
            GetSerializedKeys(),
            aliasTables.ToDictionary(
                entry => entry.Key,
                entry => GetSerializedAliases(entry.Key, entry.Value),
                StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(aliasReferenceIdentifiers, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(aliasFileHashes, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(aliasFileBaselines, StringComparer.OrdinalIgnoreCase),
            aliasLoadErrors.ToArray(),
            aliasLoadWarnings.ToArray(),
            loadedAliasReference);

    public void Restore(ConfigurationStudioReferencedFilesSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        KeyFileIdentifier = snapshot.KeyFileIdentifier;
        KeyFileHash = snapshot.KeyFileHash;
        keyFileBaseline = snapshot.KeyFileBaseline;
        loadedKeyReference = snapshot.LoadedKeyReference;
        KeyFileLoadError = snapshot.KeyFileLoadError;
        KeyFileLoadIsWarning = snapshot.KeyFileLoadIsWarning;
        Keys = string.IsNullOrWhiteSpace(snapshot.KeyFileContent)
            ? new KeyContainer()
            : KeyFileLoader.Parse(snapshot.KeyFileContent);
        serializedKeys = KeyFileLoader.Serialize(Keys);
        keysContentDirty = false;

        aliasTables.Clear();
        serializedAliasContents.Clear();
        dirtyAliasContents.Clear();
        foreach (KeyValuePair<string, string> entry in snapshot.AliasContents)
        {
            aliasTables[entry.Key] = AliasFileLoader.Parse(entry.Value);
            serializedAliasContents[entry.Key] = entry.Value;
        }
        aliasReferenceIdentifiers.Clear();
        foreach (KeyValuePair<string, string> entry in snapshot.AliasReferenceIdentifiers)
            aliasReferenceIdentifiers[entry.Key] = entry.Value;
        aliasFileHashes.Clear();
        foreach (KeyValuePair<string, string> entry in snapshot.AliasFileHashes)
            aliasFileHashes[entry.Key] = entry.Value;
        aliasFileBaselines.Clear();
        foreach (KeyValuePair<string, string> entry in snapshot.AliasFileBaselines)
            aliasFileBaselines[entry.Key] = entry.Value;
        aliasLoadErrors.Clear();
        aliasLoadErrors.AddRange(snapshot.AliasLoadErrors);
        aliasLoadWarnings.Clear();
        aliasLoadWarnings.AddRange(snapshot.AliasLoadWarnings);
        loadedAliasReference = snapshot.LoadedAliasReference;
        cachedSnapshot = snapshot;
    }

    private static string CreateAliasReference(ConsoleConfiguration configuration)
        => string.Join("\u001F", configuration.Systems.Select(system => system.AliasPath ?? string.Empty));

    private string GetSerializedKeys()
    {
        if (!keysContentDirty)
            return serializedKeys;
        serializedKeys = KeyFileLoader.Serialize(Keys);
        keysContentDirty = false;
        return serializedKeys;
    }

    private string GetSerializedAliases(string identifier, List<RadioAlias> aliases)
    {
        if (!dirtyAliasContents.Remove(identifier) &&
            serializedAliasContents.TryGetValue(identifier, out string? serialized))
        {
            return serialized;
        }

        serialized = AliasFileLoader.Serialize(aliases);
        serializedAliasContents[identifier] = serialized;
        return serialized;
    }
}
