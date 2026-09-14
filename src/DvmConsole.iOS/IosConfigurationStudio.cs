// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text;
using DvmConsole.Application;
using DvmConsole.Configuration.Yaml;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using DvmConsole.Mobile;
using DvmConsole.Presentation;
using DvmConsole.Storage;

namespace DvmConsole.iOS;

/// <summary>App-owned documents and persistence ports for the shared touch Studio.</summary>
internal static class IosConfigurationStudio
{
    public static ValueTask<MobileStudioSession> OpenAsync(IConfigurationLibrary library,
        ConfigurationReference reference, CancellationToken token)
        => OpenAtRootAsync(library, reference, IosConfigurationStorage.OpenRoot(), token);

    internal static async ValueTask<MobileStudioSession> OpenAtRootAsync(IConfigurationLibrary library,
        ConfigurationReference reference, string root, CancellationToken token)
    {
        ConfigurationDraft draft = await library.OpenDraftAsync(reference.Id, token);
        return await OpenDraftAtRootAsync(library, draft, reference, root, token);
    }

    public static ValueTask<MobileStudioSession> OpenDraftAsync(IConfigurationLibrary library,
        ConfigurationDraft draft, CancellationToken token)
        => OpenDraftAtRootAsync(library, draft, null, IosConfigurationStorage.OpenRoot(), token);

    internal static async ValueTask<MobileStudioSession> OpenDraftAtRootAsync(IConfigurationLibrary library,
        ConfigurationDraft draft, ConfigurationReference? reference, string root, CancellationToken token)
    {
        var materializer = new ManagedConfigurationMaterializer(library, Path.Combine(root, "Studio"));
        IConfigurationMaterializationLease lease;
        if (draft.IsDirty)
            (draft, lease) = await materializer.MaterializeDraftAsync(draft.Id, token);
        else
            lease = await materializer.MaterializeAsync(reference ?? throw new InvalidOperationException("The draft has no saved revision."), token);
        try
        {
            bool recoveredOlderRevision = draft.IsDirty && reference is not null && draft.BasedOnRevision != reference.Revision;
            if (!draft.IsDirty && reference is not null && draft.BasedOnRevision != reference.Revision)
                throw new InvalidOperationException("This configuration has a newer revision. Refresh the library before editing.");
            var store = new UserSettingsStore(Path.Combine(root, "UserSettings.json"));
            UserSettings settings = store.Load();
            if (store.LastReadState == SettingsReadState.Unreadable)
                throw new IOException(store.LastLoadDiagnostics.Warning ?? "Settings are unavailable.");
            CodeplugStudioState initial = settings.ConfigurationOperatorStates.TryGetValue(draft.Id.ToString(), out var saved)
                ? saved.StudioState : new();
            ConfigurationStudioInitialState editorInitial = draft.IsDirty && draft.EditorState is { } recoveredEditor
                ? new(recoveredEditor.ChannelPositions.ToDictionary(pair => pair.Key,
                        pair => new ConfigurationStudioPosition(pair.Value.X, pair.Value.Y), StringComparer.OrdinalIgnoreCase),
                    recoveredEditor.ZoneSystemAssignments, recoveredEditor.CallPrioritySystemNames)
                : new(settings.ChannelWidgetPositions.ToDictionary(pair => pair.Key,
                        pair => new ConfigurationStudioPosition(pair.Value.X, pair.Value.Y), StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, string>(initial.ZoneSystemAssignments, StringComparer.OrdinalIgnoreCase),
                    initial.CallPrioritySystemNames.ToArray());
            ConfigurationDocument document = ConfigurationDocument.Open(lease.Path);
            if (draft.IsDirty) document.MarkDirty();
            var viewModel = new ConfigurationStudioViewModel(
                document, draft.Id, lease.Path,
                new MobileStudioRuntimeContext(), new MaterializedConfigurationStudioCompanionSource(),
                new ConfigurationSnapshotPreviewFactory(),
                editorInitial, ConfigurationStudioSection.Overview);
            var planner = new ConfigurationStudioSavePlanner(viewModel, store);
            var commit = new ConfigurationStudioCommitService(store, library);
            var operations = new ConfigurationStudioOperationOwner();
            IosStudioBackgroundObserver? background = null;
            ConfigurationId managedId = draft.Id;
            ConfigurationReference? editorReference = draft.IsDirty && draft.BasedOnRevision is { } draftRevision
                ? new(draft.Id, draftRevision) : reference;
            string planPath = lease.Path;
            ConfigurationReference? Active() => (library as IActiveConfigurationService)?.Active;
            var services = new ConfigurationStudioSaveServices(
                () => Task.CompletedTask, Active,
                copy => { planPath = viewModel.Document.SourcePath!; return planner.CreatePlan(planPath, copy); },
                planner.BuildReviewText,
                (plan, copy, durable) => operations.SaveAsync(() => commit.CommitAsync(plan, planPath, copy, managedId,
                    new(Active, savedReference => materializer.MaterializeAsync(savedReference),
                        _ => Task.CompletedTask,
                        (path, savedReference, savedPlan) =>
                        {
                            viewModel.AcceptSaved(path, savedReference.Id, savedPlan);
                            managedId = savedReference.Id;
                            editorReference = savedReference;
                        }, durable, editorReference))));
            async Task CheckpointAsync(CancellationToken cancellation)
            {
                viewModel.CommitPendingEdits();
                if (!viewModel.IsDirty) return;
                ConfigurationReference? capturedReference = editorReference;
                ConfigurationId capturedId = managedId;
                string yaml = viewModel.FullExportText;
                ConfigurationDraftEditorState editorState = viewModel.CaptureEditorState();
                var companions = viewModel.CaptureExportCompanionContents().ToDictionary(
                    pair => Path.GetFileName(pair.Key),
                    pair => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(pair.Value), StringComparer.OrdinalIgnoreCase);
                await operations.CheckpointAsync(async workerToken =>
                {
                    ConfigurationDraft current = await library.OpenDraftAsync(capturedId, workerToken);
                    if (current.BasedOnRevision != capturedReference?.Revision)
                        throw new ConfigurationRevisionConflictException(new(current.Id,
                            current.BasedOnRevision ?? capturedReference!.Revision));
                    await library.StageDraftAsync(current with { Yaml = yaml, IsDirty = true, EditorState = editorState }, companions, workerToken);
                }, cancellation);
            }
            var session = new MobileStudioSession(viewModel, services,
                async (destination, sanitized) =>
                {
                    var source = new ConfigurationStudioExportDocumentSet(viewModel.FullExportText,
                        "configuration.yaml", viewModel.CaptureExportCompanionContents());
                    var result = await ConfigurationBundleExporter.ExportAsync(viewModel.FullExportText, source,
                        destination, new(Sanitized: sanitized, IncludeCompanions: !sanitized));
                    return result.OmittedCompanionReferences;
                }, () =>
                {
                    background?.Dispose();
                    return operations.CloseAsync(async () =>
                    {
                        try { await library.DiscardDraftAsync(managedId); }
                        finally { await lease.DisposeAsync(); }
                    });
                }, recoveredOlderRevision
                    ? "Recovered unsaved draft from an older revision. Use Save Copy to keep these edits; the newer saved revision will not be replaced."
                    : draft.BasedOnRevision is null ? "New configuration. Add a system and channels, then review and save. Keys and aliases can be added from their Studio sections."
                    : draft.IsDirty ? "Recovered unsaved draft." : string.Empty, CheckpointAsync);
            background = new IosStudioBackgroundObserver(session.CheckpointAsync);
            return session;
        }
        catch { await lease.DisposeAsync(); throw; }
    }
}
