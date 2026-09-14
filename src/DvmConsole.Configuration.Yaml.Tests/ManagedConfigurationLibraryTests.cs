// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Text;
using DvmConsole.Application;
using DvmConsole.Core.Configuration;
using Xunit;

namespace DvmConsole.Configuration.Yaml.Tests;

public sealed class ManagedConfigurationLibraryTests : IDisposable
{
    private const string ValidYaml = """
        systems:
          - name: Test
            identity: Console
            address: 127.0.0.1
            port: 62031
            peerId: 1
            rid: "1001"
            aliasPath: ./alias.yml
        zones: []
        groups: []
        """;

    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"dvmconsole-configuration-library-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task IncompleteCheckpointSurvivesReopenWithoutBecomingAValidRevision()
    {
        var library = new ManagedConfigurationLibrary(root);
        ConfigurationDraft draft = await library.CreateDraftAsync("In progress");
        string incomplete = ValidYaml.Replace("port: 62031", "port: 0");
        draft = await library.StageDraftAsync(draft with { Yaml = incomplete },
            new Dictionary<string, ReadOnlyMemory<byte>>
            { ["alias.yml"] = Encoding.UTF8.GetBytes("- rid: 42\n  alias: Work in progress\n") });
        var reopened = new ManagedConfigurationLibrary(root);
        ConfigurationDraft recovered = await reopened.OpenDraftAsync(draft.Id);
        Assert.Equal(incomplete, recovered.Yaml);
        Assert.True(recovered.IsDirty);
        Assert.Null(recovered.BasedOnRevision);
        var destination = new MemoryDocumentSet("draft.yml", "incomplete-draft", string.Empty);
        await reopened.ExportDraftAsync(draft.Id, destination, new(false));
        Assert.Contains("Work in progress", destination.GetCompanion("alias.yml").Text);
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.CommitAsync(recovered).AsTask());
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.StageDraftAsync(
            recovered with { Yaml = "systems: [" }, new Dictionary<string, ReadOnlyMemory<byte>>()).AsTask());
        Assert.Equal(incomplete, (await reopened.OpenDraftAsync(draft.Id)).Yaml);
        Assert.Null(reopened.Active);
    }

    [Fact]
    public async Task BlankDraftCanBeMaterializedForEditingButCannotBeCommitted()
    {
        var library = new ManagedConfigurationLibrary(root);
        ConfigurationDraft draft = await library.CreateDraftAsync("New configuration");
        var destination = new MemoryDocumentSet("draft.yml", "blank-draft", string.Empty);
        ConfigurationDraft exported = await library.ExportDraftAsync(draft.Id, destination, new(false));
        Assert.Equal(draft.Id, exported.Id);
        Assert.Null(exported.BasedOnRevision);
        Assert.Empty(ConfigurationDocument.Parse(exported.Yaml).Configuration.Systems);
        await Assert.ThrowsAsync<InvalidDataException>(() => library.CommitAsync(draft).AsTask());
        Assert.True((await library.OpenDraftAsync(draft.Id)).IsDirty);
        Assert.Null(library.Active);
    }

    [Fact]
    public async Task EditorRecoveryStateIsAtomicWithYamlAndSurvivesLibraryReopen()
    {
        var library = new ManagedConfigurationLibrary(root);
        var draft = await library.CreateDraftAsync("Editor recovery");
        var editor = new ConfigurationDraftEditorState(
            new Dictionary<string, ConfigurationDraftPosition> { ["System::Channel"] = new(12, 34) },
            new Dictionary<string, string> { ["Zone"] = "System" }, ["System"]);
        draft = await library.StageDraftAsync(draft with { Yaml = ValidYaml, IsDirty = true, EditorState = editor },
            new Dictionary<string, ReadOnlyMemory<byte>>());
        string pending = Path.Combine(root, "drafts", "active.json.pending");
        Directory.CreateDirectory(pending);
        try
        {
            Exception? failure = await Record.ExceptionAsync(() => library.StageDraftAsync(draft with
            {
                Yaml = ValidYaml.Replace("Console", "Uncommitted"),
                EditorState = editor with { CallPrioritySystemNames = [] }
            }, new Dictionary<string, ReadOnlyMemory<byte>>()).AsTask());
            Assert.True(failure is IOException or UnauthorizedAccessException);
        }
        finally { Directory.Delete(pending); }
        var recovered = await new ManagedConfigurationLibrary(root).OpenDraftAsync(draft.Id);
        Assert.Equal(ValidYaml, recovered.Yaml);
        Assert.NotNull(recovered.EditorState);
        Assert.Equal(new ConfigurationDraftPosition(12, 34), recovered.EditorState.ChannelPositions["System::Channel"]);
        Assert.Equal("System", recovered.EditorState.ZoneSystemAssignments["Zone"]);
        Assert.Equal("System", Assert.Single(recovered.EditorState.CallPrioritySystemNames));
    }

    [Fact]
    public async Task RetiredDraftCannotStageOrCommitIntoReopenedRevision()
    {
        var library = new ManagedConfigurationLibrary(root);
        var initial = await library.CreateDraftAsync("Primary");
        var first = await library.CommitAsync(initial with { Yaml = ValidYaml });
        var retired = await library.OpenDraftAsync(first.Reference.Id);
        await library.CommitAsync(retired with { Yaml = ValidYaml.Replace("Console", "Newer") });
        var current = await library.OpenDraftAsync(first.Reference.Id);

        await Assert.ThrowsAsync<ConfigurationDraftConflictException>(() =>
            library.StageDraftAsync(retired, new Dictionary<string, ReadOnlyMemory<byte>>()).AsTask());
        await Assert.ThrowsAsync<ConfigurationDraftConflictException>(() => library.CommitAsync(retired).AsTask());
        var reopened = await library.OpenDraftAsync(first.Reference.Id);
        Assert.Equal(current.BasedOnRevision, reopened.BasedOnRevision);
        Assert.Equal(current.Yaml, reopened.Yaml);
    }

    [Fact]
    public async Task CleanDraftRefreshesAfterImportedReplacementAndRejectsRetiredHandle()
    {
        var library = new ManagedConfigurationLibrary(root);
        var imported = await library.ImportAsync(new MemoryDocumentSet("original.yml", "original", ValidYaml), new());
        var retired = await library.OpenDraftAsync(imported.Reference.Id);
        var replacement = await library.ImportAsync(new MemoryDocumentSet("new.yml", "replacement", ValidYaml.Replace("Console", "Replacement")),
            new(ConfigurationConflictResolution.ReplaceExisting, imported.Reference.Id));
        var refreshed = await library.OpenDraftAsync(imported.Reference.Id);
        Assert.False(refreshed.IsDirty);
        Assert.Equal(replacement.Reference.Revision, refreshed.BasedOnRevision);
        Assert.Contains("Replacement", refreshed.Yaml);
        await Assert.ThrowsAsync<ConfigurationDraftConflictException>(() =>
            library.StageDraftAsync(retired, new Dictionary<string, ReadOnlyMemory<byte>>()).AsTask());
    }

    [Fact]
    public async Task FailedCleanDraftRefreshPreservesPreviousGeneration()
    {
        var library = new ManagedConfigurationLibrary(root);
        var imported = await library.ImportAsync(new MemoryDocumentSet("original.yml", "original", ValidYaml), new());
        _ = await library.OpenDraftAsync(imported.Reference.Id);
        string metadataPath = Path.Combine(root, "drafts", "active.json");
        string previousMetadata = await File.ReadAllTextAsync(metadataPath);
        var previousFiles = Directory.GetFiles(Path.Combine(root, "drafts"), "codeplug.yml", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllText);
        var replacement = await library.ImportAsync(new MemoryDocumentSet("new.yml", "replacement", ValidYaml.Replace("Console", "Replacement")),
            new(ConfigurationConflictResolution.ReplaceExisting, imported.Reference.Id));
        string pending = metadataPath + ".pending";
        Directory.CreateDirectory(pending);
        try
        {
            Exception? failure = await Record.ExceptionAsync(() => library.OpenDraftAsync(imported.Reference.Id).AsTask());
            Assert.True(failure is IOException or UnauthorizedAccessException);
            Assert.Equal(previousMetadata, await File.ReadAllTextAsync(metadataPath));
            foreach (var file in previousFiles)
                Assert.Equal(file.Value, await File.ReadAllTextAsync(file.Key));
        }
        finally { Directory.Delete(pending); }
        var refreshed = await new ManagedConfigurationLibrary(root).OpenDraftAsync(imported.Reference.Id);
        Assert.Equal(replacement.Reference.Revision, refreshed.BasedOnRevision);
    }

    [Fact]
    public async Task ImportedRevisionPreventsStaleCommitButPreservesDraftForCopy()
    {
        var library = new ManagedConfigurationLibrary(root);
        var imported = await library.ImportAsync(new MemoryDocumentSet("original.yml", "original", ValidYaml), new());
        var draft = await library.OpenDraftAsync(imported.Reference.Id);
        draft = await library.StageDraftAsync(draft with { Yaml = ValidYaml.Replace("Console", "Unsaved"), IsDirty = true },
            new Dictionary<string, ReadOnlyMemory<byte>>());
        var replacement = await library.ImportAsync(new MemoryDocumentSet("new.yml", "replacement", ValidYaml.Replace("Console", "Replacement")),
            new(ConfigurationConflictResolution.ReplaceExisting, imported.Reference.Id));

        await Assert.ThrowsAsync<ConfigurationRevisionConflictException>(() => library.CommitAsync(draft).AsTask());
        Assert.Contains("Replacement", await File.ReadAllTextAsync(RevisionYaml(replacement.Reference)));
        Assert.Contains("Unsaved", (await library.OpenDraftAsync(imported.Reference.Id)).Yaml);
        var copy = await library.CommitDraftCopyAsync(draft, new Dictionary<string, ReadOnlyMemory<byte>>(), "Recovered copy");
        Assert.NotEqual(imported.Reference.Id, copy.Reference.Id);
        Assert.Contains("Unsaved", await File.ReadAllTextAsync(RevisionYaml(copy.Reference)));
    }

    [Fact]
    public async Task LegacySynchronousStartupBridgeDoesNotDependOnCallerSynchronizationContext()
    {
        var completion = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
            try
            {
                var library = new ManagedConfigurationLibrary(root);
                _ = library.CreateDraftAsync("Startup").AsTask().GetAwaiter().GetResult();
                completion.TrySetResult(null);
            }
            catch (Exception exception)
            {
                completion.TrySetResult(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Configuration library startup compatibility test"
        };

        thread.Start();
        Exception? failure = await completion.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(failure);
        Assert.True(thread.Join(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task ConcurrentLibraryInstancesMergeCatalogUpdates()
    {
        var first = new ManagedConfigurationLibrary(root);
        var second = new ManagedConfigurationLibrary(root);

        await Task.WhenAll(
            first.RegisterLegacyCandidatesAsync([
                new LegacyConfigurationCandidate("First", "origin:first")]).AsTask(),
            second.RegisterLegacyCandidatesAsync([
                new LegacyConfigurationCandidate("Second", "origin:second")]).AsTask());

        List<ConfigurationSummary> entries = await ReadAllAsync(first.ListAsync());
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, entry => entry.Name == "First");
        Assert.Contains(entries, entry => entry.Name == "Second");
    }

    [Fact]
    public async Task CommitsCreateImmutableRevisionsAndActiveEntryBecomesPendingReload()
    {
        var library = new ManagedConfigurationLibrary(root);
        ConfigurationDraft draft = await library.CreateDraftAsync("Primary");
        ConfigurationCommit first = await library.CommitAsync(draft with { Yaml = ValidYaml });
        await library.ActivateAsync(first.Reference);

        ConfigurationDraft edit = await library.OpenDraftAsync(first.Reference.Id);
        ConfigurationCommit second = await library.CommitAsync(edit with
        {
            Yaml = ValidYaml + "\ncustomField: retained\n",
            IsDirty = true
        });

        Assert.Equal(first.Reference.Id, second.Reference.Id);
        Assert.NotEqual(first.Reference.Revision, second.Reference.Revision);
        Assert.True(File.Exists(RevisionYaml(first.Reference)));
        Assert.True(File.Exists(RevisionYaml(second.Reference)));
        ConfigurationSummary summary = Assert.Single(await ReadAllAsync(library.ListAsync()));
        Assert.True(summary.IsActive);
        Assert.True(summary.PendingReload);
        Assert.Equal(first.Reference, library.Active);

        await library.ReloadAsync();
        Assert.Equal(second.Reference, library.Active);

        await library.DeactivateAsync();
        Assert.Null(library.Active);
    }

    [Fact]
    public async Task StartupRepairsRestoredCatalogPointersFromTheLastExistingRevision()
    {
        var library = new ManagedConfigurationLibrary(root);
        ConfigurationDraft draft = await library.CreateDraftAsync("Primary");
        ConfigurationCommit first = await library.CommitAsync(draft with { Yaml = ValidYaml });
        await library.ActivateAsync(first.Reference);
        string currentPath = CurrentRevisionPath(first.Reference.Id);
        string firstCurrentPointer = File.ReadAllText(currentPath);

        ConfigurationDraft edit = await library.OpenDraftAsync(first.Reference.Id);
        ConfigurationCommit second = await library.CommitAsync(edit with
        {
            Yaml = ValidYaml + "\ncustomField: retained\n",
            IsDirty = true
        });
        await library.ActivateAsync(second.Reference);

        File.WriteAllText(currentPath, firstCurrentPointer);
        Directory.Delete(Path.GetDirectoryName(RevisionYaml(second.Reference))!, recursive: true);

        var restored = new ManagedConfigurationLibrary(root);

        Assert.Equal(first.Reference, restored.Active);
        ConfigurationSummary summary = Assert.Single(await ReadAllAsync(restored.ListAsync()));
        Assert.Equal(first.Reference.Revision, summary.CurrentRevision);
        Assert.True(summary.IsActive);
        Assert.False(summary.PendingReload);
        var destination = new MemoryDocumentSet("export.yml", "export:test", string.Empty);
        await restored.ExportAsync(first.Reference, destination, new ConfigurationExportOptions(false));
        Assert.Contains("name: Test", destination.PrimaryDocument.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupDeactivatesAnEntryWhenNoRestoredRevisionExists()
    {
        var library = new ManagedConfigurationLibrary(root);
        ConfigurationDraft draft = await library.CreateDraftAsync("Primary");
        ConfigurationCommit commit = await library.CommitAsync(draft with { Yaml = ValidYaml });
        await library.ActivateAsync(commit.Reference);
        Directory.Delete(Path.GetDirectoryName(RevisionYaml(commit.Reference))!, recursive: true);

        var restored = new ManagedConfigurationLibrary(root);

        Assert.Null(restored.Active);
        Assert.False(Assert.Single(await ReadAllAsync(restored.ListAsync())).IsActive);
    }

    [Fact]
    public async Task ActivationPersistsTheManagedConfigurationLastOpenedTime()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero));
        var library = new ManagedConfigurationLibrary(root, clock);
        ConfigurationDraft draft = await library.CreateDraftAsync("Primary");
        ConfigurationCommit commit = await library.CommitAsync(draft with { Yaml = ValidYaml });
        Assert.Null(Assert.Single(await ReadAllAsync(library.ListAsync())).LastOpenedAt);

        clock.UtcNow = clock.UtcNow.AddMinutes(7);
        await library.ActivateAsync(commit.Reference);

        ConfigurationSummary activated = Assert.Single(await ReadAllAsync(library.ListAsync()));
        Assert.Equal(clock.UtcNow, activated.LastOpenedAt);
        var reopenedLibrary = new ManagedConfigurationLibrary(root);
        Assert.Equal(clock.UtcNow, Assert.Single(await ReadAllAsync(reopenedLibrary.ListAsync())).LastOpenedAt);
    }

    [Fact]
    public async Task ReimportUsesOriginAndFingerprintWithoutTouchingTheSource()
    {
        var library = new ManagedConfigurationLibrary(root);
        var source = new MemoryDocumentSet("legacy.yml", "file-id:one", ValidYaml);
        byte[] original = source.PrimaryDocument.Content;

        ConfigurationImportResult first = await library.ImportAsync(source, new());
        ConfigurationImportResult second = await library.ImportAsync(source, new());
        var moved = new MemoryDocumentSet("legacy.yml", "file-id:two", ValidYaml);
        ConfigurationImportResult movedResult = await library.ImportAsync(moved, new());

        Assert.True(second.ReusedExisting);
        Assert.Equal(first.Reference, second.Reference);
        Assert.NotEqual(first.Reference.Id, movedResult.Reference.Id);
        Assert.Equal(original, source.PrimaryDocument.Content);
    }

    [Fact]
    public async Task MovedOriginCreatesANewEntryUnlessReplacementTargetIsExplicit()
    {
        var library = new ManagedConfigurationLibrary(root);
        ConfigurationImportResult original = await library.ImportAsync(
            new MemoryDocumentSet("legacy.yml", "file-id:one", ValidYaml),
            new());

        ConfigurationImportResult moved = await library.ImportAsync(
            new MemoryDocumentSet("legacy.yml", "file-id:two", ValidYaml),
            new());
        ConfigurationImportResult explicitReplacement = await library.ImportAsync(
            new MemoryDocumentSet(
                "renamed.yml",
                "file-id:three",
                ValidYaml + "\ncustomMovedField: retained\n"),
            new(
                ConfigurationConflictResolution.ReplaceExisting,
                original.Reference.Id));

        Assert.NotEqual(original.Reference.Id, moved.Reference.Id);
        Assert.Equal(original.Reference.Id, explicitReplacement.Reference.Id);
        Assert.NotEqual(original.Reference.Revision, explicitReplacement.Reference.Revision);
        Assert.True(explicitReplacement.AppendedRevision);
        Assert.True(File.Exists(RevisionYaml(original.Reference)));
        Assert.True(File.Exists(RevisionYaml(explicitReplacement.Reference)));
    }

    [Fact]
    public async Task LegacyCandidatesImportLazilyUnderTheirReservedIds()
    {
        var library = new ManagedConfigurationLibrary(root);
        await library.RegisterLegacyCandidatesAsync(
            [new LegacyConfigurationCandidate("Legacy Dispatch", "file-id:legacy")]);

        ConfigurationSummary candidate = Assert.Single(await ReadAllAsync(library.ListAsync()));
        Assert.True(candidate.IsLegacyCandidate);
        Assert.Equal("file-id:legacy", candidate.LegacyOriginIdentity);

        var source = new MemoryDocumentSet("legacy.yml", "file-id:legacy", ValidYaml);
        ConfigurationImportResult imported = await library.ImportAsync(source, new());
        ConfigurationSummary managed = Assert.Single(await ReadAllAsync(library.ListAsync()));

        Assert.Equal(candidate.Id, imported.Reference.Id);
        Assert.False(imported.AppendedRevision);
        Assert.False(managed.IsLegacyCandidate);
        Assert.Null(managed.LegacyOriginIdentity);
    }

    [Fact]
    public async Task DivergedManagedEntryRequiresExplicitImportConflictResolution()
    {
        var library = new ManagedConfigurationLibrary(root);
        var source = new MemoryDocumentSet("legacy.yml", "file-id:one", ValidYaml);
        ConfigurationImportResult imported = await library.ImportAsync(source, new());
        ConfigurationDraft edit = await library.OpenDraftAsync(imported.Reference.Id);
        await library.CommitAsync(edit with
        {
            Yaml = ValidYaml + "\noperatorNote: changed locally\n",
            IsDirty = true
        });
        source.PrimaryDocument.SetText(ValidYaml + "\nsourceNote: changed externally\n");

        await Assert.ThrowsAsync<ConfigurationImportConflictException>(async () =>
            await library.ImportAsync(source, new()));
        ConfigurationImportResult asNew = await library.ImportAsync(
            source,
            new(ConfigurationConflictResolution.ImportAsNew));

        Assert.NotEqual(imported.Reference.Id, asNew.Reference.Id);
        Assert.Equal(2, (await ReadAllAsync(library.ListAsync())).Count);
    }

    [Fact]
    public async Task CompanionsAreManagedAndExportedBundleIsReadBackBeforeSuccess()
    {
        const string yaml = """
            keyFile: ./keys.clear
            systems:
              - name: Test
                identity: Console
                address: 127.0.0.1
                port: 62031
                peerId: 1
                rid: "1001"
                aliasPath: ./alias.yml
            zones:
              - name: Operations
                channels:
                  - name: Dispatch
                    system: Test
                    tgid: "101"
                    mode: p25
                    card_size: normal
            groups: []
            """;
        var source = new MemoryDocumentSet("legacy.yml", "file-id:one", yaml);
        source.AddCompanion("./keys.clear", "keys: []\n");
        source.AddCompanion("./alias.yml", "[]\n");
        var library = new ManagedConfigurationLibrary(root);
        ConfigurationImportResult imported = await library.ImportAsync(source, new());
        var export = new MemoryDocumentSet("export.yml", "export-id", string.Empty);

        await library.ExportAsync(imported.Reference, export, new(Sanitized: false));

        string exportedYaml = export.PrimaryDocument.Text;
        Assert.Contains("./keys.clear", exportedYaml, StringComparison.Ordinal);
        Assert.Contains("./alias.yml", exportedYaml, StringComparison.Ordinal);
        Assert.Equal("keys: []\n", export.GetCompanion("keys.clear").Text);
        Assert.Equal("[]\n", export.GetCompanion("alias.yml").Text);
        Assert.Equal(yaml, source.PrimaryDocument.Text);
    }

    [Fact]
    public async Task DraftRecoveryExportsPersistedEditsAndCompanionsWithoutCommitting()
    {
        var source = new MemoryDocumentSet("original.yml", "recovery-source", ValidYaml);
        source.AddCompanion("./alias.yml", "- id: 1\n  name: Original\n");
        var library = new ManagedConfigurationLibrary(root);
        var imported = await library.ImportAsync(source, new());
        await library.ActivateAsync(imported.Reference);
        ConfigurationDraft draft = await library.OpenDraftAsync(imported.Reference.Id);
        await library.StageDraftAsync(draft with { Yaml = draft.Yaml.Replace("Console", "Recovered"), IsDirty = true },
            new Dictionary<string, ReadOnlyMemory<byte>> { ["alias.yml"] = Encoding.UTF8.GetBytes("- id: 1\n  name: Recovered\n") });

        var restored = new ManagedConfigurationLibrary(root);
        var recovered = new MemoryDocumentSet("recovered.yml", "recovered-export", string.Empty);
        ConfigurationDraft exported = await restored.ExportDraftAsync(imported.Reference.Id, recovered, new(false));
        Assert.True(exported.IsDirty);
        Assert.Equal(imported.Reference.Revision, exported.BasedOnRevision);
        Assert.Contains("Recovered", recovered.PrimaryDocument.Text);
        Assert.Contains("Recovered", recovered.GetCompanion("alias.yml").Text);
        Assert.Equal(imported.Reference, restored.Active);
        Assert.Equal(imported.Reference.Revision, Assert.Single(await ReadAllAsync(restored.ListAsync())).CurrentRevision);
        Assert.True((await restored.OpenDraftAsync(imported.Reference.Id)).IsDirty);

        var committed = new MemoryDocumentSet("committed.yml", "committed-export", string.Empty);
        await restored.ExportAsync(imported.Reference, committed, new(false));
        Assert.Contains("Console", committed.PrimaryDocument.Text);
        Assert.Contains("Original", committed.GetCompanion("alias.yml").Text);
    }

    [Fact]
    public async Task FailedDraftMetadataWritePreservesThePreviousYamlAndCompanions()
    {
        var library = new ManagedConfigurationLibrary(root);
        ConfigurationDraft draft = await library.CreateDraftAsync("Recoverable");
        draft = await library.StageDraftAsync(draft with { Yaml = ValidYaml, IsDirty = true },
            new Dictionary<string, ReadOnlyMemory<byte>> { ["alias.yml"] = Encoding.UTF8.GetBytes("old companion") });
        string pending = Path.Combine(root, "drafts", "active.json.pending");
        Directory.CreateDirectory(pending);
        try
        {
            Exception? failure = await Record.ExceptionAsync(() => library.StageDraftAsync(
                draft with { Yaml = ValidYaml.Replace("Console", "Uncommitted") },
                new Dictionary<string, ReadOnlyMemory<byte>> { ["alias.yml"] = Encoding.UTF8.GetBytes("new companion") }).AsTask());
            Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        }
        finally { Directory.Delete(pending); }

        var restored = new ManagedConfigurationLibrary(root);
        ConfigurationDraft recovered = await restored.OpenDraftAsync(draft.Id);
        Assert.Equal(draft.Yaml, recovered.Yaml);
        Assert.True(recovered.IsDirty);
        var destination = new MemoryDocumentSet("recovered.yml", "failed-write-export", string.Empty);
        await restored.ExportDraftAsync(draft.Id, destination, new(false));
        Assert.Equal("old companion", destination.GetCompanion("alias.yml").Text);
    }

    [Fact]
    public async Task PendingDraftMetadataRecoversACompleteContentGeneration()
    {
        var library = new ManagedConfigurationLibrary(root);
        ConfigurationDraft draft = await library.CreateDraftAsync("Pending");
        draft = await library.StageDraftAsync(draft with { Yaml = ValidYaml },
            new Dictionary<string, ReadOnlyMemory<byte>> { ["alias.yml"] = Encoding.UTF8.GetBytes("pending companion") });
        string active = Path.Combine(root, "drafts", "active.json");
        File.Move(active, active + ".pending");
        var restored = new ManagedConfigurationLibrary(root);
        Assert.Equal(draft.Yaml, (await restored.OpenDraftAsync(draft.Id)).Yaml);
        var destination = new MemoryDocumentSet("recovered.yml", "pending-export", string.Empty);
        await restored.ExportDraftAsync(draft.Id, destination, new(false));
        Assert.Equal("pending companion", destination.GetCompanion("alias.yml").Text);
        Assert.False(File.Exists(active + ".pending"));
    }

    [Fact]
    public async Task LegacyDraftMigratesWithoutLosingCompanionsAndRetiresOldGenerations()
    {
        var library = new ManagedConfigurationLibrary(root);
        ConfigurationDraft draft = await library.CreateDraftAsync("Legacy");
        draft = await library.StageDraftAsync(draft with { Yaml = ValidYaml },
            new Dictionary<string, ReadOnlyMemory<byte>> { ["alias.yml"] = Encoding.UTF8.GetBytes("legacy companion") });
        string active = Path.Combine(root, "drafts", "active.json");
        var metadata = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(active))!.AsObject();
        string content = metadata["ContentRevision"]!.GetValue<string>();
        string draftRoot = Path.Combine(root, "drafts", draft.Id.Value.ToString("N"));
        string generation = Path.Combine(draftRoot, Guid.Parse(content).ToString("N"));
        File.Move(Path.Combine(generation, "codeplug.yml"), Path.Combine(draftRoot, "codeplug.yml"));
        Directory.Move(Path.Combine(generation, "companions"), Path.Combine(draftRoot, "companions"));
        Directory.Delete(generation);
        metadata.Remove("ContentRevision");
        File.WriteAllText(active, metadata.ToJsonString());

        var restored = new ManagedConfigurationLibrary(root);
        draft = await restored.OpenDraftAsync(draft.Id);
        Assert.Equal(ValidYaml, draft.Yaml);
        for (int revision = 0; revision < 3; revision++)
            draft = await restored.StageDraftAsync(draft with { Name = $"Edit {revision}" },
                new Dictionary<string, ReadOnlyMemory<byte>>());
        Assert.Single(Directory.EnumerateDirectories(draftRoot),
            path => Guid.TryParseExact(Path.GetFileName(path), "N", out _));
        var destination = new MemoryDocumentSet("recovered.yml", "legacy-export", string.Empty);
        await restored.ExportDraftAsync(draft.Id, destination, new(false));
        Assert.Equal("legacy companion", destination.GetCompanion("alias.yml").Text);
    }

    [Fact]
    public async Task CancelledDraftRecoveryPreservesTheDraft()
    {
        var library = new ManagedConfigurationLibrary(root);
        ConfigurationDraft draft = await library.CreateDraftAsync("Recovery");
        await library.StageDraftAsync(draft with { Yaml = ValidYaml, IsDirty = true }, new Dictionary<string, ReadOnlyMemory<byte>>());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => library.ExportDraftAsync(draft.Id,
            new MemoryDocumentSet("cancelled.yml", "cancelled-export", string.Empty), new(false), cancelled.Token).AsTask());
        Assert.True((await library.OpenDraftAsync(draft.Id)).IsDirty);
    }

    [Fact]
    public async Task CopyOfPersistedDirtyDraftPreservesOriginalRevisionAndCompanions()
    {
        var library = new ManagedConfigurationLibrary(root);
        ConfigurationDraft draft = await library.CreateDraftAsync("Original");
        draft = await library.StageDraftAsync(draft with { Yaml = ValidYaml, IsDirty = true },
            new Dictionary<string, ReadOnlyMemory<byte>> { ["alias.yml"] = "[]\n"u8.ToArray() });
        ConfigurationCommit original = await library.CommitAsync(draft);
        await library.ActivateAsync(original.Reference);
        draft = await library.OpenDraftAsync(original.Reference.Id);
        draft = await library.StageDraftAsync(draft with { Yaml = draft.Yaml.Replace("Console", "Recovered"), IsDirty = true },
            new Dictionary<string, ReadOnlyMemory<byte>> { ["alias.yml"] = "- rid: 42\n  alias: Recovered\n"u8.ToArray() });

        ConfigurationCommit copy = await library.CommitDraftCopyAsync(draft,
            new Dictionary<string, ReadOnlyMemory<byte>>(), "Saved copy");
        Assert.NotEqual(original.Reference.Id, copy.Reference.Id);
        Assert.Equal(original.Reference, library.Active);
        var entries = await ReadAllAsync(library.ListAsync());
        Assert.Equal(2, entries.Count);
        Assert.Equal(original.Reference.Revision, Assert.Single(entries, entry => entry.Id == original.Reference.Id).CurrentRevision);
        Assert.Equal("Saved copy", Assert.Single(entries, entry => entry.Id == copy.Reference.Id).Name);
        var exported = new MemoryDocumentSet("copy.yml", "dirty-copy-export", string.Empty);
        await library.ExportAsync(copy.Reference, exported, new(false));
        Assert.Contains("Recovered", exported.PrimaryDocument.Text);
        Assert.Contains("Recovered", exported.GetCompanion("alias.yml").Text);
        Assert.False((await library.OpenDraftAsync(copy.Reference.Id)).IsDirty);
        await library.ExportAsync(original.Reference, exported, new(false));
        Assert.Contains("Console", exported.PrimaryDocument.Text);
        Assert.Equal("[]\n", exported.GetCompanion("alias.yml").Text);
    }

    [Fact]
    public async Task InvalidCopyLeavesPersistedDirtyDraftRecoverable()
    {
        var library = new ManagedConfigurationLibrary(root);
        ConfigurationDraft draft = await library.CreateDraftAsync("Original");
        ConfigurationCommit original = await library.CommitAsync(draft with { Yaml = ValidYaml });
        draft = await library.OpenDraftAsync(original.Reference.Id);
        draft = await library.StageDraftAsync(draft with { Yaml = draft.Yaml.Replace("Console", "Recovered"), IsDirty = true },
            new Dictionary<string, ReadOnlyMemory<byte>>());
        await Assert.ThrowsAsync<InvalidDataException>(() => library.CommitDraftCopyAsync(
            draft with { Yaml = draft.Yaml.Replace("62031", "70000") },
            new Dictionary<string, ReadOnlyMemory<byte>>(), "Invalid copy").AsTask());
        ConfigurationDraft recovered = await library.OpenDraftAsync(original.Reference.Id);
        Assert.True(recovered.IsDirty);
        Assert.Contains("Recovered", recovered.Yaml);
        Assert.Single(await ReadAllAsync(library.ListAsync()));
    }

    [Fact]
    public async Task ImportRecoversZoneEntriesMisplacedUnderSystems()
    {
        const string malformedYaml = """
            systems:
              - name: Test FNE
                identity: Console
                address: 127.0.0.1
                port: 62031
                peerId: 1
                rid: "1001"
              - name: Dispatch
                tabColor: '#FF6F61'
                channels:
                  - name: Main
                    system: Test FNE
                    tgid: 101
                    mode: p25
            groups: []
            """;
        var source = new MemoryDocumentSet("misplaced-zone.yml", "file-id:misplaced-zone", malformedYaml);
        var library = new ManagedConfigurationLibrary(root);

        ConfigurationImportResult imported = await library.ImportAsync(source, new());
        var export = new MemoryDocumentSet("export.yml", "export-id", string.Empty);
        await library.ExportAsync(imported.Reference, export, new(Sanitized: false));

        Assert.Contains(
            "Recovered 1 zone entry from the systems list: Dispatch.",
            imported.Warnings);
        Assert.Contains("zones:", export.PrimaryDocument.Text, StringComparison.Ordinal);
        Assert.Contains("name: Dispatch", export.PrimaryDocument.Text, StringComparison.Ordinal);
        Assert.Contains("name: Main", export.PrimaryDocument.Text, StringComparison.Ordinal);
        ConfigurationDocument exportedDocument = ConfigurationDocument.Parse(export.PrimaryDocument.Text);
        Assert.Single(exportedDocument.Configuration.Systems);
        ZoneConfiguration exportedZone = Assert.Single(exportedDocument.Configuration.Zones);
        Assert.Equal("Dispatch", exportedZone.Name);
        Assert.Single(exportedZone.Channels);
        Assert.Equal(malformedYaml, source.PrimaryDocument.Text);
    }

    [Fact]
    public async Task CompanionContentChangesAppendAnImmutableImportedRevision()
    {
        var source = new MemoryDocumentSet("legacy.yml", "file-id:companion-change", ValidYaml);
        source.AddCompanion("./alias.yml", "- id: 1\n  name: First\n");
        var library = new ManagedConfigurationLibrary(root);
        ConfigurationImportResult first = await library.ImportAsync(source, new());

        source.AddCompanion("./alias.yml", "- id: 1\n  name: Second\n");
        ConfigurationImportResult second = await library.ImportAsync(source, new());

        Assert.True(second.AppendedRevision);
        Assert.Equal(first.Reference.Id, second.Reference.Id);
        Assert.NotEqual(first.Reference.Revision, second.Reference.Revision);
        var firstExport = new MemoryDocumentSet("first.yml", "first-export", string.Empty);
        var secondExport = new MemoryDocumentSet("second.yml", "second-export", string.Empty);
        await library.ExportAsync(first.Reference, firstExport, new(Sanitized: false));
        await library.ExportAsync(second.Reference, secondExport, new(Sanitized: false));
        Assert.Contains("First", firstExport.GetCompanion("alias.yml").Text, StringComparison.Ordinal);
        Assert.Contains("Second", secondExport.GetCompanion("alias.yml").Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExternalCompanionsRequireConfirmationBeforeCreatingManagedState()
    {
        const string externalReference = "/operator/keys/keys.clear";
        string yaml = $$"""
            keyFile: {{externalReference}}
            systems:
              - name: Test
                identity: Console
                address: 127.0.0.1
                port: 62031
                peerId: 1
                rid: "1001"
            zones: []
            groups: []
            """;
        var source = new MemoryDocumentSet("legacy.yml", "file-id:external", yaml);
        source.AddCompanion(externalReference, "keys: []\n");
        var library = new ManagedConfigurationLibrary(root);

        ConfigurationExternalCompanionsConfirmationRequiredException confirmation =
            await Assert.ThrowsAsync<ConfigurationExternalCompanionsConfirmationRequiredException>(async () =>
                await library.ImportAsync(source, new()));

        Assert.Equal([externalReference], confirmation.References);
        Assert.Empty(await ReadAllAsync(library.ListAsync()));

        ConfigurationImportResult imported = await library.ImportAsync(
            source,
            new ConfigurationImportOptions(ConfirmExternalCompanions: true));
        var export = new MemoryDocumentSet("export.yml", "export-id", string.Empty);
        await library.ExportAsync(imported.Reference, export, new(Sanitized: false));

        Assert.DoesNotContain(externalReference, export.PrimaryDocument.Text, StringComparison.Ordinal);
        Assert.Contains("companions/keys.clear", File.ReadAllText(RevisionYaml(imported.Reference)), StringComparison.Ordinal);
        Assert.Contains("./keys.clear", export.PrimaryDocument.Text, StringComparison.Ordinal);
        Assert.Equal("keys: []\n", export.GetCompanion("keys.clear").Text);
        Assert.Equal(yaml, source.PrimaryDocument.Text);
    }

    [Fact]
    public async Task ReadOnlyYamlCannotRetainAnExternalCompanionDependency()
    {
        const string externalReference = "/operator/keys/keys.clear";
        string yaml = $$"""
            keyFile: &externalKey {{externalReference}}
            customKeyReference: *externalKey
            systems:
              - name: Test
                identity: Console
                address: 127.0.0.1
                port: 62031
                peerId: 1
                rid: "1001"
            zones: []
            groups: []
            """;
        var source = new MemoryDocumentSet("legacy.yml", "file-id:readonly-external", yaml);
        source.AddCompanion(externalReference, "keys: []\n");
        var library = new ManagedConfigurationLibrary(root);

        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await library.ImportAsync(
                source,
                new ConfigurationImportOptions(ConfirmExternalCompanions: true)));

        Assert.Contains("cannot be safely rewritten", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await ReadAllAsync(library.ListAsync()));
    }

    [Fact]
    public async Task TrashIsRecoverableAndActiveConfigurationCannotBeRemoved()
    {
        var library = new ManagedConfigurationLibrary(root);
        ConfigurationDraft draft = await library.CreateDraftAsync("Primary");
        ConfigurationCommit commit = await library.CommitAsync(draft with { Yaml = ValidYaml });
        await library.MoveToTrashAsync(commit.Reference.Id);
        Assert.Empty(await ReadAllAsync(library.ListAsync()));
        ConfigurationSummary trashed = Assert.Single(await ReadAllAsync(library.ListTrashAsync()));
        Assert.Equal(commit.Reference.Id, trashed.Id);
        Assert.False(trashed.IsActive);

        await library.RestoreFromTrashAsync(commit.Reference.Id);
        Assert.Single(await ReadAllAsync(library.ListAsync()));
        Assert.Empty(await ReadAllAsync(library.ListTrashAsync()));
        await library.ActivateAsync(commit.Reference);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await library.MoveToTrashAsync(commit.Reference.Id));
    }

    [Fact]
    public async Task MissingCatalogRecoversFromCompletedPendingJournal()
    {
        var library = new ManagedConfigurationLibrary(root);
        ConfigurationDraft draft = await library.CreateDraftAsync("Primary");
        await library.CommitAsync(draft with { Yaml = ValidYaml });
        string catalog = Path.Combine(root, "catalog.json");
        File.Copy(catalog, catalog + ".pending");
        File.Delete(catalog);

        var recovered = new ManagedConfigurationLibrary(root);

        Assert.Single(await ReadAllAsync(recovered.ListAsync()));
        Assert.True(File.Exists(catalog));
        Assert.False(File.Exists(catalog + ".pending"));
    }

    [Fact]
    public async Task IncompletePendingJournalLeavesCommittedCatalogIntact()
    {
        var library = new ManagedConfigurationLibrary(root);
        ConfigurationDraft draft = await library.CreateDraftAsync("Primary");
        ConfigurationCommit commit = await library.CommitAsync(draft with { Yaml = ValidYaml });
        string catalog = Path.Combine(root, "catalog.json");
        File.WriteAllText(catalog + ".pending", "{incomplete");

        var recovered = new ManagedConfigurationLibrary(root);

        ConfigurationSummary summary = Assert.Single(await ReadAllAsync(recovered.ListAsync()));
        Assert.Equal(commit.Reference, new ConfigurationReference(summary.Id, summary.CurrentRevision));
        Assert.False(File.Exists(catalog + ".pending"));
    }

    [Fact]
    public async Task CorruptCatalogRecoversFromValidAtomicBackup()
    {
        var library = new ManagedConfigurationLibrary(root);
        ConfigurationDraft draft = await library.CreateDraftAsync("Primary");
        await library.CommitAsync(draft with { Yaml = ValidYaml });
        string catalog = Path.Combine(root, "catalog.json");
        File.Copy(catalog, catalog + ".backup", overwrite: true);
        File.WriteAllText(catalog, "{not valid json");

        var recovered = new ManagedConfigurationLibrary(root);

        Assert.Single(await ReadAllAsync(recovered.ListAsync()));
        Assert.False(File.Exists(catalog + ".backup"));
    }

    [Fact]
    public async Task DirtyDraftBlocksReplacementUntilDiscarded()
    {
        var library = new ManagedConfigurationLibrary(root);
        ConfigurationDraft first = await library.CreateDraftAsync("First");

        ConfigurationDraftConflictException conflict = await Assert.ThrowsAsync<ConfigurationDraftConflictException>(async () =>
            await library.CreateDraftAsync("Second"));
        Assert.Equal(first.Id, conflict.ExistingDraft.Id);

        await library.DiscardDraftAsync(first.Id);
        ConfigurationDraft second = await library.CreateDraftAsync("Second");
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task DuplicateRemovesWebAuthorizationAndPreservesUnknownFields()
    {
        const string authorizedYaml = """
            systems:
              - name: Test
                identity: Console
                address: 127.0.0.1
                port: 62031
                peerId: 1
                rid: "1001"
            zones:
              - name: Dispatch
                customZoneField: retained
                channels: []
                web_streams:
                  - name: Secure stream
                    url: https://example.invalid/audio
                    authUsername: operator
                    authPassword: secret
            groups: []
            customRootField: retained
            """;
        var library = new ManagedConfigurationLibrary(root);
        ConfigurationDraft draft = await library.CreateDraftAsync("Primary");
        ConfigurationCommit commit = await library.CommitAsync(draft with { Yaml = authorizedYaml });

        ConfigurationReference copy = await library.DuplicateAsync(commit.Reference.Id, "Copy");
        var export = new MemoryDocumentSet("copy.yml", "copy-export", string.Empty);
        await library.ExportAsync(copy, export, new(Sanitized: false));

        Assert.DoesNotContain("operator", export.PrimaryDocument.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", export.PrimaryDocument.Text, StringComparison.Ordinal);
        Assert.Contains("customZoneField: retained", export.PrimaryDocument.Text, StringComparison.Ordinal);
        Assert.Contains("customRootField: retained", export.PrimaryDocument.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirtyStudioBundleExportUsesDocumentHandlesAndReadsBackCompanions()
    {
        var source = new MemoryDocumentSet("draft.yml", "draft-origin", ValidYaml);
        source.AddCompanion("./alias.yml", "- id: 1001\n  name: Dispatch\n");
        var destination = new MemoryDocumentSet("portable.yml", "export-origin", string.Empty);

        await ConfigurationBundleExporter.ExportAsync(
            ValidYaml + "\ncustomDraftField: retained\n",
            source,
            destination,
            new ConfigurationExportOptions(Sanitized: false));

        Assert.Contains("aliasPath: ./alias.yml", destination.PrimaryDocument.Text, StringComparison.Ordinal);
        Assert.Contains("customDraftField: retained", destination.PrimaryDocument.Text, StringComparison.Ordinal);
        Assert.Contains("Dispatch", destination.GetCompanion("alias.yml").Text, StringComparison.Ordinal);
        Assert.Equal(ValidYaml, source.PrimaryDocument.Text);
    }

    [Fact]
    public async Task DirtyStudioBundleExportUsesHostIndependentPortableCompanionNames()
    {
        const string yaml = """
            keyFile: './CON?.clear. '
            systems:
              - name: Test
                identity: Console
                address: 127.0.0.1
                port: 62031
                peerId: 1
                rid: "1001"
                aliasPath: './dispatch:aliases?.yml'
            zones: []
            groups: []
            """;
        var source = new MemoryDocumentSet("draft.yml", "draft-origin", yaml);
        source.AddCompanion("./CON?.clear. ", "[]\n");
        source.AddCompanion("./dispatch:aliases?.yml", "- id: 1001\n  name: Dispatch\n");
        var destination = new MemoryDocumentSet("portable.yml", "export-origin", string.Empty);

        await ConfigurationBundleExporter.ExportAsync(
            yaml,
            source,
            destination,
            new ConfigurationExportOptions(Sanitized: false));

        Assert.Contains("keyFile: ./_CON.clear", destination.PrimaryDocument.Text, StringComparison.Ordinal);
        Assert.Contains("aliasPath: ./dispatchaliases.yml", destination.PrimaryDocument.Text, StringComparison.Ordinal);
        Assert.Equal("[]\n", destination.GetCompanion("_CON.clear").Text);
        Assert.Contains("Dispatch", destination.GetCompanion("dispatchaliases.yml").Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SanitizedDirtyStudioExportDoesNotCopyCompanionsOrCredentials()
    {
        const string yaml = """
            systems:
              - name: Test
                identity: Console
                address: 127.0.0.1
                port: 62031
                peerId: 1
                rid: "1001"
                password: secret
                aliasPath: ./alias.yml
            zones: []
            groups: []
            """;
        var source = new MemoryDocumentSet("draft.yml", "draft-origin", yaml);
        source.AddCompanion("./alias.yml", "[]\n");
        var destination = new MemoryDocumentSet("support.yml", "support-origin", string.Empty);

        await ConfigurationBundleExporter.ExportAsync(
            yaml,
            source,
            destination,
            new ConfigurationExportOptions(Sanitized: true));

        Assert.DoesNotContain("secret", destination.PrimaryDocument.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", destination.PrimaryDocument.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("alias.yml", destination.PrimaryDocument.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("tgid: '101'", destination.PrimaryDocument.Text, StringComparison.Ordinal);
        ConfigurationDocument reparsed = ConfigurationDocument.Parse(destination.PrimaryDocument.Text);
        Assert.DoesNotContain(reparsed.Validate(), issue => issue.IsError);
    }

    [Fact]
    public async Task SanitizedExportPseudonymizesNamesIdentifiersEncryptionAndColorsConsistently()
    {
        const string yaml = """
            patchSourceIdPassthrough: true
            systems:
              - name: EXAMPLE SYSTEM
                identity: EXAMPLE-CONSOLE
                address: 192.0.2.40
                port: 62031
                peerId: 1000001
                rid: "1000002"
                encrypted: true
                transportEncryptionMode: cbc
                password: DEMO_ONLY
                presharedKey: DEMO_ONLY
                kmfPresharedKey: DEMO_ONLY
                aliasPath: ./example-aliases.yml
            zones:
              - name: Zone Alpha
                tabColor: '#112233'
                tabTextColor: '#FFFFFF'
                channels:
                  - name: Dispatch Primary
                    system: EXAMPLE SYSTEM
                    tgid: '101'
                    algo: aes
                    keyId: '0x001'
                    selectableEncryption: true
                    resourceColor: '#445566'
                  - name: Dispatch Backup
                    system: EXAMPLE SYSTEM
                    tgid: '101'
                web_streams:
                  - name: Example Stream
                    url: https://audio.example/live/101
                    authUsername: DEMO_ONLY
                    authPassword: DEMO_ONLY
                    idleColor: '#778899'
            groups:
              - name: Example Patch
                type: patch
            """;
        var source = new MemoryDocumentSet("draft.yml", "draft-origin", yaml);
        var destination = new MemoryDocumentSet("support.yml", "support-origin", string.Empty);

        await ConfigurationBundleExporter.ExportAsync(
            yaml,
            source,
            destination,
            new ConfigurationExportOptions(Sanitized: true));

        string exported = destination.PrimaryDocument.Text;
        foreach (string sentinel in new[]
                 {
                     "EXAMPLE SYSTEM", "EXAMPLE-CONSOLE", "192.0.2.40", "1000001", "1000002",
                     "DEMO_ONLY", "example-aliases", "Zone Alpha", "Dispatch Primary", "101",
                     "0x001", "#112233", "#445566", "Example Stream", "audio.example",
                     "#778899", "Example Patch"
                 })
        {
            Assert.DoesNotContain(sentinel, exported, StringComparison.OrdinalIgnoreCase);
        }

        ConfigurationDocument reparsed = ConfigurationDocument.Parse(exported);
        Assert.DoesNotContain(reparsed.Validate(), issue => issue.IsError);
        Assert.Equal("System 1", reparsed.Configuration.Systems[0].Name);
        Assert.Equal("Zone 1", reparsed.Configuration.Zones[0].Name);
        Assert.Equal(
            reparsed.Configuration.Zones[0].Channels[0].Tgid,
            reparsed.Configuration.Zones[0].Channels[1].Tgid);
    }

    [Fact]
    public async Task DirtyStudioExportKeepsYamlWhenOptionalCompanionIsMissing()
    {
        var source = new MemoryDocumentSet("draft.yml", "draft-origin", ValidYaml);
        var destination = new MemoryDocumentSet("portable.yml", "export-origin", string.Empty);

        ConfigurationBundleExportResult result = await ConfigurationBundleExporter.ExportAsync(
            ValidYaml,
            source,
            destination,
            new ConfigurationExportOptions(Sanitized: false));

        Assert.Contains("aliasPath: ./alias.yml", destination.PrimaryDocument.Text, StringComparison.Ordinal);
        Assert.Equal(["./alias.yml"], result.OmittedCompanionReferences);
        Assert.Throws<KeyNotFoundException>(() => destination.GetCompanion("alias.yml"));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
            // A synchronous desktop-startup bridge cannot pump posted work.
        }
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private string RevisionYaml(ConfigurationReference reference)
        => Path.Combine(
            root,
            "entries",
            reference.Id.Value.ToString("N"),
            "revisions",
            reference.Revision.Value.ToString("N"),
            "codeplug.yml");

    private string CurrentRevisionPath(ConfigurationId id)
        => Path.Combine(root, "entries", id.Value.ToString("N"), "current.json");

    private static async Task<List<T>> ReadAllAsync<T>(IAsyncEnumerable<T> source)
    {
        var values = new List<T>();
        await foreach (T value in source)
            values.Add(value);
        return values;
    }

    private sealed class MemoryDocumentSet : IImportDocumentSet, IExportDocumentSet
    {
        private readonly Dictionary<string, MemoryDocument> companions =
            new(StringComparer.OrdinalIgnoreCase);

        public MemoryDocumentSet(string displayName, string originIdentity, string text)
        {
            PrimaryDocument = new MemoryDocument(displayName, originIdentity, Encoding.UTF8.GetBytes(text));
        }

        public MemoryDocument PrimaryDocument { get; }
        public IReadableDocument Primary => PrimaryDocument;
        IWritableDocument IExportDocumentSet.Primary => PrimaryDocument;

        public void AddCompanion(string reference, string text)
            => companions[reference] = new MemoryDocument(
                Path.GetFileName(reference),
                $"companion:{reference}",
                Encoding.UTF8.GetBytes(text));

        public MemoryDocument GetCompanion(string name) => companions[name];

        public ValueTask<IReadableDocument?> ResolveCompanionAsync(
            string relativeReference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            companions.TryGetValue(relativeReference, out MemoryDocument? document);
            return ValueTask.FromResult<IReadableDocument?>(document);
        }

        public ValueTask<IWritableDocument> CreateCompanionAsync(
            string safeRelativeName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = new MemoryDocument(safeRelativeName, $"export:{safeRelativeName}", []);
            companions[safeRelativeName] = document;
            return ValueTask.FromResult<IWritableDocument>(document);
        }

        public ValueTask<IReadableDocument?> ResolveExportedCompanionAsync(
            string safeRelativeName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            companions.TryGetValue(safeRelativeName, out MemoryDocument? document);
            return ValueTask.FromResult<IReadableDocument?>(document);
        }
    }

    private sealed class MemoryDocument(
        string displayName,
        string originIdentity,
        byte[] content) : IWritableDocument
    {
        private byte[] content = content.ToArray();

        public string DisplayName { get; } = displayName;
        public string? OriginIdentity { get; } = originIdentity;
        public byte[] Content => content.ToArray();
        public string Text => Encoding.UTF8.GetString(content);

        public void SetText(string value) => content = Encoding.UTF8.GetBytes(value);

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream(content, writable: false));
        }

        public ValueTask<Stream> OpenWriteAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new CommittingStream(bytes => content = bytes));
        }

        private sealed class CommittingStream(Action<byte[]> commit) : MemoryStream
        {
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    commit(ToArray());
                base.Dispose(disposing);
            }

            public override async ValueTask DisposeAsync()
            {
                commit(ToArray());
                await base.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
