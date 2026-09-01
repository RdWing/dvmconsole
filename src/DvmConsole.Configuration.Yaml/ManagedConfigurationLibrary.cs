using System.Security.Cryptography;
using System.Text;
using DvmConsole.Application;
using DvmConsole.Core.Configuration;

namespace DvmConsole.Configuration.Yaml;

public sealed class ManagedConfigurationLibrary : IConfigurationLibrary, IActiveConfigurationService
{
    private const string EmptyConfiguration = "systems: []\nzones: []\ngroups: []\n";
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string root;
    private readonly string catalogPath;
    private readonly IClock clock;
    private ConfigurationReference? activeConfiguration;

    public ManagedConfigurationLibrary(string rootPath, IClock? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        root = Path.GetFullPath(rootPath);
        catalogPath = Path.Combine(root, "catalog.json");
        this.clock = clock ?? SystemClock.Instance;
        EnsureLayout();
        if (File.Exists(catalogPath) || File.Exists(catalogPath + ".pending") || File.Exists(catalogPath + ".backup"))
        {
            AtomicLibraryFile.Recover(catalogPath, ConfigurationLibraryJsonContext.Default.CatalogState);
            CatalogState catalog = LoadCatalog();
            activeConfiguration = catalog.Active is null
                ? null
                : new ConfigurationReference(
                    new ConfigurationId(catalog.Active.Id),
                    new ConfigurationRevision(catalog.Active.Revision));
        }
        else
        {
            AtomicLibraryFile.Write(
                catalogPath,
                new CatalogState(),
                ConfigurationLibraryJsonContext.Default.CatalogState);
        }
    }

    public ConfigurationReference? Active
        => Volatile.Read(ref activeConfiguration);

    public async ValueTask RegisterLegacyCandidatesAsync(
        IReadOnlyList<LegacyConfigurationCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        await SwitchToWorkerAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CatalogState catalog = LoadCatalog();
            bool changed = false;
            foreach (LegacyConfigurationCandidate candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(candidate.DisplayName) ||
                    string.IsNullOrWhiteSpace(candidate.OriginIdentity))
                {
                    continue;
                }
                string origin = NormalizeOrigin(candidate.OriginIdentity)!;
                if (catalog.Entries.Any(entry =>
                        string.Equals(entry.OriginIdentity, origin, StringComparison.Ordinal)))
                {
                    continue;
                }

                CatalogEntryState entry = NewEntry(candidate.DisplayName, isReadOnly: false, origin);
                entry.IsLegacyCandidate = true;
                catalog.Entries.Add(entry);
                Directory.CreateDirectory(EntryRoot(entry.Id));
                changed = true;
            }
            if (changed)
                SaveCatalog(catalog);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<ConfigurationDraft> CreateDraftAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        await SwitchToWorkerAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureDraftCanBeReplaced();
            ClearDrafts();
            var state = new DraftState
            {
                Id = Guid.NewGuid(),
                Name = name.Trim(),
                IsDirty = true
            };
            WriteDraft(state, EmptyConfiguration, new Dictionary<string, byte[]>());
            return ToDraft(state, EmptyConfiguration);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<ConfigurationImportResult> ImportAsync(
        IImportDocumentSet source,
        ConfigurationImportOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        await SwitchToWorkerAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] primary = await ReadAllBytesAsync(source.Primary, cancellationToken).ConfigureAwait(false);
            string yaml = DecodeYaml(primary);
            ConfigurationDocument document = ParseAndValidate(yaml, source.Primary.DisplayName);
            (string managedYaml, Dictionary<string, byte[]> companions, List<string> warnings) =
                await PrepareImportBundleAsync(document, source, options, cancellationToken).ConfigureAwait(false);
            string fingerprint = ComputeFingerprint(primary, companions);
            string? origin = NormalizeOrigin(source.Primary.OriginIdentity);
            CatalogState catalog = LoadCatalog();
            CatalogEntryState? existing = origin is null
                ? null
                : catalog.Entries.FirstOrDefault(entry =>
                    string.Equals(entry.OriginIdentity, origin, StringComparison.Ordinal));
            CatalogEntryState? requestedReplacement = null;
            if (options.ConflictResolution == ConfigurationConflictResolution.ReplaceExisting)
            {
                if (options.ReplaceConfigurationId is not ConfigurationId replacementId)
                {
                    throw new ArgumentException(
                        "Replacing a configuration requires an explicit target configuration ID.",
                        nameof(options));
                }

                requestedReplacement = catalog.Entries.FirstOrDefault(entry =>
                    entry.Id == replacementId.Value) ??
                    throw new KeyNotFoundException(
                        $"Configuration '{replacementId}' is not available for replacement.");
                if (existing is not null && !ReferenceEquals(existing, requestedReplacement))
                {
                    throw new InvalidOperationException(
                        "The imported origin already belongs to another managed configuration.");
                }
            }

