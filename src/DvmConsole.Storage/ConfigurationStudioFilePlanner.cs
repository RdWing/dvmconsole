// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Storage;

/// <summary>Plans codeplug and companion file content with the existing optimistic conflict hashes.</summary>
public static class ConfigurationStudioFilePlanner
{
    public static IReadOnlyList<ConfigurationFileChange> CreateFiles(ConfigurationDocument document,
        ConsoleConfiguration configuration, ConfigurationStudioSaveState state, string destinationPath,
        bool includeUnchangedCompanions = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        string fullDestination = Path.GetFullPath(destinationPath);
        var files = new List<ConfigurationFileChange>
        {
            new(
                fullDestination,
                state.Yaml,
                document.SourcePath is not null &&
                FileSystemPathIdentity.AreEquivalent(fullDestination, document.SourcePath)
                    ? document.SourceHash
                    : null,
                "Codeplug",
                ContainsSecrets: true)
        };

        if (state.KeyFileIdentifier is not null)
        {
            string keyTarget = ResolveReferencedSaveTarget(
                configuration.KeyFile,
                state.KeyFileIdentifier,
                fullDestination,
                document.SourcePath);
            if (includeUnchangedCompanions || state.KeyFileDirty ||
                !FileSystemPathIdentity.AreEquivalent(keyTarget, state.KeyFileIdentifier))
            {
                files.Add(new ConfigurationFileChange(
                    keyTarget,
                    state.KeyFileContent,
                    GetExpectedHash(keyTarget, state.KeyFileIdentifier, state.KeyFileHash),
                    "Encryption key file",
                    ContainsSecrets: true));
            }
        }

        foreach ((string currentPath, string content) in state.AliasContents)
        {
            bool dirty = !state.AliasFileBaselines.TryGetValue(currentPath, out string? baseline) ||
                         !string.Equals(baseline, content, StringComparison.Ordinal);
            foreach (string target in GetAliasSaveTargets(
                         currentPath,
                         fullDestination,
                         document.SourcePath,
                         configuration))
            {
                if (!includeUnchangedCompanions && !dirty && FileSystemPathIdentity.AreEquivalent(target, currentPath))
                    continue;
                files.Add(new ConfigurationFileChange(
                    target,
                    content,
                    GetExpectedHash(target, currentPath, state.AliasFileHashes.GetValueOrDefault(currentPath)),
                    "RID alias file",
                    ContainsSecrets: false));
            }
        }

        return files;
    }

    private static string ResolveReferencedSaveTarget(
        string? reference,
        string currentResolvedPath,
        string codeplugDestination,
        string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(reference) || Path.IsPathRooted(reference) || sourcePath is null)
            return currentResolvedPath;
        string sourceDirectory = Path.GetDirectoryName(sourcePath) ?? AppContext.BaseDirectory;
        string destinationDirectory = Path.GetDirectoryName(codeplugDestination) ?? AppContext.BaseDirectory;
        if (FileSystemPathIdentity.AreEquivalent(sourceDirectory, destinationDirectory))
            return currentResolvedPath;
        return Path.GetFullPath(Path.Combine(destinationDirectory, reference));
    }

    private static IReadOnlyList<string> GetAliasSaveTargets(
        string currentResolvedPath,
        string codeplugDestination,
        string? sourcePath,
        ConsoleConfiguration configuration)
    {
        var targets = new HashSet<string>(FileSystemPathIdentity.Comparer);
        foreach (SystemConfiguration system in configuration.Systems)
        {
            if (string.IsNullOrWhiteSpace(system.AliasPath))
                continue;
            string sourceResolved;
            try
            {
                sourceResolved = ConfigurationLoader.ResolvePath(configuration, system.AliasPath);
            }
            catch (InvalidOperationException)
            {
                continue;
            }
            if (FileSystemPathIdentity.AreEquivalent(sourceResolved, currentResolvedPath))
            {
                targets.Add(ResolveReferencedSaveTarget(
                    system.AliasPath,
                    currentResolvedPath,
                    codeplugDestination,
                    sourcePath));
            }
        }
        if (targets.Count == 0)
            targets.Add(currentResolvedPath);
        return targets.ToArray();
    }

    private static string? GetExpectedHash(string target, string source, string? sourceHash)
    {
        if (FileSystemPathIdentity.AreEquivalent(target, source))
            return sourceHash;
        return File.Exists(target) ? ConfigurationDocument.ComputeFileHash(target) : null;
    }
}
