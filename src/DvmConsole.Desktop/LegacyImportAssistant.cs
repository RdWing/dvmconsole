// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using DvmConsole.Application;
using DvmConsole.Configuration.Yaml;
using DvmConsole.Core.Settings;
using DvmConsole.Storage;

namespace DvmConsole.Desktop;

internal sealed record LegacyConfigurationImportCandidate(
    string Id,
    string Name,
    string CodeplugPath);

internal sealed record LegacyImportDiscovery(
    bool HasSettings,
    IReadOnlyList<string> Profiles,
    IReadOnlyList<LegacyConfigurationImportCandidate> Configurations)
{
    public bool HasCandidates => HasSettings || Profiles.Count > 0 || Configurations.Count > 0;
}

internal sealed record LegacyImportSelection(
    bool ImportSettings,
    IReadOnlyCollection<string> Profiles,
    IReadOnlyCollection<string> ConfigurationIds);

internal sealed record LegacyImportMarker(
    int SchemaVersion,
    string Decision,
    DateTimeOffset CompletedAt);

internal sealed class LegacyImportAssistant
{
    internal const string MarkerFileName = ".legacy-import-v1.json";
    private readonly string newRoot;
    private readonly string oldRoot;
    private readonly string markerPath;

    public LegacyImportAssistant(string newApplicationDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newApplicationDataRoot);
        newRoot = Path.GetFullPath(newApplicationDataRoot);
        string parent = Path.GetDirectoryName(newRoot) ?? throw new ArgumentException(
            "The application data root requires a parent directory.",
            nameof(newApplicationDataRoot));
        oldRoot = Path.Combine(parent, "dvmconsole");
        markerPath = Path.Combine(newRoot, MarkerFileName);
    }

    public bool ShouldOfferImport()
        => !File.Exists(markerPath) && IsPristine(newRoot) && Directory.Exists(oldRoot);

    public LegacyImportDiscovery Discover()
    {
        if (!ShouldOfferImport())
            return new LegacyImportDiscovery(false, [], []);

        string settingsPath = Path.Combine(oldRoot, "UserSettings.json");
        bool hasSettings = TryReadSettings(settingsPath);
        IReadOnlyList<string> profiles = DiscoverProfiles();
        IReadOnlyList<LegacyConfigurationImportCandidate> configurations = DiscoverConfigurations();
        return new LegacyImportDiscovery(hasSettings, profiles, configurations);
    }

    public async Task ImportAsync(
        LegacyImportDiscovery discovery,
        LegacyImportSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(selection);
        if (!ShouldOfferImport())
            throw new InvalidOperationException("The legacy import is no longer available for this store.");

        string parent = Path.GetDirectoryName(newRoot)!;
        string stagingRoot = Path.Combine(parent, ".dvmconsole-neo-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingRoot);
        try
        {
            var stagedSettings = new UserSettingsStore(Path.Combine(stagingRoot, "UserSettings.json"));
            if (selection.ImportSettings && discovery.HasSettings)
            {
                SettingsImportStage settings = new UserSettingsStore(
                        Path.Combine(oldRoot, "UserSettings.json"))
                    .StageImport(Path.Combine(oldRoot, "UserSettings.json"));
                await ImportReferencedAssetsAsync(settings.Settings, stagingRoot, cancellationToken)
                    .ConfigureAwait(false);
                stagedSettings.Import(settings, SettingsImportScope.All);
            }

            HashSet<string> availableProfiles = discovery.Profiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var oldSettings = new UserSettingsStore(Path.Combine(oldRoot, "UserSettings.json"));
            foreach (string profile in selection.Profiles.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!availableProfiles.Contains(profile))
                    throw new InvalidOperationException($"Legacy settings profile '{profile}' is no longer available.");
                stagedSettings.SaveNamedProfile(profile, oldSettings.LoadNamedProfile(profile));
            }

            HashSet<string> selectedConfigurations = selection.ConfigurationIds
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (selectedConfigurations.Count > 0)
            {
                var stagedLibrary = new ManagedConfigurationLibrary(
                    Path.Combine(stagingRoot, "ConfigurationLibrary"));
                foreach (LegacyConfigurationImportCandidate candidate in discovery.Configurations)
                {
                    if (!selectedConfigurations.Remove(candidate.Id))
                        continue;
                    cancellationToken.ThrowIfCancellationRequested();
                    await stagedLibrary.ImportAsync(
                        new LegacyManagedConfigurationDocumentSet(candidate.CodeplugPath),
                        new ConfigurationImportOptions(
                            ConfigurationConflictResolution.ImportAsNew,
                            ConfirmExternalCompanions: false),
                        cancellationToken).ConfigureAwait(false);
                }
                if (selectedConfigurations.Count > 0)
                    throw new InvalidOperationException("One or more selected legacy configurations are no longer available.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            CommitStagedStore(stagingRoot);
            WriteMarker("imported");
        }
        catch
        {
            if (Directory.Exists(stagingRoot))
                Directory.Delete(stagingRoot, recursive: true);
            throw;
        }
    }

    public void Decline()
    {
        if (File.Exists(markerPath))
            return;
        WriteMarker("declined");
    }

    private static bool IsPristine(string root)
    {
        if (!Directory.Exists(root))
            return true;
        return !Directory.EnumerateFileSystemEntries(root)
            .Any(path => !Path.GetFileName(path).Equals(MarkerFileName, StringComparison.Ordinal));
    }

    private static bool TryReadSettings(string path)
    {
        if (!File.Exists(path))
            return false;
        try
        {
            _ = new UserSettingsStore(path).StageImport(path);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or JsonException or
                UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private IReadOnlyList<string> DiscoverProfiles()
    {
        string profilesRoot = Path.Combine(oldRoot, "Profiles");
        if (!Directory.Exists(profilesRoot))
            return [];
        var oldSettings = new UserSettingsStore(Path.Combine(oldRoot, "UserSettings.json"));
        return oldSettings.ListNamedProfiles()
            .Where(profile =>
            {
                try
                {
                    _ = oldSettings.StageNamedProfile(profile);
                    return true;
                }
                catch (Exception exception) when (
                    exception is IOException or InvalidDataException or JsonException or
                        UnauthorizedAccessException or NotSupportedException)
                {
                    return false;
                }
            })
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IReadOnlyList<LegacyConfigurationImportCandidate> DiscoverConfigurations()
    {
        string libraryRoot = Path.Combine(oldRoot, "ConfigurationLibrary");
        string catalogPath = Path.Combine(libraryRoot, "catalog.json");
        if (!File.Exists(catalogPath))
            return [];
        try
        {
            using JsonDocument catalog = JsonDocument.Parse(File.ReadAllText(catalogPath));
            if (!catalog.RootElement.TryGetProperty("Entries", out JsonElement entries) &&
                !catalog.RootElement.TryGetProperty("entries", out entries))
            {
                return [];
            }
            var candidates = new List<LegacyConfigurationImportCandidate>();
            foreach (JsonElement entry in entries.EnumerateArray())
            {
                if (!TryGetGuid(entry, "Id", out Guid id) ||
                    !TryGetGuid(entry, "CurrentRevision", out Guid revision) ||
                    !TryGetString(entry, "Name", out string? name))
                {
                    continue;
                }
                string codeplugPath = Path.Combine(
                    libraryRoot,
                    "entries",
                    id.ToString("N"),
                    "revisions",
                    revision.ToString("N"),
                    "codeplug.yml");
                if (File.Exists(codeplugPath))
                {
                    candidates.Add(new LegacyConfigurationImportCandidate(
                        id.ToString("N"),
                        name!,
                        codeplugPath));
                }
            }
            return candidates.OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or UnauthorizedAccessException or
                NotSupportedException)
        {
            return [];
        }
    }

    private async Task ImportReferencedAssetsAsync(
        UserSettings settings,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        var requestedIds = new HashSet<Guid>();
        if (Guid.TryParse(settings.UserBackgroundAssetId, out Guid backgroundId))
            requestedIds.Add(backgroundId);
        foreach (AlertToneSetting tone in settings.AlertTones)
        {
            if (Guid.TryParse(tone.AssetId, out Guid toneId))
                requestedIds.Add(toneId);
        }
        if (requestedIds.Count == 0)
            return;

        string catalogPath = Path.Combine(oldRoot, "Assets", "catalog.json");
        if (!File.Exists(catalogPath))
            return;
        using JsonDocument catalog = JsonDocument.Parse(await File.ReadAllTextAsync(
            catalogPath,
            cancellationToken).ConfigureAwait(false));
        var replacements = new Dictionary<Guid, Guid>();
        var destination = new ManagedAssetStore(Path.Combine(stagingRoot, "Assets"));
        foreach (JsonElement entry in catalog.RootElement.EnumerateArray())
        {
            if (!TryGetGuid(entry, "Id", out Guid id) || !requestedIds.Contains(id) ||
                !TryGetString(entry, "DisplayName", out string? name) ||
                !TryGetString(entry, "MediaType", out string? mediaType))
            {
                continue;
            }
            string contentPath = Path.Combine(oldRoot, "Assets", "content", id.ToString("N") + ".asset");
            if (!File.Exists(contentPath))
                continue;
            await using FileStream content = File.OpenRead(contentPath);
            AssetDescriptor imported = await destination.ImportAsync(
                name!, mediaType!, content, cancellationToken).ConfigureAwait(false);
            replacements[id] = imported.Id.Value;
        }
        if (Guid.TryParse(settings.UserBackgroundAssetId, out backgroundId) &&
            replacements.TryGetValue(backgroundId, out Guid nextBackground))
        {
            settings.UserBackgroundAssetId = nextBackground.ToString();
        }
        foreach (AlertToneSetting tone in settings.AlertTones)
        {
            if (Guid.TryParse(tone.AssetId, out Guid toneId) &&
                replacements.TryGetValue(toneId, out Guid nextTone))
            {
                tone.AssetId = nextTone.ToString();
            }
        }
        foreach (ToolbarToneAssignmentSetting assignment in settings.ToolbarToneAssignments.Values)
        {
            if (assignment.IsCustomAudio && Guid.TryParse(assignment.AssetId, out Guid assignedId) &&
                replacements.TryGetValue(assignedId, out Guid nextAssignedId))
            {
                assignment.AssetId = nextAssignedId.ToString();
            }
        }
    }

    private void CommitStagedStore(string stagingRoot)
    {
        AppDataFileProtection.EnsureDirectory(newRoot);
        string[] sources = Directory.EnumerateFileSystemEntries(stagingRoot).ToArray();
        foreach (string source in sources)
        {
            string destination = Path.Combine(newRoot, Path.GetFileName(source));
            if (File.Exists(destination) || Directory.Exists(destination))
                throw new IOException($"The NEO data store changed while the legacy import was being prepared: {Path.GetFileName(destination)}");
        }

        var moved = new List<(string Source, string Destination, bool IsFile)>();
        try
        {
            foreach (string source in sources)
            {
                string destination = Path.Combine(newRoot, Path.GetFileName(source));
                bool isFile = File.Exists(source);
                if (isFile)
                    File.Move(source, destination);
                else
                    Directory.Move(source, destination);
                moved.Add((source, destination, isFile));
            }
        }
        catch
        {
            for (int index = moved.Count - 1; index >= 0; index--)
            {
                (string source, string destination, bool isFile) = moved[index];
                if (isFile && File.Exists(destination))
                    File.Move(destination, source);
                else if (!isFile && Directory.Exists(destination))
                    Directory.Move(destination, source);
            }
            throw;
        }
        Directory.Delete(stagingRoot);
    }

    private void WriteMarker(string decision)
    {
        AppDataFileProtection.EnsureDirectory(newRoot);
        string pending = markerPath + ".pending";
        File.WriteAllText(
            pending,
            JsonSerializer.Serialize(
                new LegacyImportMarker(1, decision, DateTimeOffset.UtcNow),
                DesktopSettingsJsonContext.Default.LegacyImportMarker));
        AppDataFileProtection.EnsureFile(pending);
        File.Move(pending, markerPath, overwrite: true);
        AppDataFileProtection.EnsureFile(markerPath);
    }

    private static bool TryGetGuid(JsonElement value, string name, out Guid result)
    {
        result = default;
        return (value.TryGetProperty(name, out JsonElement property) ||
                value.TryGetProperty(char.ToLowerInvariant(name[0]) + name[1..], out property)) &&
               property.ValueKind == JsonValueKind.String &&
               Guid.TryParse(property.GetString(), out result);
    }

    private static bool TryGetString(JsonElement value, string name, out string? result)
    {
        result = null;
        return (value.TryGetProperty(name, out JsonElement property) ||
                value.TryGetProperty(char.ToLowerInvariant(name[0]) + name[1..], out property)) &&
               property.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(result = property.GetString());
    }

    private sealed class LegacyManagedConfigurationDocumentSet(string codeplugPath) : IImportDocumentSet
    {
        private readonly string revisionRoot = Path.GetDirectoryName(Path.GetFullPath(codeplugPath))!;
        public IReadableDocument Primary { get; } = new DesktopConfigurationDocument(codeplugPath);

        public ValueTask<IReadableDocument?> ResolveCompanionAsync(
            string relativeReference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalized = relativeReference.Replace('\\', '/').Trim('/');
            string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string name = segments.LastOrDefault() ?? string.Empty;
            bool isManagedReference = segments.Length == 1 ||
                                      (segments.Length == 2 &&
                                       segments[0].Equals("companions", StringComparison.OrdinalIgnoreCase));
            if (!isManagedReference || name.Length == 0 || name is "." or "..")
                return ValueTask.FromResult<IReadableDocument?>(null);
            string path = Path.Combine(revisionRoot, "companions", name);
            return ValueTask.FromResult<IReadableDocument?>(
                File.Exists(path) ? new DesktopConfigurationDocument(path) : null);
        }
    }
}