            if (existing is not null &&
                string.Equals(existing.OriginFingerprint, fingerprint, StringComparison.Ordinal))
            {
                return new ConfigurationImportResult(
                    ToReference(existing),
                    ReusedExisting: true,
                    AppendedRevision: false,
                    warnings);
            }

            bool append = false;
            CatalogEntryState target;
            if (requestedReplacement is not null)
            {
                target = requestedReplacement;
                target.IsLegacyCandidate = false;
                append = target.CurrentRevision != Guid.Empty;
            }
            else if (existing is null)
            {
                target = NewEntry(source.Primary.DisplayName, document.IsReadOnly, origin);
            }
            else if (existing.IsLegacyCandidate)
            {
                target = existing;
                target.IsLegacyCandidate = false;
            }
            else if (existing.LastImportedRevision == existing.CurrentRevision)
            {
                target = existing;
                append = true;
            }
            else
            {
                switch (options.ConflictResolution)
                {
                    case ConfigurationConflictResolution.ImportAsNew:
                        target = NewEntry(source.Primary.DisplayName, document.IsReadOnly, origin);
                        break;
                    default:
                        throw new ConfigurationImportConflictException(
                            new ConfigurationId(existing.Id),
                            "The legacy source and its managed configuration both changed. Choose Import as New, Replace Existing, or Cancel.");
                }
            }

