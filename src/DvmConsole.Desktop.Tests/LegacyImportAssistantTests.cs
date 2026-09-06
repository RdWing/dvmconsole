// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Settings;
using DvmConsole.Application;
using DvmConsole.Configuration.Yaml;
using DvmConsole.Storage;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class LegacyImportAssistantTests
{
    [Fact]
    public async Task ImportedToolbarShortcutFollowsTheRemappedAssetId()
    {
        using var fixture = new LegacyImportFixture();
        AssetId sourceId = AssetId.New();
        string oldAssets = Path.Combine(fixture.OldRoot, "Assets");
        Directory.CreateDirectory(Path.Combine(oldAssets, "content"));
        await File.WriteAllBytesAsync(Path.Combine(oldAssets, "content", sourceId.Value.ToString("N") + ".asset"), [1, 2, 3]);
        // Legacy catalogs encode IDs as strings.
        await File.WriteAllTextAsync(Path.Combine(oldAssets, "catalog.json"),
            System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new { Id = sourceId.ToString(), DisplayName = "dispatch.wav", MediaType = "application/octet-stream" }
            }));
        new UserSettingsStore(fixture.OldSettingsPath).Save(new UserSettings
        {
            AlertTones = [new() { Name = "Dispatch", AssetId = sourceId.ToString(), FileName = "dispatch.wav" }],
            ToolbarToneAssignments = new()
            {
                [1] = new() { PresetName = "Dispatch", IsCustomAudio = true, AssetId = sourceId.ToString() }
            }
        });
        var assistant = new LegacyImportAssistant(fixture.NewRoot);
        await assistant.ImportAsync(assistant.Discover(), new LegacyImportSelection(true, [], []));
        UserSettings imported = new UserSettingsStore(fixture.NewSettingsPath).Load();
        Guid alertId = Guid.Parse(Assert.Single(imported.AlertTones).AssetId!);
        Assert.NotEqual(sourceId.Value, alertId);
        Assert.Equal(alertId, Guid.Parse(imported.ToolbarToneAssignments[1].AssetId!));
        var destination = new ManagedAssetStore(Path.Combine(fixture.NewRoot, "Assets"));
        await using Stream restored = await destination.OpenReadAsync(new AssetId(alertId));
        Assert.Equal(3, restored.Length);
    }

    [Fact]
    public async Task ImportsOnlySelectedSettingsAndProfilesWithoutChangingLegacyStore()
    {
        using var fixture = new LegacyImportFixture();
        var oldStore = new UserSettingsStore(fixture.OldSettingsPath);
        oldStore.Save(new UserSettings { DarkMode = true, UiScale = 1.25 });
        oldStore.SaveNamedProfile("Dispatch", new UserSettings { ToneFrequencyHz = 725 });
        oldStore.SaveNamedProfile("Ignored", new UserSettings { ToneFrequencyHz = 900 });
        Dictionary<string, byte[]> original = fixture.CaptureLegacyFiles();
        var assistant = new LegacyImportAssistant(fixture.NewRoot);
        LegacyImportDiscovery discovery = assistant.Discover();

        await assistant.ImportAsync(
            discovery,
            new LegacyImportSelection(true, ["Dispatch"], []));

        var imported = new UserSettingsStore(fixture.NewSettingsPath);
        Assert.True(imported.Load().DarkMode);
        Assert.Equal(1.25, imported.Load().UiScale);
        Assert.Equal(725, imported.LoadNamedProfile("Dispatch").ToneFrequencyHz);
        Assert.DoesNotContain("Ignored", imported.ListNamedProfiles());
        Assert.True(File.Exists(Path.Combine(fixture.NewRoot, LegacyImportAssistant.MarkerFileName)));
        Assert.False(assistant.ShouldOfferImport());
        Assert.Equal(original, fixture.CaptureLegacyFiles(), ByteArrayDictionaryComparer.Instance);
    }

    [Fact]
    public void DeclineSuppressesFuturePromptButCancelLeavesStoreUntouched()
    {
        using var fixture = new LegacyImportFixture();
        new UserSettingsStore(fixture.OldSettingsPath).Save(new UserSettings());
        var assistant = new LegacyImportAssistant(fixture.NewRoot);

        Assert.True(assistant.ShouldOfferImport());
        Assert.False(Directory.Exists(fixture.NewRoot));

        assistant.Decline();

        Assert.False(assistant.ShouldOfferImport());
        Assert.True(File.Exists(Path.Combine(fixture.NewRoot, LegacyImportAssistant.MarkerFileName)));
    }

    [Fact]
    public void DoesNotOfferForNonPristineNeoStoreOrMalformedLegacySettings()
    {
        using var fixture = new LegacyImportFixture();
        Directory.CreateDirectory(fixture.OldRoot);
        File.WriteAllText(fixture.OldSettingsPath, "not json");
        var assistant = new LegacyImportAssistant(fixture.NewRoot);

        LegacyImportDiscovery discovery = assistant.Discover();

        Assert.False(discovery.HasSettings);
        Assert.False(discovery.HasCandidates);

        Directory.CreateDirectory(fixture.NewRoot);
        File.WriteAllText(Path.Combine(fixture.NewRoot, "existing.txt"), "owned by NEO");
        Assert.False(assistant.ShouldOfferImport());
    }

    [Fact]
    public async Task CancellationDoesNotCommitOrWriteDecisionMarker()
    {
        using var fixture = new LegacyImportFixture();
        new UserSettingsStore(fixture.OldSettingsPath).Save(new UserSettings { DarkMode = true });
        var assistant = new LegacyImportAssistant(fixture.NewRoot);
        LegacyImportDiscovery discovery = assistant.Discover();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => assistant.ImportAsync(
            discovery,
            new LegacyImportSelection(true, [], []),
            cancellation.Token));

        Assert.False(File.Exists(fixture.NewSettingsPath));
        Assert.False(File.Exists(Path.Combine(fixture.NewRoot, LegacyImportAssistant.MarkerFileName)));
        Assert.True(assistant.ShouldOfferImport());
    }

    [Fact]
    public async Task ImportsSelectedManagedConfigurationWithItsCompanionsOnly()
    {
        using var fixture = new LegacyImportFixture();
        string sourceRoot = Path.Combine(fixture.OldRoot, "source");
        Directory.CreateDirectory(sourceRoot);
        string sourcePath = Path.Combine(sourceRoot, "codeplug.yml");
        await File.WriteAllTextAsync(sourcePath, """
            keyFile: keys.clear
            systems:
              - name: Test
                identity: Console
                address: 192.0.2.10
                port: 62031
                peerId: 1
                rid: "1001"
            zones: []
            groups: []
            """);
        await File.WriteAllTextAsync(
            Path.Combine(sourceRoot, "keys.clear"),
            "Test,1,00112233445566778899AABBCCDDEEFF,128,p25\n");
        var oldLibrary = new ManagedConfigurationLibrary(
            Path.Combine(fixture.OldRoot, "ConfigurationLibrary"));
        await oldLibrary.ImportAsync(
            new DesktopConfigurationDocumentSet(sourcePath),
            new ConfigurationImportOptions(
                ConfigurationConflictResolution.ImportAsNew,
                ConfirmExternalCompanions: true));
        Dictionary<string, byte[]> original = fixture.CaptureLegacyFiles();
        var assistant = new LegacyImportAssistant(fixture.NewRoot);
        LegacyImportDiscovery discovery = assistant.Discover();
        LegacyConfigurationImportCandidate selected = Assert.Single(discovery.Configurations);

        await assistant.ImportAsync(
            discovery,
            new LegacyImportSelection(false, [], [selected.Id]));

        var importedLibrary = new ManagedConfigurationLibrary(
            Path.Combine(fixture.NewRoot, "ConfigurationLibrary"));
        List<ConfigurationSummary> entries = [];
        await foreach (ConfigurationSummary entry in importedLibrary.ListAsync())
            entries.Add(entry);
        Assert.Single(entries);
        Assert.Contains(
            Directory.EnumerateFiles(
                Path.Combine(fixture.NewRoot, "ConfigurationLibrary"),
                "*",
                SearchOption.AllDirectories),
            path => File.ReadAllText(path).Contains("00112233445566778899AABBCCDDEEFF", StringComparison.Ordinal));
        Assert.Equal(original, fixture.CaptureLegacyFiles(), ByteArrayDictionaryComparer.Instance);
    }

    private sealed class LegacyImportFixture : IDisposable
    {
        private readonly string parent = Directory.CreateTempSubdirectory("dvmconsole-legacy-import-").FullName;

        public string OldRoot => Path.Combine(parent, "dvmconsole");
        public string NewRoot => Path.Combine(parent, "dvmconsole-neo");
        public string OldSettingsPath => Path.Combine(OldRoot, "UserSettings.json");
        public string NewSettingsPath => Path.Combine(NewRoot, "UserSettings.json");

        public Dictionary<string, byte[]> CaptureLegacyFiles()
            => Directory.Exists(OldRoot)
                ? Directory.EnumerateFiles(OldRoot, "*", SearchOption.AllDirectories)
                    .ToDictionary(
                        path => Path.GetRelativePath(OldRoot, path),
                        File.ReadAllBytes,
                        StringComparer.Ordinal)
                : [];

        public void Dispose() => Directory.Delete(parent, recursive: true);
    }

    private sealed class ByteArrayDictionaryComparer : IEqualityComparer<Dictionary<string, byte[]>>
    {
        public static ByteArrayDictionaryComparer Instance { get; } = new();

        public bool Equals(Dictionary<string, byte[]>? left, Dictionary<string, byte[]>? right)
            => left is not null && right is not null &&
               left.Count == right.Count &&
               left.All(pair => right.TryGetValue(pair.Key, out byte[]? value) && pair.Value.SequenceEqual(value));

        public int GetHashCode(Dictionary<string, byte[]> value) => value.Count;
    }
}
