// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using DvmConsole.Configuration.Yaml;
using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ConfigurationStudioManagedCompanionTests
{
    [Fact]
    public void KeyFileDisplayReferenceDoesNotExposeAbsoluteHostPaths()
    {
        string absolute = Path.Combine(Path.GetTempPath(), "private-session", "keys.clear");

        Assert.Equal("keys.clear", ConfigurationStudioViewModel.DisplayFileReference(absolute));
        Assert.Equal("companions/keys.clear", ConfigurationStudioViewModel.DisplayFileReference(
            Path.Combine("companions", "keys.clear")));
    }

    [Fact]
    public async Task SelectingAliasOwnerUsesExactManagedFileAndAddCreatesMissingFile()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-alias-owner-tests",
            Guid.NewGuid().ToString("N"));
        string firstDirectory = Path.Combine(directory, "first");
        string secondDirectory = Path.Combine(directory, "second");
        string codeplugPath = Path.Combine(directory, "codeplug.yml");
        string settingsPath = Path.Combine(directory, "UserSettings.json");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);

        try
        {
            string firstAliases = Path.Combine(firstDirectory, "aliases.yml");
            string secondAliases = Path.Combine(secondDirectory, "aliases.yml");
            File.WriteAllText(firstAliases, "- alias: First Unit\n  rid: 1001\n");
            File.WriteAllText(secondAliases, "- alias: TYF Unit\n  rid: 2002\n");
            File.WriteAllText(codeplugPath, $$"""
                systems:
                  - name: First
                    identity: First Console
                    address: 127.0.0.1
                    port: 62031
                    peerId: 1
                    rid: "1001"
                    aliasPath: '{{firstAliases}}'
                  - name: TYF
                    identity: TYF Console
                    address: 127.0.0.1
                    port: 62032
                    peerId: 2
                    rid: "2002"
                    aliasPath: '{{secondAliases}}'
                  - name: No Aliases
                    identity: New Console
                    address: 127.0.0.1
                    port: 62033
                    peerId: 3
                    rid: "3003"
                zones: []
                groups: []
                """);

            var settingsStore = new UserSettingsStore(settingsPath);
            await using MainWindowViewModel runtime = MainWindowViewModel.Load(codeplugPath, settingsStore);
            var studio = new ConfigurationStudioViewModel(
                ConfigurationDocument.Open(codeplugPath),
                null,
                codeplugPath,
                runtime,
                new DesktopConfigurationStudioCompanionSource(),
                new DesktopConfigurationStudioPreviewFactory(),
                new ConfigurationStudioInitialState(
                    new Dictionary<string, ConfigurationStudioPosition>(),
                    new Dictionary<string, string>(),
                    []),
                ConfigurationStudioSection.Files);

            SystemConfiguration tyf = Assert.Single(studio.Systems, system => system.Name == "TYF");
            studio.SelectedAliasSystem = tyf;
            Assert.Equal("TYF Unit", Assert.IsType<ConfigurationAliasRow>(studio.SelectedAlias).Name);
            Assert.True(studio.CanEditSelectedAlias);

            SystemConfiguration first = Assert.Single(studio.Systems, system => system.Name == "First");
            string firstReference = first.AliasPath;
            string replacementReference = studio.AttachAliasFile(
                tyf,
                "/external/replacement.yml",
                "- alias: Replacement TYF Unit\n  rid: 2222\n");
            Assert.Equal("replacement.yml", replacementReference);
            Assert.Equal(replacementReference, tyf.AliasPath);
            Assert.Equal(firstReference, first.AliasPath);
            Assert.Equal("Replacement TYF Unit", Assert.IsType<ConfigurationAliasRow>(studio.SelectedAlias).Name);
            Assert.Same(tyf, studio.SelectedAliasSystem);
            Assert.Contains(replacementReference, studio.CaptureSaveState().AliasContents.Keys);

            SystemConfiguration withoutAliases = Assert.Single(
                studio.Systems,
                system => system.Name == "No Aliases");
            studio.SelectedAliasSystem = withoutAliases;
            Assert.Null(studio.SelectedAlias);

            studio.AddAlias();

            ConfigurationAliasRow added = Assert.IsType<ConfigurationAliasRow>(studio.SelectedAlias);
            Assert.True(studio.CanEditSelectedAlias);
            Assert.Equal("./alias.yml", withoutAliases.AliasPath);
            Assert.Equal(1u, added.Rid);
            Assert.Contains(
                Path.Combine(directory, "alias.yml"),
                studio.CaptureSaveState().AliasContents.Keys);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NewUserCanBuildEncryptedChannelAndManagedAliasFromEmptyConfiguration()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-new-user-configuration-tests",
            Guid.NewGuid().ToString("N"));
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "Demo", "codeplug.yml");
        var settingsStore = new UserSettingsStore(Path.Combine(directory, "UserSettings.json"));
        Directory.CreateDirectory(directory);

        try
        {
            await using MainWindowViewModel runtime = MainWindowViewModel.Load(codeplugPath, settingsStore);
            var studio = new ConfigurationStudioViewModel(
                ConfigurationDocument.CreateNew(),
                null,
                "managed:new-user",
                runtime,
                new DesktopConfigurationStudioCompanionSource(),
                new DesktopConfigurationStudioPreviewFactory(),
                new ConfigurationStudioInitialState(
                    new Dictionary<string, ConfigurationStudioPosition>(),
                    new Dictionary<string, string>(),
                    []),
                ConfigurationStudioSection.Overview);

            studio.AddSystem();
            SystemConfiguration system = Assert.Single(studio.Systems);
            ZoneConfiguration zone = Assert.Single(studio.Zones);
            Assert.Equal(string.Empty, system.AliasPath);
            Assert.Equal("New Zone", zone.Name);
            Assert.Equal(system.Name, studio.SelectedZoneSystemName);

            studio.Undo();
            Assert.Empty(studio.Systems);
            Assert.Empty(studio.Zones);
            studio.Redo();
            system = Assert.Single(studio.Systems);
            zone = Assert.Single(studio.Zones);

            string? changedProperty = null;
            system.PropertyChanged += (_, args) => changedProperty = args.PropertyName;
            system.Name = "Regional FNE";
            Assert.Equal(nameof(SystemConfiguration.Name), changedProperty);
            studio.CommitFieldEdit();
            Assert.Equal("Regional FNE", Assert.Single(studio.Systems).Name);
            Assert.Equal("Regional FNE", studio.SelectedZoneSystemName);
            studio.SelectedZoneName = "Regional Dispatch";
            studio.CommitFieldEdit();
            Assert.Equal("Regional Dispatch", zone.Name);

            studio.AddChannelToSelectedSystem();
            Assert.Same(zone, Assert.Single(studio.Zones));
            ChannelConfiguration channel = Assert.Single(zone.Channels);
            Assert.Equal(system.Name, channel.System);
            Assert.Equal("p25", channel.Mode);

            studio.AddKey();
            KeyEntry key = Assert.Single(studio.KeyEntries);
            Assert.Equal("Key 1", key.Name);
            Assert.Equal(system.Name, key.System);
            key.Key = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
            studio.SelectedKeyIdHexDigits = "not-hex";
            studio.CommitKeyEdit();
            Assert.Equal(1, key.KeyId);
            Assert.Contains(studio.ValidationIssues, issue =>
                issue.Path == "keys[0].keyId" && issue.IsError);
            studio.SelectedKeyIdHexDigits = "1";
            studio.CommitKeyEdit();

            studio.SelectedZone = zone;
            studio.SelectedChannel = channel;
            studio.SelectedChannelAlgorithm = Assert.Single(
                studio.AvailableChannelAlgorithms,
                option => option.ConfigurationValue == "aes");
            studio.SelectedChannelKeyIdHexDigits = "1";
            studio.CommitChannelAlgorithmEdit();
            Assert.Equal("aes", channel.Algo);
            Assert.Equal("0x1", channel.KeyId);

            studio.SelectedAliasSystem = system;
            studio.AddAlias();
            Assert.Equal("aliases.yml", system.AliasPath);
            ConfigurationAliasRow alias = Assert.Single(studio.Aliases);
            alias.Rid = 4242;
            alias.Name = "New User Alias";
            studio.CommitAliasEdit();

            studio.AddStream();
            ConfigurationStreamRow stream = Assert.IsType<ConfigurationStreamRow>(studio.SelectedStream);
            stream.Stream.Url = "invalid";
            studio.CommitFieldEdit();
            ConfigurationValidationIssue streamIssue = Assert.Single(studio.ValidationIssues, issue =>
                issue.Domain == "Web Streams" && issue.Path.EndsWith(".url", StringComparison.Ordinal));
            studio.NavigateToIssue(streamIssue);
            Assert.True(studio.IsStreams);
            Assert.Same(stream.Stream, studio.SelectedStream!.Stream);
            stream.Stream.Url = "https://example.invalid/stream";
            studio.CommitFieldEdit();

            studio.SelectedZone = zone;
            studio.DuplicateZone();
            Assert.DoesNotContain(studio.ValidationIssues, issue => issue.IsError);
            Assert.All(
                studio.Configuration.Zones.SelectMany(item => item.Channels)
                    .GroupBy(item => (item.System, item.Name)),
                group => Assert.Single(group));
            Assert.All(
                studio.Configuration.Zones.SelectMany(item => item.WebStreams)
                    .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
                group => Assert.Single(group));

            ConfigurationStudioSaveState state = studio.CaptureSaveState();
            Assert.Equal("keys.clear", studio.Configuration.KeyFile);
            Assert.Contains("name: Key 1", state.KeyFileContent, StringComparison.Ordinal);
            Assert.Contains("system: Regional FNE", state.KeyFileContent, StringComparison.Ordinal);
            Assert.Contains("keyId: 0x1", state.KeyFileContent, StringComparison.Ordinal);
            Assert.DoesNotContain("requiredLength", state.KeyFileContent, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("New User Alias", state.AliasContents["aliases.yml"], StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DirtyBundleExportUsesCurrentManagedCompanionContents()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-dirty-export-tests",
            Guid.NewGuid().ToString("N"));
        string sourceDirectory = Path.Combine(directory, "source");
        string exportDirectory = Path.Combine(directory, "export");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(exportDirectory);
        string yaml = """
            keyFile: keys.clear
            systems:
              - name: Test
                identity: Console
                address: 127.0.0.1
                port: 62031
                peerId: 1
                rid: "1001"
                aliasPath: alias.yml
            zones:
              - name: Operations
                channels:
                  - name: Dispatch
                    system: Test
                    tgid: "1"
                    mode: p25
                    algo: none
                    card_size: normal
            groups: []
            """;
        string sourcePath = Path.Combine(sourceDirectory, "codeplug.yml");
        string destinationPath = Path.Combine(exportDirectory, "codeplug.yml");
        File.WriteAllText(sourcePath, yaml);
        File.WriteAllText(Path.Combine(sourceDirectory, "keys.clear"), "old-key-content");
        File.WriteAllText(Path.Combine(sourceDirectory, "alias.yml"), "old-alias-content");

        try
        {
            var source = new ConfigurationStudioExportDocumentSet(
                yaml,
                "codeplug.yml",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["keys.clear"] = "current-key-content",
                    ["alias.yml"] = "current-alias-content"
                });
            var destination = new DesktopConfigurationDocumentSet(destinationPath);

            Directory.Delete(sourceDirectory, recursive: true);

            ConfigurationBundleExportResult result = await ConfigurationBundleExporter.ExportAsync(
                yaml,
                source,
                destination,
                new ConfigurationExportOptions(Sanitized: false, IncludeCompanions: true));

            Assert.Empty(result.OmittedCompanionReferences);
            Assert.Equal("current-key-content", File.ReadAllText(Path.Combine(exportDirectory, "keys.clear")));
            Assert.Equal("current-alias-content", File.ReadAllText(Path.Combine(exportDirectory, "alias.yml")));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AddingFirstEncryptionKeyCreatesManagedKeyFile()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-managed-companion-tests",
            Guid.NewGuid().ToString("N"));
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "Demo", "codeplug.yml");
        var settingsStore = new UserSettingsStore(Path.Combine(directory, "UserSettings.json"));
        Directory.CreateDirectory(directory);

        try
        {
            await using MainWindowViewModel runtime = MainWindowViewModel.Load(codeplugPath, settingsStore);
            var studio = new ConfigurationStudioViewModel(
                ConfigurationDocument.CreateNew(),
                null,
                "managed:new",
                runtime,
                new DesktopConfigurationStudioCompanionSource(),
                new DesktopConfigurationStudioPreviewFactory(),
                new ConfigurationStudioInitialState(
                    new Dictionary<string, ConfigurationStudioPosition>(),
                    new Dictionary<string, string>(),
                    []),
                ConfigurationStudioSection.EncryptionKeys);

            studio.AddKey();

            Assert.True(studio.HasKeyFile);
            Assert.Equal("keys.clear", studio.Configuration.KeyFile);
            Assert.Equal("keys.clear", studio.KeyFileIdentifierText);
            Assert.True(studio.IsKeyFileDirty);
            Assert.Single(studio.KeyEntries);
            Assert.Same(studio.KeyEntries[0], studio.SelectedKey);
            Assert.Contains("name: Key 1", studio.CaptureSaveState().KeyFileContent, StringComparison.Ordinal);
            Assert.Contains("keyId: 0x1", studio.CaptureSaveState().KeyFileContent, StringComparison.Ordinal);

            studio.Undo();
            Assert.False(studio.HasKeyFile);
            Assert.Null(studio.Configuration.KeyFile);
            Assert.Empty(studio.KeyEntries);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SelectedCompanionsBecomePortableManagedReferences()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-managed-companion-tests",
            Guid.NewGuid().ToString("N"));
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "Demo", "codeplug.yml");
        var settingsStore = new UserSettingsStore(Path.Combine(directory, "UserSettings.json"));
        Directory.CreateDirectory(directory);

        try
        {
            await using MainWindowViewModel runtime = MainWindowViewModel.Load(codeplugPath, settingsStore);
            var studio = new ConfigurationStudioViewModel(
                ConfigurationDocument.Open(codeplugPath),
                runtime.ConfigurationReference?.Id,
                codeplugPath,
                runtime,
                new DesktopConfigurationStudioCompanionSource(),
                new DesktopConfigurationStudioPreviewFactory(),
                new ConfigurationStudioInitialState(
                    new Dictionary<string, ConfigurationStudioPosition>(),
                    new Dictionary<string, string>(),
                    []),
                ConfigurationStudioSection.Files);
            const string keyContent = """
                keys:
                  - protocol: p25
                    keyId: 25
                    algId: 132
                    key: 000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F
                """;
            const string aliasContent = """
                - rid: 4242
                  alias: Selected Unit
                """;

            string keyReference = studio.AttachKeyFile("/outside/private/keys.clear", keyContent);
            SystemConfiguration firstSystem = studio.Configuration.Systems[0];
            string aliasReference = studio.AttachAliasFile(
                firstSystem,
                "C:\\outside\\aliases.yml",
                aliasContent);
            ConfigurationStudioSaveState state = studio.CaptureSaveState();

            Assert.Equal("keys.clear", keyReference);
            Assert.Equal("keys.clear", studio.Configuration.KeyFile);
            Assert.Equal("aliases-2.yml", aliasReference);
            Assert.Equal("aliases-2.yml", firstSystem.AliasPath);
            Assert.True(studio.IsKeyFileDirty);
            Assert.True(studio.AliasFilesDirty);
            Assert.Contains("keyId: 0x19", state.KeyFileContent, StringComparison.Ordinal);
            Assert.Contains("Selected Unit", state.AliasContents[aliasReference], StringComparison.Ordinal);
            Assert.DoesNotContain("outside", state.Yaml, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DesktopExportTransactionRollbackPreservesExistingFiles()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-export-rollback-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string primaryPath = Path.Combine(directory, "codeplug.yml");
        string companionPath = Path.Combine(directory, "keys.clear");
        File.WriteAllText(primaryPath, "original-primary");
        File.WriteAllText(companionPath, "original-companion");

        try
        {
            var destination = new DesktopConfigurationDocumentSet(primaryPath);
            await using IExportDocumentTransaction transaction = await destination.BeginTransactionAsync();
            await WriteAsync(transaction.Primary, "replacement-primary");
            IWritableDocument companion = await transaction.CreateCompanionAsync("keys.clear");
            await WriteAsync(companion, "replacement-companion");

            await transaction.RollbackAsync();

            Assert.Equal("original-primary", File.ReadAllText(primaryPath));
            Assert.Equal("original-companion", File.ReadAllText(companionPath));
            Assert.Empty(Directory.EnumerateDirectories(directory, ".dvmconsole-export-*"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AvaloniaExportTreatsANewPickerDestinationAsMissingRollbackContent()
    {
        byte[]? original = await AvaloniaStorageExportTransaction.ReadExistingOrMissingAsync(
            "codeplug.yml",
            () => Task.FromException<Stream>(new FileNotFoundException(
                "Could not find file '/Volumes/EXAMPLE/codeplug.yml'.")));

        Assert.Null(original);
    }

    [Fact]
    public async Task AvaloniaExportDoesNotHideAnUnreadableExistingDestination()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            AvaloniaStorageExportTransaction.ReadExistingOrMissingAsync(
                "codeplug.yml",
                () => Task.FromException<Stream>(new UnauthorizedAccessException(
                    "Destination is not writable."))));
    }

    [Fact]
    public async Task DesktopExportTransactionsSerializeConcurrentWriters()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-export-concurrency-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string primaryPath = Path.Combine(directory, "codeplug.yml");
        File.WriteAllText(primaryPath, "original");
        try
        {
            var destination = new DesktopConfigurationDocumentSet(primaryPath);
            await using IExportDocumentTransaction first = await destination.BeginTransactionAsync();
            await using IExportDocumentTransaction second = await destination.BeginTransactionAsync();
            await WriteAsync(first.Primary, "first");
            await WriteAsync(second.Primary, "second");

            await Task.WhenAll(first.CommitAsync().AsTask(), second.CommitAsync().AsTask());

            Assert.Contains(File.ReadAllText(primaryPath), new[] { "first", "second" });
            Assert.Empty(Directory.EnumerateDirectories(directory, ".dvmconsole-export-*"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DesktopExportStagingUsesOwnerOnlyUnixPermissions()
    {
        if (OperatingSystem.IsWindows())
            return;

        string directory = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-export-permission-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string primaryPath = Path.Combine(directory, "codeplug.yml");
        try
        {
            var destination = new DesktopConfigurationDocumentSet(primaryPath);
            await using IExportDocumentTransaction transaction = await destination.BeginTransactionAsync();
            await WriteAsync(transaction.Primary, "secret-primary");
            IWritableDocument companion = await transaction.CreateCompanionAsync("keys.clear");
            await WriteAsync(companion, "secret-key");
            string stagingDirectory = Assert.Single(
                Directory.EnumerateDirectories(directory, ".dvmconsole-export-*"));

            Assert.Equal(
                AppDataFileProtection.PrivateDirectoryMode,
                File.GetUnixFileMode(stagingDirectory));
            Assert.All(
                Directory.EnumerateFiles(stagingDirectory),
                file => Assert.Equal(
                    AppDataFileProtection.PrivateFileMode,
                    File.GetUnixFileMode(file)));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task WriteAsync(IWritableDocument document, string value)
    {
        await using Stream stream = await document.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(value);
    }
}
