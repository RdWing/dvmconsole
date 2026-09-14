// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Threading;
using DvmConsole.Application;
using DvmConsole.Configuration.Yaml;
using DvmConsole.Core.Configuration;
using DvmConsole.Presentation;
using DvmConsole.Storage;

namespace DvmConsole.iOS;

/// <summary>Exercises the native host's actual Studio composition without touching operator data.</summary>
internal static class IosStudioDiagnostics
{
    public static Task<string> RunAsync()
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try { completion.TrySetResult(await RunCoreAsync()); }
            catch (Exception exception) { completion.TrySetException(exception); }
        });
        return completion.Task;
    }

    private static async Task<string> RunCoreAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), $"neo-studio-{Guid.NewGuid():N}");
        try
        {
            await QualifyNewConfigurationAsync(Path.Combine(root, "NewConfiguration"));
            await QualifyRecoveredCopyAsync(Path.Combine(root, "RecoveredCopy"));
            await QualifyStaleEditorAsync(Path.Combine(root, "StaleEditor"));
            var library = new ManagedConfigurationLibrary(Path.Combine(root, "Library"));
            var draft = await library.CreateDraftAsync("Studio qualification");
            draft = await library.StageDraftAsync(draft with { IsDirty = true, Yaml = """
                keyFile: ./keys.clear
                systems:
                  - name: Simulator
                    identity: Console
                    address: 127.0.0.1
                    port: 62031
                    peerId: 1
                    rid: "1001"
                    aliasPath: ./aliases.yml
                zones:
                  - name: Qualification
                    channels:
                      - name: Receive P25
                        system: Simulator
                        tgid: 100
                        mode: p25
                groups: []
                """ }, new Dictionary<string, ReadOnlyMemory<byte>>
                {
                    ["keys.clear"] = "keys: []\n"u8.ToArray(),
                    ["aliases.yml"] = "- rid: 42\n  alias: Qualification\n"u8.ToArray()
                });
            var initial = await library.CommitAsync(draft);
            await library.ActivateAsync(initial.Reference);
            ConfigurationDraft recoverable = await library.OpenDraftAsync(initial.Reference.Id);
            ConfigurationDraft staged = await library.StageDraftAsync(recoverable with
            {
                Yaml = recoverable.Yaml.Replace("identity: Console", "identity: Recovered"), IsDirty = true
            }, new Dictionary<string, ReadOnlyMemory<byte>>());
            string pending = Path.Combine(root, "Library", "drafts", "active.json.pending");
            Directory.CreateDirectory(pending);
            bool checkpointFailed = false;
            try
            {
                await library.StageDraftAsync(staged with { Yaml = staged.Yaml.Replace("identity: Recovered", "identity: Uncommitted") },
                    new Dictionary<string, ReadOnlyMemory<byte>> { ["aliases.yml"] = "uncommitted companion"u8.ToArray() });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { checkpointFailed = true; }
            finally { Directory.Delete(pending); }
            Require(checkpointFailed, "The checkpoint failure fixture did not fail the metadata write.");
            library = new ManagedConfigurationLibrary(Path.Combine(root, "Library"));
            var recoveryMaterializer = new ManagedConfigurationMaterializer(library, Path.Combine(root, "RecoveryCheck"));
            var recovered = await recoveryMaterializer.MaterializeDraftAsync(initial.Reference.Id);
            await using (var recoveredLease = recovered.Lease)
            {
                Require(recovered.Draft.Yaml == staged.Yaml, "Failed checkpoint changed recoverable YAML.");
                Require(File.ReadAllText(Path.Combine(Path.GetDirectoryName(recoveredLease.Path)!, "aliases.yml")) ==
                    "- rid: 42\n  alias: Qualification\n", "Failed checkpoint changed recoverable aliases.");
            }
            await using var session = await IosConfigurationStudio.OpenAtRootAsync(
                library, initial.Reference, root, CancellationToken.None);
            var viewModel = session.ViewModel;
            Require(viewModel.IsDirty && viewModel.Configuration.Systems[0].Identity == "Recovered",
                "The persisted draft was not recovered as an unsaved edit.");
            Require(session.InitialStatus == "Recovered unsaved draft.", "Recovery notice was missing.");
            viewModel.Configuration.Systems[0].Identity = "Edited on iOS";
            viewModel.CommitFieldEdit();
            viewModel.Undo();
            Require(viewModel.Configuration.Systems[0].Identity == "Recovered", "Undo did not restore the recovered draft.");
            viewModel.Redo();
            Require(viewModel.Configuration.Systems[0].Identity == "Edited on iOS", "Redo did not restore the edit.");
            viewModel.SelectedSystem = viewModel.Configuration.Systems[0];
            viewModel.SelectedSystemHasCallPriority = true;
            await session.CheckpointAsync();
            ConfigurationDraft checkpointed = await library.OpenDraftAsync(initial.Reference.Id);
            Require(checkpointed.IsDirty && checkpointed.BasedOnRevision == initial.Reference.Revision &&
                checkpointed.Yaml.Contains("Edited on iOS", StringComparison.Ordinal),
                "Background checkpoint did not preserve the editor without committing a revision.");
            Require(checkpointed.EditorState?.CallPrioritySystemNames.Contains("Simulator") == true,
                "Checkpoint lost unsaved Studio priority state.");
            Require(viewModel.IsDirty && library.Active == initial.Reference,
                "Checkpoint changed dirty state or the running revision.");
            var checkpointMaterializer = new ManagedConfigurationMaterializer(library, Path.Combine(root, "CheckpointCheck"));
            var checkpointCopy = await checkpointMaterializer.MaterializeDraftAsync(initial.Reference.Id);
            await using (var checkpointLease = checkpointCopy.Lease)
            {
                var checkpointAliases = AliasFileLoader.Load(Path.Combine(Path.GetDirectoryName(checkpointLease.Path)!, "aliases.yml"));
                Require(checkpointAliases.Count == 1 && checkpointAliases[0].Rid == 42 && checkpointAliases[0].Alias == "Qualification",
                    "Editor checkpoint changed the alias mapping.");
            }
            await using (var recoverySession = await IosConfigurationStudio.OpenAtRootAsync(
                library, initial.Reference, root, CancellationToken.None))
            {
                recoverySession.ViewModel.SelectedSystem = recoverySession.ViewModel.Configuration.Systems[0];
                Require(recoverySession.ViewModel.SelectedSystemHasCallPriority && recoverySession.ViewModel.IsDirty,
                    "Native editor reconstruction lost draft priority state.");
            }
            // Closing the recovery probe intentionally discards its draft; restore
            // the original editor's checkpoint before exercising its reviewed save.
            await session.CheckpointAsync();
            var controller = new ConfigurationStudioSaveController(viewModel, initial.Reference.Id);
            var interaction = new ConfigurationStudioSaveInteraction(
                (_, _, _) => Task.FromResult(true), (_, _) => Task.CompletedTask,
                _ => throw new InvalidOperationException("Studio qualification must not load a radio session."));
            Require(await controller.ReviewAndSaveAsync(false, false, session.SaveServices, interaction), "Save failed.");
            Require(!viewModel.IsDirty, "Saved draft remained dirty.");
            Require(library.Active == initial.Reference, "Saving changed the running revision.");
            ConfigurationDraft saved = await library.OpenDraftAsync(initial.Reference.Id);
            Require(saved.BasedOnRevision != initial.Reference.Revision && saved.Yaml.Contains("Edited on iOS", StringComparison.Ordinal),
                "Saved revision did not contain the edit.");
            await using (var archive = new ConfigurationExportArchive(Path.Combine(root, "Exports")))
            {
                Require((await session.ExportAsync(archive, false)).Count == 0, "Draft export omitted content.");
                IReadableDocument document = await archive.CreateArchiveAsync();
                await using Stream input = await document.OpenReadAsync();
                Require(await input.ReadAsync(new byte[4]) == 4, "Draft export was empty.");
            }
            Require(await controller.ReviewAndSaveAsync(true, false, session.SaveServices, interaction), "Save-copy failed.");
            Require(controller.ManagedConfigurationId != initial.Reference.Id, "Save-copy reused the source ID.");
            Require(library.Active == initial.Reference, "Save-copy changed the active revision.");
            await using var reopened = await IosConfigurationStudio.OpenAtRootAsync(library,
                new(initial.Reference.Id, saved.BasedOnRevision!.Value), root, CancellationToken.None);
            Require(reopened.ViewModel.Configuration.Systems[0].Identity == "Edited on iOS", "Reopen lost the saved edit.");
            ConfigurationStudioSaveState copyContents = ((IConfigurationStudioSaveSource)reopened.ViewModel).CaptureSaveState();
            var copyController = new ConfigurationStudioSaveController(reopened.ViewModel, initial.Reference.Id);
            Require(await copyController.ReviewAndSaveAsync(true, false, reopened.SaveServices, interaction),
                "Copying an unchanged reopened configuration failed.");
            ConfigurationDraft copied = await library.OpenDraftAsync(copyController.ManagedConfigurationId!.Value);
            var copyMaterializer = new ManagedConfigurationMaterializer(library, Path.Combine(root, "CopyCheck"));
            await using (var copyLease = await copyMaterializer.MaterializeAsync(new(copied.Id, copied.BasedOnRevision!.Value)))
            {
                string directory = Path.GetDirectoryName(copyLease.Path)!;
                Require(File.ReadAllText(Path.Combine(directory, "keys.clear")) == copyContents.KeyFileContent, "Save-copy lost unchanged keys.");
                Require(File.ReadAllText(Path.Combine(directory, "aliases.yml")) == copyContents.AliasContents.Single().Value,
                    "Save-copy lost unchanged aliases.");
            }
            await IosStudioScreenshot.CaptureAsync(reopened, copyController.ManagedConfigurationId.Value, root);
            return "PASS\nNative Studio host, new configuration with manual keys/aliases and on-demand companion import, editor checkpoint and companion recovery, stale-editor revision protection, failed-checkpoint atomic recovery, persisted draft recovery/copy, shared draft editor, undo/redo, managed save, saved-revision isolation, draft bundle export, companion-preserving save-copy, reopen and lease disposal. No radio session or operator data.";
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static async Task QualifyNewConfigurationAsync(string root)
    {
        var library = new ManagedConfigurationLibrary(Path.Combine(root, "Library"));
        ConfigurationDraft draft = await library.CreateDraftAsync("New mobile configuration");
        await using var session = await IosConfigurationStudio.OpenDraftAtRootAsync(
            library, draft, null, root, CancellationToken.None);
        var editor = session.ViewModel;
        Require(editor.Systems.Count == 0 && editor.IsDirty, "New Studio did not open an empty editable draft.");
        await session.CheckpointAsync();
        Require((await library.OpenDraftAsync(draft.Id)).BasedOnRevision is null,
            "Blank draft checkpoint created a revision.");
        editor.AddSystem();
        var system = editor.Systems.Single();
        system.PeerId = 1;
        system.Rid = "1001";
        editor.CommitFieldEdit();
        editor.AddChannelToSelectedSystem();
        editor.AddKey();
        editor.KeyEntries.Single().Key = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
        editor.CommitKeyEdit();
        string completeKey = editor.KeyEntries.Single().Key;
        editor.KeyEntries.Single().Key = "0011";
        editor.CommitKeyEdit();
        await session.CheckpointAsync();
        var checkpointReader = new ManagedConfigurationMaterializer(library, Path.Combine(root, "IncompleteReadback"));
        var incomplete = await checkpointReader.MaterializeDraftAsync(draft.Id);
        await using (var lease = incomplete.Lease)
        {
            Require(KeyFileLoader.Load(Path.Combine(Path.GetDirectoryName(lease.Path)!, "keys.clear")).Keys.Single().Key == "0011",
                "An incomplete key was not retained in the draft checkpoint.");
        }
        editor.KeyEntries.Single().Key = completeKey;
        editor.CommitKeyEdit();
        editor.SelectedAliasSystem = system;
        editor.AddAlias();
        editor.Aliases.Single().Rid = 4242;
        editor.Aliases.Single().Name = "New mobile alias";
        editor.CommitAliasEdit();
        await session.CheckpointAsync();
        ConfigurationDraft checkpoint = await library.OpenDraftAsync(draft.Id);
        Require(checkpoint.BasedOnRevision is null && checkpoint.IsDirty,
            "Checkpoint committed the new draft before save review.");
        Require(library.Active is null, "Creating a configuration activated radio services.");

        var saves = new ConfigurationStudioSaveController(editor, draft.Id);
        var messages = new List<string>();
        var interaction = new ConfigurationStudioSaveInteraction((_, _, _) => Task.FromResult(true),
            (title, text) => { messages.Add(title + ": " + text); return Task.CompletedTask; },
            _ => throw new InvalidOperationException("New configuration qualification must not start radio services."));
        Require(await saves.ReviewAndSaveAsync(false, false, session.SaveServices, interaction),
            "New configuration save failed: " + string.Join("; ", messages));
        ConfigurationDraft saved = await library.OpenDraftAsync(draft.Id);
        Require(saved.BasedOnRevision is not null && !saved.IsDirty && library.Active is null,
            "New configuration did not become an inactive saved revision.");
        var materializer = new ManagedConfigurationMaterializer(library, Path.Combine(root, "Readback"));
        await using (var lease = await materializer.MaterializeAsync(new(draft.Id, saved.BasedOnRevision!.Value)))
        {
            var document = ConfigurationDocument.Open(lease.Path);
            string directory = Path.GetDirectoryName(lease.Path)!;
            Require(File.Exists(Path.Combine(directory, "keys.clear")), "Manual key entry did not create keys.clear.");
            var aliases = AliasFileLoader.Load(Path.Combine(directory, "aliases.yml"));
            Require(aliases.Count == 1 && aliases[0].Rid == 4242 && aliases[0].Alias == "New mobile alias",
                "Manual alias entry did not create its bundled alias file.");
            Require(document.Configuration.Systems.Count == 1, "New system was not saved.");
        }
        await using (var manualEntries = await IosConfigurationStudio.OpenAtRootAsync(
            library, new(draft.Id, saved.BasedOnRevision!.Value), root, CancellationToken.None))
        {
            Require(manualEntries.ViewModel.KeyEntries.Single().Key == completeKey,
                "Reopening the new configuration changed its manually entered key.");
            Require(manualEntries.ViewModel.Aliases.Single() is { Rid: 4242, Name: "New mobile alias" },
                "Reopening the new configuration lost its manually entered alias.");
        }
        // Import actions may also add or replace files after the initial save.
        string keyContent = editor.CaptureExportCompanionContents().Single(pair => pair.Key.EndsWith("keys.clear", StringComparison.Ordinal)).Value;
        editor.AttachKeyFile("imported-keys.clear", keyContent);
        editor.AttachAliasFile(editor.Systems.Single(), "imported-aliases.yml", "- rid: 4343\n  alias: Imported mobile alias\n");
        Require(await saves.ReviewAndSaveAsync(false, false, session.SaveServices, interaction),
            "On-demand companion save failed: " + string.Join("; ", messages));
        saved = await library.OpenDraftAsync(draft.Id);
        await using var reopened = await IosConfigurationStudio.OpenAtRootAsync(
            library, new(draft.Id, saved.BasedOnRevision!.Value), root, CancellationToken.None);
        Require(reopened.ViewModel.KeyEntries.Count == 1 && reopened.ViewModel.Aliases.Single().Rid == 4343,
            "Reopened configuration lost manually entered keys or imported aliases.");
    }

    private static async Task QualifyStaleEditorAsync(string root)
    {
        var library = new ManagedConfigurationLibrary(Path.Combine(root, "Library"));
        var draft = await library.CreateDraftAsync("Revision qualification");
        var original = await library.CommitAsync(draft with { Yaml = """
            systems:
              - name: Simulator
                identity: Original
                address: 127.0.0.1
                port: 62031
                peerId: 1
                rid: "1001"
            zones: []
            groups: []
            """ });
        await using var session = await IosConfigurationStudio.OpenAtRootAsync(
            library, original.Reference, root, CancellationToken.None);
        session.ViewModel.Configuration.Systems[0].Identity = "Stale editor";
        session.ViewModel.CommitFieldEdit();
        var competing = await library.OpenDraftAsync(original.Reference.Id);
        var replacement = await library.CommitAsync(competing with { Yaml = competing.Yaml.Replace("Original", "Newer revision") });
        string notice = string.Empty;
        var controller = new ConfigurationStudioSaveController(session.ViewModel, original.Reference.Id);
        bool saved = await controller.ReviewAndSaveAsync(false, false, session.SaveServices,
            new((_, _, _) => Task.FromResult(true), (_, message) => { notice = message; return Task.CompletedTask; },
                _ => throw new InvalidOperationException("Revision qualification must not load a radio session.")));
        Require(!saved && session.ViewModel.IsDirty && notice.Contains("newer saved revision", StringComparison.Ordinal),
            "Stale editor was not preserved with a revision conflict notice.");
        var current = await library.OpenDraftAsync(original.Reference.Id);
        Require(current.BasedOnRevision == replacement.Reference.Revision && current.Yaml.Contains("Newer revision", StringComparison.Ordinal),
            "Stale editor replaced the current revision.");
    }

    private static async Task QualifyRecoveredCopyAsync(string root)
    {
        var library = new ManagedConfigurationLibrary(Path.Combine(root, "Library"));
        ConfigurationDraft draft = await library.CreateDraftAsync("Original");
        ConfigurationCommit original = await library.CommitAsync(draft with { Yaml = """
            systems:
              - name: Simulator
                identity: Original
                address: 127.0.0.1
                port: 62031
                peerId: 1
                rid: "1001"
            zones: []
            groups: []
            """ });
        await library.ActivateAsync(original.Reference);
        draft = await library.OpenDraftAsync(original.Reference.Id);
        await library.StageDraftAsync(draft with { Yaml = draft.Yaml.Replace("Original", "RecoveredCopy"), IsDirty = true },
            new Dictionary<string, ReadOnlyMemory<byte>>());
        string replacementPath = Path.Combine(root, "replacement.yaml");
        await File.WriteAllTextAsync(replacementPath, draft.Yaml.Replace("Original", "Newer revision"));
        ConfigurationImportResult replacement = await library.ImportAsync(new FileConfigurationDocumentSet(replacementPath),
            new(ConfigurationConflictResolution.ReplaceExisting, original.Reference.Id));
        await using var session = await IosConfigurationStudio.OpenAtRootAsync(library, replacement.Reference, root, CancellationToken.None);
        Require(session.InitialStatus.Contains("older revision", StringComparison.Ordinal), "Older draft recovery notice was missing.");
        Require(session.ViewModel.IsDirty && session.ViewModel.Configuration.Systems[0].Identity == "RecoveredCopy",
            "Opening the newer library entry lost the recovered edit.");
        await session.CheckpointAsync();
        var controller = new ConfigurationStudioSaveController(session.ViewModel, original.Reference.Id);
        string conflictNotice = string.Empty;
        Require(!await controller.ReviewAndSaveAsync(false, false, session.SaveServices,
            new((_, _, _) => Task.FromResult(true), (_, message) => { conflictNotice = message; return Task.CompletedTask; },
                _ => Task.FromResult(false))), "Recovered draft overwrote the newer revision.");
        Require(session.ViewModel.IsDirty && conflictNotice.Contains("newer saved revision", StringComparison.Ordinal),
            "Recovered draft conflict did not retain the edit and explain recovery.");
        Require(await controller.ReviewAndSaveAsync(true, false, session.SaveServices,
            new((_, _, _) => Task.FromResult(true), (_, _) => Task.CompletedTask, _ => Task.FromResult(false))),
            "A persisted recovered draft could not be saved as a copy.");
        Require(controller.ManagedConfigurationId != original.Reference.Id, "Recovered copy reused the original ID.");
        Require(library.Active == original.Reference, "Recovered copy changed the running revision.");
        ConfigurationDraft source = await library.OpenDraftAsync(original.Reference.Id);
        Require(!source.IsDirty && source.BasedOnRevision == replacement.Reference.Revision &&
            source.Yaml.Contains("identity: Newer revision", StringComparison.Ordinal),
            "Recovered copy modified the newer revision.");
        ConfigurationDraft copy = await library.OpenDraftAsync(controller.ManagedConfigurationId!.Value);
        Require(copy.Yaml.Contains("RecoveredCopy", StringComparison.Ordinal), "Recovered copy lost the unsaved edit.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