            ConfigurationCommit commit = CommitRevision(
                catalog,
                target,
                managedYaml,
                companions,
                imported: true,
                origin,
                fingerprint,
                document.IsReadOnly,
                warnings);
            return new ConfigurationImportResult(
                commit.Reference,
                ReusedExisting: false,
                AppendedRevision: append,
                warnings);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<ConfigurationDraft> OpenDraftAsync(
        ConfigurationId id,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        await SwitchToWorkerAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DraftState? activeDraft = TryReadDraftState();
            if (activeDraft is not null && activeDraft.Id == id.Value)
                return ReadDraft(activeDraft);
            EnsureDraftCanBeReplaced();
            ClearDrafts();

            CatalogEntryState entry = GetEntry(LoadCatalog(), id);
            string revisionRoot = RevisionRoot(entry.Id, entry.CurrentRevision);
            string yaml = File.ReadAllText(Path.Combine(revisionRoot, "codeplug.yml"));
            RevisionMetadataState metadata = ReadRevisionMetadata(revisionRoot);
            var state = new DraftState
            {
                Id = entry.Id,
                BasedOnRevision = entry.CurrentRevision,
                Name = entry.Name,
                IsDirty = false,
                IsReadOnly = entry.IsReadOnly
            };
            Dictionary<string, byte[]> companions = ReadCompanions(revisionRoot, metadata.CompanionNames);
            WriteDraft(state, yaml, companions);
            return ToDraft(state, yaml);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<ConfigurationDraft> StageDraftAsync(
        ConfigurationDraft draft,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> companions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(companions);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        await SwitchToWorkerAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DraftState? activeDraft = TryReadDraftState();
            if (activeDraft is null || activeDraft.Id != draft.Id.Value)
                throw new InvalidOperationException("The draft is no longer the active Configuration Studio draft.");
            if (activeDraft.IsReadOnly && draft.IsDirty)
                throw new InvalidOperationException("This configuration uses YAML constructs that cannot safely be rewritten.");

            ConfigurationDocument document = ParseAndValidate(draft.Yaml, draft.Name);
            activeDraft.Name = NormalizeDisplayName(draft.Name);
            activeDraft.IsDirty = draft.IsDirty;
            activeDraft.IsReadOnly = document.IsReadOnly;
            activeDraft.Warnings = draft.Warnings.ToList();
            Dictionary<string, byte[]> stagedCompanions = ReadDraftCompanions(activeDraft.Id);
            foreach ((string name, ReadOnlyMemory<byte> content) in companions)
                stagedCompanions[EnsureSafeCompanionName(name)] = content.ToArray();
            WriteDraft(activeDraft, draft.Yaml, stagedCompanions);
            return ToDraft(activeDraft, draft.Yaml);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<ConfigurationCommit> CommitAsync(
        ConfigurationDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        await SwitchToWorkerAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DraftState? activeDraft = TryReadDraftState();
            if (activeDraft is null || activeDraft.Id != draft.Id.Value)
                throw new InvalidOperationException("The draft is no longer the active Configuration Studio draft.");
            if (activeDraft.IsReadOnly && draft.IsDirty)
                throw new InvalidOperationException("This configuration uses YAML constructs that cannot safely be rewritten.");

            ConfigurationDocument document = ParseAndValidate(draft.Yaml, draft.Name);
            string yaml = document.IsReadOnly ? document.SourceText : document.Serialize();
            CatalogState catalog = LoadCatalog();
            CatalogEntryState? existing = catalog.Entries.FirstOrDefault(entry => entry.Id == draft.Id.Value);
            CatalogEntryState target = existing ?? NewEntry(draft.Name, document.IsReadOnly, origin: null, draft.Id.Value);
            target.Name = draft.Name.Trim();
            Dictionary<string, byte[]> companions = ReadDraftCompanions(activeDraft.Id);
            ConfigurationCommit commit = CommitRevision(
                catalog,
                target,
                yaml,
                companions,
                imported: false,
                origin: target.OriginIdentity,
                fingerprint: target.OriginFingerprint,
                document.IsReadOnly,
                draft.Warnings);
            ClearDrafts();
            return commit;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DiscardDraftAsync(
        ConfigurationId id,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        await SwitchToWorkerAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DraftState? state = TryReadDraftState();
            if (state is not null && state.Id == id.Value)
                ClearDrafts();
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<ConfigurationReference> DuplicateAsync(
        ConfigurationId id,
        string copyName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(copyName);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        await SwitchToWorkerAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CatalogState catalog = LoadCatalog();
            CatalogEntryState source = GetEntry(catalog, id);
            string sourceRoot = RevisionRoot(source.Id, source.CurrentRevision);
            RevisionMetadataState sourceMetadata = ReadRevisionMetadata(sourceRoot);
            string yaml = File.ReadAllText(Path.Combine(sourceRoot, "codeplug.yml"));
            yaml = ConfigurationCopyPolicy.RemoveTrustScopedWebAuthorization(yaml);

            CatalogEntryState target = NewEntry(copyName, source.IsReadOnly, origin: null);
            ConfigurationCommit commit = CommitRevision(
                catalog,
                target,
                yaml,
                ReadCompanions(sourceRoot, sourceMetadata.CompanionNames),
                imported: false,
                origin: null,
                fingerprint: null,
                source.IsReadOnly,
                []);
            return commit.Reference;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask ExportAsync(
        ConfigurationReference configuration,
        IExportDocumentSet destination,
        ConfigurationExportOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(options);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        await SwitchToWorkerAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CatalogState catalog = LoadCatalog();
            CatalogEntryState entry = GetEntry(catalog, configuration.Id);
            EnsureRevisionExists(entry.Id, configuration.Revision.Value);
            string revisionRoot = RevisionRoot(entry.Id, configuration.Revision.Value);
            RevisionMetadataState metadata = ReadRevisionMetadata(revisionRoot);
            string storedYaml = File.ReadAllText(Path.Combine(revisionRoot, "codeplug.yml"));
            ConfigurationDocument document = ConfigurationDocument.Parse(storedYaml);
            string exportYaml;
            if (options.Sanitized)
            {
                exportYaml = document.SerializeSanitized();
            }
            else if (document.IsReadOnly)
            {
                exportYaml = document.SourceText;
            }
            else
            {
                RewriteCompanionReferencesForExport(document);
                document.MarkDirty();
                exportYaml = document.Serialize();
            }

            await WriteTextAsync(destination.Primary, exportYaml, cancellationToken).ConfigureAwait(false);
            if (!options.Sanitized && options.IncludeCompanions)
            {
                foreach (string companionName in metadata.CompanionNames)
                {
                    IWritableDocument target = await destination
                        .CreateCompanionAsync(companionName, cancellationToken)
                        .ConfigureAwait(false);
                    byte[] content = File.ReadAllBytes(Path.Combine(revisionRoot, "companions", companionName));
                    await WriteBytesAsync(target, content, cancellationToken).ConfigureAwait(false);
                }
            }

            await ValidateExportReadbackAsync(destination, options, metadata.CompanionNames, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask MoveToTrashAsync(
        ConfigurationId id,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        await SwitchToWorkerAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CatalogState catalog = LoadCatalog();
            if (catalog.Active?.Id == id.Value)
                throw new InvalidOperationException("The active configuration cannot be removed.");
            CatalogEntryState entry = GetEntry(catalog, id);
            string source = EntryRoot(entry.Id);
            string target = TrashRoot(entry.Id);
            if (Directory.Exists(target))
                throw new IOException("A recoverable trash entry already exists for this configuration.");
            Directory.Move(source, target);
            catalog.Entries.Remove(entry);
            catalog.Trash.Add(entry);
            try
            {
                SaveCatalog(catalog);
            }
            catch
            {
                Directory.Move(target, source);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask RestoreFromTrashAsync(
        ConfigurationId id,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        await SwitchToWorkerAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CatalogState catalog = LoadCatalog();
            CatalogEntryState entry = catalog.Trash.FirstOrDefault(candidate => candidate.Id == id.Value)
                ?? throw new KeyNotFoundException($"Configuration '{id}' is not in trash.");
            string source = TrashRoot(entry.Id);
            string target = EntryRoot(entry.Id);
            if (Directory.Exists(target))
                throw new IOException("A managed configuration with this ID already exists.");
            Directory.Move(source, target);
            catalog.Trash.Remove(entry);
            catalog.Entries.Add(entry);
            try
            {
                SaveCatalog(catalog);
            }
            catch
            {
                Directory.Move(target, source);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async IAsyncEnumerable<ConfigurationSummary> ListAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        await SwitchToWorkerAsync(cancellationToken).ConfigureAwait(false);
        ConfigurationSummary[] snapshot;
        try
        {
            CatalogState catalog = LoadCatalog();
            snapshot = catalog.Entries
                .OrderByDescending(entry => entry.ModifiedAt)
                .Select(entry => ToSummary(catalog, entry))
                .ToArray();
        }
        finally
        {
            gate.Release();
        }

        foreach (ConfigurationSummary entry in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry;
        }
    }

    public async IAsyncEnumerable<ConfigurationSummary> ListTrashAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        await SwitchToWorkerAsync(cancellationToken).ConfigureAwait(false);
        ConfigurationSummary[] snapshot;
        try
        {
            CatalogState catalog = LoadCatalog();
            snapshot = catalog.Trash
                .OrderByDescending(entry => entry.ModifiedAt)
                .Select(entry => ToTrashSummary(entry))
                .ToArray();
        }
        finally
        {
            gate.Release();
        }

        foreach (ConfigurationSummary entry in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry;
        }
    }

    public async ValueTask ActivateAsync(
        ConfigurationReference configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        await SwitchToWorkerAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CatalogState catalog = LoadCatalog();
            CatalogEntryState entry = GetEntry(catalog, configuration.Id);
            EnsureRevisionExists(entry.Id, configuration.Revision.Value);
            catalog.Active = new ActiveConfigurationState
            {
                Id = configuration.Id.Value,
                Revision = configuration.Revision.Value
            };
            entry.LastOpenedAt = clock.UtcNow;
            SaveCatalog(catalog);
            Volatile.Write(ref activeConfiguration, configuration);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask ReloadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        await SwitchToWorkerAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CatalogState catalog = LoadCatalog();
            if (catalog.Active is null)
                return;
            CatalogEntryState entry = catalog.Entries.FirstOrDefault(candidate => candidate.Id == catalog.Active.Id)
                ?? throw new InvalidDataException("The active configuration is missing from the managed catalog.");
            catalog.Active.Revision = entry.CurrentRevision;
            SaveCatalog(catalog);
            Volatile.Write(
                ref activeConfiguration,
                new ConfigurationReference(
                    new ConfigurationId(catalog.Active.Id),
                    new ConfigurationRevision(catalog.Active.Revision)));
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DeactivateAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        await SwitchToWorkerAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CatalogState catalog = LoadCatalog();
            catalog.Active = null;
            SaveCatalog(catalog);
            Volatile.Write(ref activeConfiguration, null);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async ValueTask SwitchToWorkerAsync(CancellationToken cancellationToken)
    {
        // A library operation can perform substantial catalog, YAML, and
        // companion-file work. Always schedule that work on the default task
        // scheduler instead of inheriting an Avalonia or test synchronization
        // context. Task.Run is intentional here: ForceYielding can still depend
        // on the caller's constrained execution context and deadlock a legacy
        // synchronous startup bridge.
        await Task.Run(static () => { }, cancellationToken).ConfigureAwait(false);
    }

    private ConfigurationCommit CommitRevision(
        CatalogState catalog,
        CatalogEntryState entry,
        string yaml,
        IReadOnlyDictionary<string, byte[]> companions,
        bool imported,
        string? origin,
        string? fingerprint,
        bool isReadOnly,
        IReadOnlyList<string> warnings)
    {
        Guid? parent = entry.CurrentRevision == Guid.Empty ? null : entry.CurrentRevision;
        Guid revision = Guid.NewGuid();
        DateTimeOffset committedAt = clock.UtcNow;
        string revisionRoot = RevisionRoot(entry.Id, revision);
        Directory.CreateDirectory(Path.Combine(revisionRoot, "companions"));
        File.WriteAllText(Path.Combine(revisionRoot, "codeplug.yml"), yaml, new UTF8Encoding(false));
        foreach ((string name, byte[] content) in companions)
            File.WriteAllBytes(Path.Combine(revisionRoot, "companions", EnsureSafeCompanionName(name)), content);

        var metadata = new RevisionMetadataState
        {
            Id = entry.Id,
            Revision = revision,
            ParentRevision = parent,
            CommittedAt = committedAt,
            Imported = imported,
            OriginIdentity = imported ? origin : null,
            OriginFingerprint = imported ? fingerprint : null,
            IsReadOnly = isReadOnly,
            CompanionNames = companions.Keys.Order(StringComparer.Ordinal).ToList()
        };
        AtomicLibraryFile.Write(
            Path.Combine(revisionRoot, "revision.json"),
            metadata,
            ConfigurationLibraryJsonContext.Default.RevisionMetadataState);
        AtomicLibraryFile.Write(
            Path.Combine(EntryRoot(entry.Id), "current.json"),
            new CurrentRevisionState { Revision = revision },
            ConfigurationLibraryJsonContext.Default.CurrentRevisionState);

        bool isNew = !catalog.Entries.Contains(entry);
        if (isNew)
            catalog.Entries.Add(entry);
        entry.CurrentRevision = revision;
        entry.ModifiedAt = committedAt;
        entry.IsReadOnly = isReadOnly;
        if (imported)
        {
            entry.IsLegacyCandidate = false;
            entry.OriginIdentity = origin;
            entry.OriginFingerprint = fingerprint;
            entry.LastImportedRevision = revision;
        }
        SaveCatalog(catalog);
        return new ConfigurationCommit(
            new ConfigurationReference(new ConfigurationId(entry.Id), new ConfigurationRevision(revision)),
            committedAt,
            warnings.ToArray());
    }

    private async ValueTask<(string Yaml, Dictionary<string, byte[]> Companions, List<string> Warnings)>
        PrepareImportBundleAsync(
            ConfigurationDocument document,
            IImportDocumentSet source,
            ConfigurationImportOptions options,
            CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var companions = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var rewrites = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] references = EnumerateCompanionReferences(document.Configuration)
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string[] externalReferences = references
            .Where(reference => !IsSafeRelativeReference(reference))
            .ToArray();
        if (externalReferences.Length > 0 && !options.ConfirmExternalCompanions)
        {
            throw new ConfigurationExternalCompanionsConfirmationRequiredException(
                externalReferences);
        }
        if (externalReferences.Length > 0 && document.IsReadOnly)
        {
            throw new InvalidDataException(
                "This read-only YAML uses external companion paths that cannot be safely rewritten into the managed library. " +
                "Replace the external references with relative paths before importing it.");
        }

        foreach (string reference in references)
        {
            IReadableDocument? companion = await source.ResolveCompanionAsync(reference, cancellationToken)
                .ConfigureAwait(false);
            if (companion is null)
            {
                warnings.Add($"Companion '{reference}' was not found; existing missing-file behavior is preserved.");
                continue;
            }

            byte[] content = await ReadAllBytesAsync(companion, cancellationToken).ConfigureAwait(false);
            string name = AllocateCompanionName(reference, content, companions);
            companions[name] = content;
            rewrites[reference] = $"companions/{name}";
        }

        string yaml = document.SourceText;
        if (rewrites.Count > 0)
        {
            if (document.IsReadOnly)
            {
                warnings.Add("Unsafe YAML remains read-only; companion references could not be rewritten automatically.");
            }
            else
            {
                RewriteCompanionReferences(document.Configuration, rewrites);
                document.MarkDirty();
                yaml = document.Serialize();
            }
        }
        return (yaml, companions, warnings);
    }

    private static ConfigurationDocument ParseAndValidate(string yaml, string displayName)
    {
        ConfigurationDocument document;
        try
        {
            document = ConfigurationDocument.Parse(yaml);
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException or YamlDotNet.Core.YamlException)
        {
            throw new InvalidDataException($"Configuration '{displayName}' could not be parsed: {exception.Message}", exception);
        }

        ConfigurationValidationIssue? error = document.Validate().FirstOrDefault(issue => issue.IsError);
        if (error is not null)
            throw new InvalidDataException($"Configuration '{displayName}' is invalid: {error.Message}");
        return document;
    }

    private async ValueTask ValidateExportReadbackAsync(
        IExportDocumentSet destination,
        ConfigurationExportOptions options,
        IReadOnlyList<string> companionNames,
        CancellationToken cancellationToken)
    {
        byte[] primary = await ReadAllBytesAsync(destination.Primary, cancellationToken).ConfigureAwait(false);
        _ = ParseAndValidate(DecodeYaml(primary), destination.Primary.DisplayName);
        if (options.Sanitized || !options.IncludeCompanions)
            return;
        foreach (string name in companionNames)
        {
            IReadableDocument? companion = await destination.ResolveExportedCompanionAsync(name, cancellationToken)
                .ConfigureAwait(false);
            if (companion is null)
                throw new IOException($"Exported companion '{name}' could not be read back.");
            _ = await ReadAllBytesAsync(companion, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void RewriteCompanionReferencesForExport(ConfigurationDocument document)
    {
        ConsoleConfiguration configuration = document.Configuration;
        if (!string.IsNullOrWhiteSpace(configuration.KeyFile))
            configuration.KeyFile = ExportReference(configuration.KeyFile);
        foreach (SystemConfiguration system in configuration.Systems)
            system.AliasPath = ExportReference(system.AliasPath);
    }

    private static string ExportReference(string reference)
    {
        const string prefix = "companions/";
        return reference.StartsWith(prefix, StringComparison.Ordinal)
            ? "./" + reference[prefix.Length..]
            : reference;
    }

    private static void RewriteCompanionReferences(
        ConsoleConfiguration configuration,
        IReadOnlyDictionary<string, string> rewrites)
    {
        if (!string.IsNullOrWhiteSpace(configuration.KeyFile) &&
            rewrites.TryGetValue(configuration.KeyFile, out string? keyFile))
        {
            configuration.KeyFile = keyFile;
        }
        foreach (SystemConfiguration system in configuration.Systems)
        {
            if (rewrites.TryGetValue(system.AliasPath, out string? aliasPath))
                system.AliasPath = aliasPath;
        }
    }

    private static IEnumerable<string> EnumerateCompanionReferences(ConsoleConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(configuration.KeyFile))
            yield return configuration.KeyFile;
        foreach (SystemConfiguration system in configuration.Systems)
        {
            if (!string.IsNullOrWhiteSpace(system.AliasPath))
                yield return system.AliasPath;
        }
    }

    private static string AllocateCompanionName(
        string reference,
        byte[] content,
        IReadOnlyDictionary<string, byte[]> existing)
    {
        string normalized = reference.Replace('\\', '/');
        string candidate = SanitizeFileName(normalized[(normalized.LastIndexOf('/') + 1)..]);
        if (candidate.Length == 0)
            candidate = "companion";
        if (!existing.ContainsKey(candidate))
            return candidate;
        string extension = Path.GetExtension(candidate);
        string stem = Path.GetFileNameWithoutExtension(candidate);
        string suffix = Convert.ToHexString(SHA256.HashData(content))[..8].ToLowerInvariant();
        return $"{stem}-{suffix}{extension}";
    }

    private static string SanitizeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value.Where(character => !invalid.Contains(character) && character is not '/' and not '\\').ToArray());
    }

    private static bool IsSafeRelativeReference(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || Path.IsPathRooted(reference))
            return false;
        string normalized = reference.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments.All(segment => segment != "..");
    }

    private static string EnsureSafeCompanionName(string name)
    {
        string safe = SanitizeFileName(name);
        if (safe.Length == 0 || !string.Equals(safe, name, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsafe managed companion name '{name}'.");
        return safe;
    }

    private static string ComputeFingerprint(
        byte[] primary,
        IReadOnlyDictionary<string, byte[]> companions)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(primary);
        foreach ((string name, byte[] content) in companions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(name));
            hash.AppendData(SHA256.HashData(content));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string? NormalizeOrigin(string? origin)
        => string.IsNullOrWhiteSpace(origin) ? null : origin.Trim();

    private CatalogEntryState NewEntry(
        string name,
        bool isReadOnly,
        string? origin,
        Guid? id = null)
    {
        DateTimeOffset now = clock.UtcNow;
        return new CatalogEntryState
        {
            Id = id ?? Guid.NewGuid(),
            Name = NormalizeDisplayName(name),
            CreatedAt = now,
            ModifiedAt = now,
            IsReadOnly = isReadOnly,
            OriginIdentity = origin
        };
    }

    private static string NormalizeDisplayName(string displayName)
    {
        string name = Path.GetFileNameWithoutExtension(displayName)?.Trim() ?? string.Empty;
        return name.Length == 0 ? "Configuration" : name;
    }

    private void EnsureDraftCanBeReplaced()
    {
        DraftState? active = TryReadDraftState();
        if (active is not null && active.IsDirty)
            throw new ConfigurationDraftConflictException(ReadDraft(active));
    }

    private ConfigurationDraft ReadDraft(DraftState state)
        => ToDraft(state, File.ReadAllText(Path.Combine(DraftRoot(state.Id), "codeplug.yml")));

    private static ConfigurationDraft ToDraft(DraftState state, string yaml)
        => new(
            new ConfigurationId(state.Id),
            state.BasedOnRevision is Guid revision ? new ConfigurationRevision(revision) : null,
            state.Name,
            yaml,
            state.IsDirty,
            state.IsReadOnly,
            state.Warnings.ToArray());

    private void WriteDraft(
        DraftState state,
        string yaml,
        IReadOnlyDictionary<string, byte[]> companions)
    {
        string draftRoot = DraftRoot(state.Id);
        Directory.CreateDirectory(Path.Combine(draftRoot, "companions"));
        File.WriteAllText(Path.Combine(draftRoot, "codeplug.yml"), yaml, new UTF8Encoding(false));
        foreach ((string name, byte[] content) in companions)
            File.WriteAllBytes(Path.Combine(draftRoot, "companions", EnsureSafeCompanionName(name)), content);
        AtomicLibraryFile.Write(
            DraftStatePath,
            state,
            ConfigurationLibraryJsonContext.Default.DraftState);
    }

    private DraftState? TryReadDraftState()
    {
        if (!File.Exists(DraftStatePath))
            return null;
        AtomicLibraryFile.Recover(DraftStatePath, ConfigurationLibraryJsonContext.Default.DraftState);
        return AtomicLibraryFile.Read(DraftStatePath, ConfigurationLibraryJsonContext.Default.DraftState);
    }

    private Dictionary<string, byte[]> ReadDraftCompanions(Guid id)
    {
        string directory = Path.Combine(DraftRoot(id), "companions");
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory)
                .ToDictionary(
                    path => Path.GetFileName(path),
                    File.ReadAllBytes,
                    StringComparer.OrdinalIgnoreCase)
            : [];
    }

    private static Dictionary<string, byte[]> ReadCompanions(
        string revisionRoot,
        IEnumerable<string> companionNames)
        => companionNames.ToDictionary(
            name => name,
            name => File.ReadAllBytes(Path.Combine(revisionRoot, "companions", EnsureSafeCompanionName(name))),
            StringComparer.OrdinalIgnoreCase);

    private void ClearDrafts()
    {
        if (Directory.Exists(DraftsRoot))
        {
            foreach (string directory in Directory.EnumerateDirectories(DraftsRoot))
                Directory.Delete(directory, recursive: true);
            foreach (string file in Directory.EnumerateFiles(DraftsRoot))
                File.Delete(file);
        }
        Directory.CreateDirectory(DraftsRoot);
    }

    private CatalogState LoadCatalog()
    {
        AtomicLibraryFile.Recover(catalogPath, ConfigurationLibraryJsonContext.Default.CatalogState);
        CatalogState catalog = AtomicLibraryFile.Read(catalogPath, ConfigurationLibraryJsonContext.Default.CatalogState);
        if (catalog.SchemaVersion != 1)
            throw new InvalidDataException($"Unsupported Configuration Library schema {catalog.SchemaVersion}.");
        return catalog;
    }

    private void SaveCatalog(CatalogState catalog)
        => AtomicLibraryFile.Write(catalogPath, catalog, ConfigurationLibraryJsonContext.Default.CatalogState);

    private static CatalogEntryState GetEntry(CatalogState catalog, ConfigurationId id)
        => catalog.Entries.FirstOrDefault(entry => entry.Id == id.Value)
            ?? throw new KeyNotFoundException($"Unknown configuration ID '{id}'.");

    private static ConfigurationReference ToReference(CatalogEntryState entry)
        => new(new ConfigurationId(entry.Id), new ConfigurationRevision(entry.CurrentRevision));

    private static ConfigurationSummary ToSummary(CatalogState catalog, CatalogEntryState entry)
    {
        bool active = catalog.Active?.Id == entry.Id;
        return new ConfigurationSummary(
            new ConfigurationId(entry.Id),
            entry.Name,
            new ConfigurationRevision(entry.CurrentRevision),
            entry.ModifiedAt,
            active,
            active && catalog.Active!.Revision != entry.CurrentRevision,
            entry.IsReadOnly,
            entry.IsLegacyCandidate,
            entry.IsLegacyCandidate ? entry.OriginIdentity : null,
            entry.LastOpenedAt);
    }

    private static ConfigurationSummary ToTrashSummary(CatalogEntryState entry)
        => new(
            new ConfigurationId(entry.Id),
            entry.Name,
            new ConfigurationRevision(entry.CurrentRevision),
            entry.ModifiedAt,
            IsActive: false,
            PendingReload: false,
            entry.IsReadOnly,
            entry.IsLegacyCandidate,
            entry.IsLegacyCandidate ? entry.OriginIdentity : null);

    private RevisionMetadataState ReadRevisionMetadata(string revisionRoot)
    {
        string path = Path.Combine(revisionRoot, "revision.json");
        AtomicLibraryFile.Recover(path, ConfigurationLibraryJsonContext.Default.RevisionMetadataState);
        return AtomicLibraryFile.Read(path, ConfigurationLibraryJsonContext.Default.RevisionMetadataState);
    }

    private void EnsureRevisionExists(Guid id, Guid revision)
    {
        if (!Directory.Exists(RevisionRoot(id, revision)))
            throw new KeyNotFoundException($"Unknown configuration revision '{revision:N}'.");
    }

    private void EnsureLayout()
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "entries"));
        Directory.CreateDirectory(DraftsRoot);
        Directory.CreateDirectory(Path.Combine(root, "trash"));
    }

    private static async ValueTask<byte[]> ReadAllBytesAsync(
        IReadableDocument document,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await document.OpenReadAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static async ValueTask WriteTextAsync(
        IWritableDocument document,
        string content,
        CancellationToken cancellationToken)
        => await WriteBytesAsync(document, Encoding.UTF8.GetBytes(content), cancellationToken).ConfigureAwait(false);

    private static async ValueTask WriteBytesAsync(
        IWritableDocument document,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await document.OpenWriteAsync(cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string DecodeYaml(byte[] content)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(content);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Configuration YAML must be UTF-8.", exception);
        }
    }

    private string EntryRoot(Guid id) => Path.Combine(root, "entries", id.ToString("N"));
    private string TrashRoot(Guid id) => Path.Combine(root, "trash", id.ToString("N"));
    private string RevisionRoot(Guid id, Guid revision)
        => Path.Combine(EntryRoot(id), "revisions", revision.ToString("N"));
    private string DraftRoot(Guid id) => Path.Combine(DraftsRoot, id.ToString("N"));
    private string DraftsRoot => Path.Combine(root, "drafts");
    private string DraftStatePath => Path.Combine(DraftsRoot, "active.json");
}
