using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

/// <summary>
/// Temporary desktop migration adapter for the legacy path-based Studio save transaction.
/// Managed configuration commits consume the resulting content and do not modify imported files.
/// </summary>
internal sealed class DesktopConfigurationStudioSavePlanner(
    ConfigurationStudioViewModel viewModel,
    UserSettingsStore settingsStore)
{
    private readonly ConfigurationStudioViewModel viewModel =
        viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    private readonly UserSettingsStore settingsStore =
        settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));

    public ConfigurationSavePlan CreatePlan(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        string fullDestination = Path.GetFullPath(destinationPath);
        ConfigurationStudioSaveState state = viewModel.CaptureSaveState();
        ConfigurationDocument document = viewModel.Document;
        ConsoleConfiguration configuration = viewModel.Configuration;
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
            if (state.KeyFileDirty ||
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
                if (!dirty && FileSystemPathIdentity.AreEquivalent(target, currentPath))
                    continue;
                files.Add(new ConfigurationFileChange(
                    target,
                    content,
                    GetExpectedHash(target, currentPath, state.AliasFileHashes.GetValueOrDefault(currentPath)),
                    "RID alias file",
                    ContainsSecrets: false));
            }
        }

        UserSettings settings = settingsStore.Load();
        viewModel.ApplyOperatorStateForSave(settings, fullDestination);
        UserSettingsSnapshot settingsSnapshot = settingsStore.CaptureSnapshot(settings);
        files.Add(new ConfigurationFileChange(
            settingsStore.Path,
            settingsSnapshot.Json,
            File.Exists(settingsStore.Path)
                ? ConfigurationDocument.ComputeFileHash(settingsStore.Path)
                : null,
            "Operator settings",
            ContainsSecrets: false));

        return new ConfigurationSavePlan(files, state.Issues);
    }

    public string BuildReviewText(ConfigurationSavePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string managedChanges = string.Join("\n", plan.Files
            .Where(file => file.Category != "Operator settings")
            .Select(file => $"• {file.Category}: copy into the managed revision"));
        string operatorSettings = plan.Files.Any(file => file.Category == "Operator settings")
            ? "\n• Operator settings: update the app-owned settings store"
            : string.Empty;
        string compatibility = viewModel.Document.UnknownFields.Count > 0
            ? $"\n\n{viewModel.Document.UnknownFields.Count} unmatched YAML field(s) will be retained."
            : string.Empty;
        string migrations = viewModel.BuildIdentityMigrationReviewText();
        return $"Configuration Studio will commit:\n\n{managedChanges}{operatorSettings}{compatibility}{migrations}\n\n" +
               "The imported YAML and companion files are not modified. Edited sections are stored in canonical YAML; " +
               "comments and hand formatting inside those sections may change in the managed revision.";
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
