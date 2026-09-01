using System.Text.Json.Serialization;

namespace DvmConsole.Configuration.Yaml;

internal sealed class CatalogState
{
    public int SchemaVersion { get; set; } = 1;
    public List<CatalogEntryState> Entries { get; set; } = [];
    public List<CatalogEntryState> Trash { get; set; } = [];
    public ActiveConfigurationState? Active { get; set; }
}

internal sealed class CatalogEntryState
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid CurrentRevision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }
    public DateTimeOffset? LastOpenedAt { get; set; }
    public bool IsReadOnly { get; set; }
    public bool IsLegacyCandidate { get; set; }
    public string? OriginIdentity { get; set; }
    public string? OriginFingerprint { get; set; }
    public Guid? LastImportedRevision { get; set; }
}

internal sealed class ActiveConfigurationState
{
    public Guid Id { get; set; }
    public Guid Revision { get; set; }
}

internal sealed class CurrentRevisionState
{
    public Guid Revision { get; set; }
}

internal sealed class RevisionMetadataState
{
    public Guid Id { get; set; }
    public Guid Revision { get; set; }
    public Guid? ParentRevision { get; set; }
    public DateTimeOffset CommittedAt { get; set; }
    public bool Imported { get; set; }
    public string? OriginIdentity { get; set; }
    public string? OriginFingerprint { get; set; }
    public bool IsReadOnly { get; set; }
    public List<string> CompanionNames { get; set; } = [];
}

internal sealed class DraftState
{
    public Guid Id { get; set; }
    public Guid? BasedOnRevision { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsDirty { get; set; }
    public bool IsReadOnly { get; set; }
    public List<string> Warnings { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(CatalogState))]
[JsonSerializable(typeof(CurrentRevisionState))]
[JsonSerializable(typeof(RevisionMetadataState))]
[JsonSerializable(typeof(DraftState))]
internal sealed partial class ConfigurationLibraryJsonContext : JsonSerializerContext;
