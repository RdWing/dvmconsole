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
            zones: []
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
