// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using DvmConsole.Application;

namespace DvmConsole.Storage;

/// <summary>
/// Combines shared editor state and app-owned operator settings into a managed save plan.
/// Managed configuration commits consume the resulting content and do not modify imported files.
/// </summary>
public sealed class ConfigurationStudioSavePlanner(
    IConfigurationStudioSaveSource viewModel,
    UserSettingsStore settingsStore)
{
    private readonly IConfigurationStudioSaveSource viewModel =
        viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    private readonly UserSettingsStore settingsStore =
        settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));

    public ConfigurationSavePlan CreatePlan(string destinationPath, bool saveCopy = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        string fullDestination = Path.GetFullPath(destinationPath);
        ConfigurationStudioSaveState state = viewModel.CaptureSaveState();
        ConfigurationDocument document = viewModel.Document;
        ConsoleConfiguration configuration = viewModel.Configuration;
        var files = ConfigurationStudioFilePlanner.CreateFiles(document, configuration, state, fullDestination,
            includeUnchangedCompanions: saveCopy).ToList();

        UserSettings settings = settingsStore.Load();
        bool identityChanged = !FileSystemPathIdentity.AreEquivalent(
            viewModel.DocumentIdentity,
            fullDestination);
        viewModel.ApplyOperatorStateForSave(settings, fullDestination, identityChanged);
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

}
