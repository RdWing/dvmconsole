// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

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
    private readonly ConfigurationStudioSavePlanner savePlanner;
    private readonly ConfigurationStudioSaveController saveController;

    public ConfigurationStudioSessionController(
        ConfigurationStudioViewModel viewModel,
        ConfigurationStudioRuntimePorts runtime,
        UserSettingsStore settingsStore,
        ManagedConfigurationLibrary configurationLibrary,
        ConfigurationStudioSavePlanner savePlanner,
        ConfigurationId? managedConfigurationId)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        this.configurationLibrary = configurationLibrary ?? throw new ArgumentNullException(nameof(configurationLibrary));
        this.savePlanner = savePlanner ?? throw new ArgumentNullException(nameof(savePlanner));
        saveController = new ConfigurationStudioSaveController(viewModel, managedConfigurationId);
    }

    public ConfigurationId? ManagedConfigurationId => saveController.ManagedConfigurationId;

    public ConfigurationSavePlan CreatePlan(string destinationPath)
        => savePlanner.CreatePlan(destinationPath);

    public string BuildReviewText(ConfigurationSavePlan plan)
        => savePlanner.BuildReviewText(plan);

    public Task<bool> ReviewAndSaveAsync(
        bool saveCopy,
        bool offerReload,
        ConfigurationStudioSessionPorts ports)
    {
        ArgumentNullException.ThrowIfNull(ports);
        string? planPath = null;
        return saveController.ReviewAndSaveAsync(saveCopy, offerReload,
            new ConfigurationStudioSaveServices(
                runtime.FlushSettingsAsync,
                runtime.GetActiveConfiguration,
                copy =>
                {
                    planPath = viewModel.Document.SourcePath ?? Path.Combine(
                        Path.GetDirectoryName(settingsStore.Path) ?? AppContext.BaseDirectory,
                        "ConfigurationDraftPreview", "codeplug.yml");
                    return savePlanner.CreatePlan(planPath, copy);
                },
                savePlanner.BuildReviewText,
                (plan, copy, committed) => CommitAsync(plan, planPath!, copy, ports, committed)),
            new(ports.ConfirmAsync, ports.ShowMessageAsync, ports.ReloadAsync));
    }

    private Task<ConfigurationStudioCommitResult> CommitAsync(
        ConfigurationSavePlan plan,
        string planPath,
        bool saveCopy,
        ConfigurationStudioSessionPorts ports,
        Action<ConfigurationReference> recordDurableCommit)
        => new ConfigurationStudioCommitService(settingsStore, configurationLibrary).CommitAsync(
            plan, planPath, saveCopy, ManagedConfigurationId,
            new(runtime.GetActiveConfiguration, ports.MaterializeAsync, runtime.AdoptSettingsAsync,
                (managedPath, reference, savedPlan) =>
                {
                    viewModel.AcceptSaved(managedPath, reference.Id, savedPlan);
                }, recordDurableCommit));
}
