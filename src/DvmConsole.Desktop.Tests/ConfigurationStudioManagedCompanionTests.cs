using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ConfigurationStudioManagedCompanionTests
{
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
            Assert.Contains("keyId: 1", studio.CaptureSaveState().KeyFileContent, StringComparison.Ordinal);

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
            Assert.Contains("keyId: 25", state.KeyFileContent, StringComparison.Ordinal);
            Assert.Contains("Selected Unit", state.AliasContents[aliasReference], StringComparison.Ordinal);
            Assert.DoesNotContain("outside", state.Yaml, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
