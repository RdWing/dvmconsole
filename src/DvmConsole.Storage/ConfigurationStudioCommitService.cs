// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text;
using DvmConsole.Core.Settings;

namespace DvmConsole.Storage;

public sealed record ConfigurationStudioCommitPorts(
    Func<ConfigurationReference?> GetActiveConfiguration,
    Func<ConfigurationReference, ValueTask<IConfigurationMaterializationLease>> MaterializeAsync,
    Func<UserSettingsSnapshot, Task> AdoptSettingsAsync,
    Action<string, ConfigurationReference, ConfigurationSavePlan> AcceptSaved,
    Action<ConfigurationReference> RecordDurableCommit,
    ConfigurationReference? ExpectedConfiguration = null);

/// <summary>Shared managed-revision commit, operator-state migration and post-commit recovery boundary.</summary>
public sealed class ConfigurationStudioCommitService(UserSettingsStore settingsStore, IConfigurationLibrary configurationLibrary)
{
    public async Task<ConfigurationStudioCommitResult> CommitAsync(ConfigurationSavePlan plan,
        string planPath, bool saveCopy, ConfigurationId? managedConfigurationId, ConfigurationStudioCommitPorts ports)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(ports);
        string yaml = plan.Files.First(file => file.Category == "Codeplug").Content;
        if (saveCopy)
            yaml = ConfigurationCopyPolicy.RemoveTrustScopedWebAuthorization(yaml);

        bool currentConfigurationIsCatalogued = managedConfigurationId is ConfigurationId currentId &&
            await ConfigurationExistsAsync(currentId);
        var companions = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase);
        foreach (ConfigurationFileChange file in plan.Files.Where(file =>
                     file.Category is not "Codeplug" and not "Operator settings"))
        {
            companions[Path.GetFileName(file.Path)] = Encoding.UTF8.GetBytes(file.Content);
        }
        ConfigurationCommit commit;
        if (saveCopy && currentConfigurationIsCatalogued && configurationLibrary is IConfigurationDraftCopyService copies)
        {
            ConfigurationDraft source = await configurationLibrary.OpenDraftAsync(managedConfigurationId!.Value);
            commit = await copies.CommitDraftCopyAsync(source with { Yaml = yaml, IsDirty = true },
                companions, "Configuration Copy");
        }
        else
        {
            ConfigurationDraft draft;
            if ((saveCopy && currentConfigurationIsCatalogued) || managedConfigurationId is null)
            {
                string name = saveCopy ? "Configuration Copy" : "Untitled Configuration";
                draft = await configurationLibrary.CreateDraftAsync(name);
            }
            else
                draft = await configurationLibrary.OpenDraftAsync(managedConfigurationId.Value);
            if (!saveCopy && ports.ExpectedConfiguration is { } expected &&
                (draft.Id != expected.Id || draft.BasedOnRevision != expected.Revision))
                throw new ConfigurationRevisionConflictException(
                    new(draft.Id, draft.BasedOnRevision ?? expected.Revision));
            draft = await configurationLibrary.StageDraftAsync(draft with { Yaml = yaml, IsDirty = true }, companions);
            commit = await configurationLibrary.CommitAsync(draft);
        }
        ports.RecordDurableCommit(commit.Reference);

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
        if (saveCopy && ports.GetActiveConfiguration() is { } sourceConfiguration)
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
            await ports.AdoptSettingsAsync(
                settingsStore.CaptureSnapshot(committedSettings));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            settingsPersistenceFailure = exception;
        }

        ports.AcceptSaved(managedPath, commit.Reference, plan);
        return new ConfigurationStudioCommitResult(
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

}
