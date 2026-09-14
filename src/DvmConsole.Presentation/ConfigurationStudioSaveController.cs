// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Configuration;

namespace DvmConsole.Presentation;

public sealed record ConfigurationStudioSaveServices(
    Func<Task> FlushSettingsAsync,
    Func<ConfigurationReference?> GetActiveConfiguration,
    Func<bool, ConfigurationSavePlan> CreatePlan,
    Func<ConfigurationSavePlan, string> BuildReviewText,
    Func<ConfigurationSavePlan, bool, Action<ConfigurationReference>, Task<ConfigurationStudioCommitResult>> CommitAsync);

public sealed record ConfigurationStudioSaveInteraction(
    Func<string, string, string, Task<bool>> ConfirmAsync,
    Func<string, string, Task> ShowMessageAsync,
    Func<ConfigurationReference, Task<bool>> ReloadAsync);

/// <summary>Shared save review and recovery flow; hosts supply persistence and navigation.</summary>
public sealed class ConfigurationStudioSaveController(
    ConfigurationStudioViewModel viewModel,
    ConfigurationId? managedConfigurationId)
{
    private readonly ConfigurationStudioViewModel viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    public ConfigurationId? ManagedConfigurationId { get; private set; } = managedConfigurationId;

    public async Task<bool> ReviewAndSaveAsync(
        bool saveCopy,
        bool offerReload,
        ConfigurationStudioSaveServices services,
        ConfigurationStudioSaveInteraction ports)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(ports);
        viewModel.CommitPendingEdits();
        try
        {
            await services.FlushSettingsAsync();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            await ports.ShowMessageAsync(
                "Unable to prepare save",
                $"Current operator settings could not be saved.\n\n{exception.Message}");
            return false;
        }

        ConfigurationSavePlan plan;
        try
        {
            plan = services.CreatePlan(saveCopy);
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
        if (!await ports.ConfirmAsync("Review & Save", services.BuildReviewText(plan), action))
            return false;

        ConfigurationStudioCommitResult? committed = null;
        ConfigurationReference? durableCommit = null;
        try
        {
            committed = await services.CommitAsync(
                plan,
                saveCopy,
                reference => durableCommit = reference);
            ManagedConfigurationId = committed.Reference.Id;
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
            services.GetActiveConfiguration()?.Id == committed.Reference.Id;
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

}
