namespace DvmConsole.Application;

public sealed record ConfigurationSummary(
    ConfigurationId Id,
    string Name,
    ConfigurationRevision CurrentRevision,
    DateTimeOffset ModifiedAt,
    bool IsActive,
    bool PendingReload,
    bool IsReadOnly,
    bool IsLegacyCandidate,
    string? LegacyOriginIdentity = null);

public sealed record LegacyConfigurationCandidate(
    string DisplayName,
    string OriginIdentity);

public sealed record ConfigurationDraft(
    ConfigurationId Id,
    ConfigurationRevision? BasedOnRevision,
    string Name,
    string Yaml,
    bool IsDirty,
    bool IsReadOnly,
    IReadOnlyList<string> Warnings);

public sealed record ConfigurationCommit(
    ConfigurationReference Reference,
    DateTimeOffset CommittedAt,
    IReadOnlyList<string> Warnings);

public sealed record ConfigurationImportResult(
    ConfigurationReference Reference,
    bool ReusedExisting,
    bool AppendedRevision,
    IReadOnlyList<string> Warnings);

public enum ConfigurationConflictResolution
{
    ImportAsNew,
    ReplaceExisting,
    Cancel
}

public sealed record ConfigurationImportOptions(
    ConfigurationConflictResolution ConflictResolution = ConfigurationConflictResolution.Cancel,
    ConfigurationId? ReplaceConfigurationId = null,
    bool ConfirmExternalCompanions = false);

public sealed record ConfigurationExportOptions(
    bool Sanitized,
    bool IncludeCompanions = true);

public sealed class ConfigurationDraftConflictException(ConfigurationDraft existingDraft)
    : InvalidOperationException("A dirty Configuration Studio draft is already open.")
{
    public ConfigurationDraft ExistingDraft { get; } = existingDraft;
}

public sealed class ConfigurationImportConflictException(
    ConfigurationId existingConfigurationId,
    string message) : InvalidOperationException(message)
{
    public ConfigurationId ExistingConfigurationId { get; } = existingConfigurationId;
}

public sealed class ConfigurationExternalCompanionsConfirmationRequiredException(
    IReadOnlyList<string> references)
    : InvalidOperationException(
        "One or more companion files are outside the imported document folder and require explicit confirmation.")
{
    public IReadOnlyList<string> References { get; } =
        Array.AsReadOnly((references ?? throw new ArgumentNullException(nameof(references))).ToArray());
}

public interface IConfigurationLibrary
{
    ValueTask RegisterLegacyCandidatesAsync(
        IReadOnlyList<LegacyConfigurationCandidate> candidates,
        CancellationToken cancellationToken = default);
    ValueTask<ConfigurationDraft> CreateDraftAsync(
        string name,
        CancellationToken cancellationToken = default);
    ValueTask<ConfigurationImportResult> ImportAsync(
        IImportDocumentSet source,
        ConfigurationImportOptions options,
        CancellationToken cancellationToken = default);
    ValueTask<ConfigurationDraft> OpenDraftAsync(
        ConfigurationId id,
        CancellationToken cancellationToken = default);
    ValueTask<ConfigurationDraft> StageDraftAsync(
        ConfigurationDraft draft,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> companions,
        CancellationToken cancellationToken = default);
    ValueTask<ConfigurationCommit> CommitAsync(
        ConfigurationDraft draft,
        CancellationToken cancellationToken = default);
    ValueTask DiscardDraftAsync(
        ConfigurationId id,
        CancellationToken cancellationToken = default);
    ValueTask<ConfigurationReference> DuplicateAsync(
        ConfigurationId id,
        string copyName,
        CancellationToken cancellationToken = default);
    ValueTask ExportAsync(
        ConfigurationReference configuration,
        IExportDocumentSet destination,
        ConfigurationExportOptions options,
        CancellationToken cancellationToken = default);
    ValueTask MoveToTrashAsync(
        ConfigurationId id,
        CancellationToken cancellationToken = default);
    ValueTask RestoreFromTrashAsync(
        ConfigurationId id,
        CancellationToken cancellationToken = default);
    IAsyncEnumerable<ConfigurationSummary> ListAsync(
        CancellationToken cancellationToken = default);
    IAsyncEnumerable<ConfigurationSummary> ListTrashAsync(
        CancellationToken cancellationToken = default);
}

public interface IActiveConfigurationService
{
    ConfigurationReference? Active { get; }
    ValueTask ActivateAsync(
        ConfigurationReference configuration,
        CancellationToken cancellationToken = default);
    ValueTask DeactivateAsync(CancellationToken cancellationToken = default);
    ValueTask ReloadAsync(CancellationToken cancellationToken = default);
}
