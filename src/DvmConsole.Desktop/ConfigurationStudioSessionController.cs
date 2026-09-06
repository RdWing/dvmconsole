// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text;
using DvmConsole.Application;
using DvmConsole.Configuration.Yaml;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

internal sealed record ConfigurationStudioSessionPorts(
    Func<string, string, string, Task<bool>> ConfirmAsync,
    Func<string, string, Task> ShowMessageAsync,
    Func<ConfigurationReference, ValueTask<IConfigurationMaterializationLease>> MaterializeAsync,
    Func<ConfigurationReference, Task<bool>> ReloadAsync);

internal sealed record ConfigurationStudioRuntimePorts(
    Func<Task> FlushSettingsAsync,
    Func<ConfigurationReference?> GetActiveConfiguration,
    Func<UserSettingsSnapshot, Task> AdoptSettingsAsync);

// Owns the Studio transaction boundary. The window supplies only user
// confirmation, status presentation, materialization, and live reload ports.
internal sealed class ConfigurationStudioSessionController
{
    private readonly ConfigurationStudioViewModel viewModel;
    private readonly ConfigurationStudioRuntimePorts runtime;
    private readonly UserSettingsStore settingsStore;
    private readonly ManagedConfigurationLibrary configurationLibrary;
    private readonly DesktopConfigurationStudioSavePlanner savePlanner;

