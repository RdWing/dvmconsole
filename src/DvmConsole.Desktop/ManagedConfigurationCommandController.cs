// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Configuration.Yaml;

namespace DvmConsole.Desktop;

internal sealed class ManagedConfigurationCommandController(IConfigurationLibrary library)
{
    private readonly IConfigurationLibrary library =
        library ?? throw new ArgumentNullException(nameof(library));

    public async ValueTask<ConfigurationImportResult> ImportAsync(
        IImportDocumentSet source,
        bool usesDocumentPicker,
        Func<string, string, string, Task<bool>> confirmAsync,
        Func<IReadOnlyList<string>, Task<bool>> selectExternalCompanionsAsync)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(confirmAsync);
        ArgumentNullException.ThrowIfNull(selectExternalCompanionsAsync);

        var options = new ConfigurationImportOptions();
        while (true)
        {
            try
            {
                return await library.ImportAsync(source, options);
            }
            catch (ConfigurationExternalCompanionsConfirmationRequiredException confirmation)
            {
                if (options.ConfirmExternalCompanions)
                    throw;
                string references = string.Join(
                    Environment.NewLine,
                    confirmation.References.Select(reference => $"• {reference}"));
                bool approved = await confirmAsync(
                    "Import external companion files?",
                    "This configuration refers to key or alias files outside its folder. " +
                    "DVM Console will copy the selected files into the managed revision; the originals will remain unchanged.\n\n" +
                    references,
                    usesDocumentPicker ? "Select files" : "Import companions");
                if (!approved ||
                    usesDocumentPicker &&
                    !await selectExternalCompanionsAsync(confirmation.References))
                {
                    throw new OperationCanceledException();
                }
                options = options with { ConfirmExternalCompanions = true };
            }
            catch (ConfigurationImportConflictException conflict)
            {
                if (await confirmAsync(
                        "Configuration changed in two places",
                        "Both the imported YAML bundle and its managed configuration changed since the last import. Replace the managed entry with a recoverable new revision?",
                        "Replace existing"))
                {
                    options = options with
                    {
                        ConflictResolution = ConfigurationConflictResolution.ReplaceExisting,
                        ReplaceConfigurationId = conflict.ExistingConfigurationId
                    };
                    continue;
                }
                if (await confirmAsync(
                        "Import as a new configuration?",
                        "Keep the existing managed configuration and import this YAML bundle under a new configuration ID?",
                        "Import as new"))
                {
                    options = options with
                    {
                        ConflictResolution = ConfigurationConflictResolution.ImportAsNew,
                        ReplaceConfigurationId = null
                    };
                    continue;
                }
                throw new OperationCanceledException();
            }
        }
    }

    public async ValueTask<ConfigurationDraft> CreateDraftAsync(
        Func<string, string, string, Task<bool>> confirmAsync)
    {
        ArgumentNullException.ThrowIfNull(confirmAsync);
        try
        {
            return await library.CreateDraftAsync("Untitled Configuration");
        }
        catch (ConfigurationDraftConflictException conflict)
        {
            bool discard = await confirmAsync(
                "Unfinished configuration draft",
                "Configuration Studio found an unfinished managed draft from an earlier session. Discard it and start a new configuration?",
                "Discard and start new");
            if (!discard)
                throw new OperationCanceledException();
            await library.DiscardDraftAsync(conflict.ExistingDraft.Id);
            return await library.CreateDraftAsync("Untitled Configuration");
        }
    }
}