    public ConfigurationStudioSessionController(
        ConfigurationStudioViewModel viewModel,
        ConfigurationStudioRuntimePorts runtime,
        UserSettingsStore settingsStore,
        ManagedConfigurationLibrary configurationLibrary,
        DesktopConfigurationStudioSavePlanner savePlanner,
        ConfigurationId? managedConfigurationId)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        this.configurationLibrary = configurationLibrary ?? throw new ArgumentNullException(nameof(configurationLibrary));
        this.savePlanner = savePlanner ?? throw new ArgumentNullException(nameof(savePlanner));
        ManagedConfigurationId = managedConfigurationId;
    }

    public ConfigurationId? ManagedConfigurationId { get; private set; }

    public ConfigurationSavePlan CreatePlan(string destinationPath)
        => savePlanner.CreatePlan(destinationPath);

    public string BuildReviewText(ConfigurationSavePlan plan)
        => savePlanner.BuildReviewText(plan);

    public async Task<bool> ReviewAndSaveAsync(
        bool saveCopy,
        bool offerReload,
        ConfigurationStudioSessionPorts ports)
    {
        ArgumentNullException.ThrowIfNull(ports);
        viewModel.CommitPendingEdits();
        try
        {
            await runtime.FlushSettingsAsync();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            await ports.ShowMessageAsync(
                "Unable to prepare save",
                $"Current operator settings could not be saved.\n\n{exception.Message}");
            return false;
        }

        string planPath = viewModel.Document.SourcePath ?? Path.Combine(
            Path.GetDirectoryName(settingsStore.Path) ?? AppContext.BaseDirectory,
            "ConfigurationDraftPreview",
            "codeplug.yml");
        ConfigurationSavePlan plan;
        try
        {
            plan = savePlanner.CreatePlan(planPath);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            await ports.ShowMessageAsync("Unable to prepare save", exception.Message);
            return false;
        }

        if (!plan.CanSave)
        {
            viewModel.OpenValidationDrawer();
            return false;
        }

        string action = saveCopy ? "Save a copy" : "Save";
        if (!await ports.ConfirmAsync("Review & Save", savePlanner.BuildReviewText(plan), action))
            return false;

        ConfigurationCommitResult? committed = null;
        ConfigurationReference? durableCommit = null;
        try
        {
            committed = await CommitAsync(
                plan,
                planPath,
                saveCopy,
                ports,
                reference => durableCommit = reference);
            await ports.ShowMessageAsync(
                "Configuration saved",
                saveCopy
                    ? "Saved a managed copy with a new configuration ID." + committed.SettingsWarning
                    : "Committed a new immutable managed revision." + committed.SettingsWarning);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or InvalidOperationException)
        {
            if (durableCommit is null)
            {
                await ports.ShowMessageAsync(
                    "Configuration save failed",
                    $"No managed revision was committed. Restricted backups remain available when originals were staged.\n\n{exception.Message}");
                return false;
            }

            ManagedConfigurationId = durableCommit.Id;
            await ports.ShowMessageAsync(
                "Configuration committed with a follow-up failure",
                $"Managed revision {durableCommit.Revision} was committed and remains recoverable in the Configuration Library, " +
                "but Studio could not finish materializing it or rebasing operator settings. Reopen the managed configuration before making more edits.\n\n" +
                exception.Message);
            return true;
        }

        bool updatesActiveConfiguration =
            runtime.GetActiveConfiguration()?.Id == committed.Reference.Id;
        if (offerReload &&
            !saveCopy &&
            await ports.ConfirmAsync(
                updatesActiveConfiguration
                    ? "Reload active configuration?"
                    : "Load saved configuration?",
                updatesActiveConfiguration
                    ? "The running FNE sessions still use the previous managed revision. Disconnect and reload now, or cancel to keep the new revision pending without changing the active session."
                    : "This configuration is saved, but the console is still displaying a different configuration. Disconnect that configuration and load this one now, or cancel to leave this saved configuration in the library.",
                updatesActiveConfiguration
                    ? "Disconnect and reload"
                    : "Disconnect and load"))
        {
            return await ports.ReloadAsync(committed.Reference);
        }

        return true;
    }

    private async Task<ConfigurationCommitResult> CommitAsync(
        ConfigurationSavePlan plan,
        string planPath,
        bool saveCopy,
        ConfigurationStudioSessionPorts ports,
        Action<ConfigurationReference> recordDurableCommit)
    {
        string yaml = plan.Files.First(file => file.Category == "Codeplug").Content;
        if (saveCopy)
            yaml = ConfigurationCopyPolicy.RemoveTrustScopedWebAuthorization(yaml);

        bool currentConfigurationIsCatalogued = ManagedConfigurationId is ConfigurationId currentId &&
            await ConfigurationExistsAsync(currentId);
        ConfigurationDraft draft;
        if ((saveCopy && currentConfigurationIsCatalogued) || ManagedConfigurationId is null)
        {
            string name = saveCopy ? "Configuration Copy" : "Untitled Configuration";
            draft = await configurationLibrary.CreateDraftAsync(name);
        }
        else
        {
            draft = await configurationLibrary.OpenDraftAsync(ManagedConfigurationId.Value);
        }

        var companions = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase);
        foreach (ConfigurationFileChange file in plan.Files.Where(file =>
                     file.Category is not "Codeplug" and not "Operator settings"))
        {
            companions[Path.GetFileName(file.Path)] = Encoding.UTF8.GetBytes(file.Content);
        }
        draft = await configurationLibrary.StageDraftAsync(
            draft with { Yaml = yaml, IsDirty = true },
            companions);
        ConfigurationCommit commit = await configurationLibrary.CommitAsync(draft);
        recordDurableCommit(commit.Reference);

        ConfigurationFileChange[] settingsChanges = plan.Files
            .Where(file => file.Category == "Operator settings")
            .ToArray();
        string backupRoot = Path.Combine(
            Path.GetDirectoryName(settingsStore.Path) ?? AppContext.BaseDirectory,
            "ConfigurationBackups");
        if (settingsChanges.Length > 0)
            _ = ConfigurationSaveTransaction.Execute(new ConfigurationSavePlan(settingsChanges, []), backupRoot);

        await using IConfigurationMaterializationLease materialization =
            await ports.MaterializeAsync(commit.Reference);
        string managedPath = materialization.Path;
        UserSettings committedSettings = settingsStore.Load();
        if (saveCopy && runtime.GetActiveConfiguration() is { } sourceConfiguration)
        {
            ConfigurationOperatorStateStore.Copy(
                committedSettings,
                sourceConfiguration.Id.ToString(),
                commit.Reference.Id.ToString(),
                includeWebStreamAuthorization: false);
        }
        CodeplugGroupState copiedGroupState =
            CodeplugGroupStateStore.CopyForSaveAs(committedSettings, planPath, managedPath);
        CodeplugStudioState copiedStudioState =
            CodeplugStudioStateStore.CopyForSaveAs(committedSettings, planPath, managedPath);
        ConfigurationOperatorStateStore.UpdateDocumentState(
            committedSettings,
            commit.Reference.Id.ToString(),
            copiedGroupState,
            copiedStudioState);
        settingsStore.Save(committedSettings);

        Exception? settingsPersistenceFailure = null;
        try
        {
            await runtime.AdoptSettingsAsync(
                settingsStore.CaptureSnapshot(committedSettings));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            settingsPersistenceFailure = exception;
        }

        viewModel.AcceptSaved(managedPath, commit.Reference.Id, plan);
        ManagedConfigurationId = commit.Reference.Id;
        return new ConfigurationCommitResult(
            commit.Reference,
            DescribeSettingsPersistenceWarning(settingsPersistenceFailure));
    }

    private async ValueTask<bool> ConfigurationExistsAsync(ConfigurationId id)
    {
        await foreach (ConfigurationSummary summary in configurationLibrary.ListAsync())
        {
            if (summary.Id == id)
                return true;
        }
        return false;
    }

    private static string DescribeSettingsPersistenceWarning(Exception? failure)
        => failure is null
            ? string.Empty
            : "\n\nThe managed revision was committed and the live settings were rebased, " +
              $"but the settings writer reported a failure and will retry after the next change.\n\n{failure.Message}";

    private sealed record ConfigurationCommitResult(
        ConfigurationReference Reference,
        string SettingsWarning);
}
