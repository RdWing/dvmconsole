using DvmConsole.Core.Settings;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ConsoleSessionCompositionTests
{
    [Fact]
    public async Task RejectedConfigurationBuildsPresentationWithoutKeyOrConnectionServices()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dvmconsole-session-{Guid.NewGuid():N}");
        string codeplugPath = Path.Combine(root, "invalid.yml");
        string settingsPath = Path.Combine(root, "settings.json");
        Directory.CreateDirectory(root);
        File.WriteAllText(codeplugPath, """
            keyFile: "missing-keys.clear"
            systems: []
            zones:
              - name: Streams
                channels: []
                web_streams:
                  - name: Dispatch
                    url: "https://example.test/live"
            """);

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(
                codeplugPath,
                new UserSettingsStore(settingsPath));

            Assert.False(viewModel.IsCodeplugLoaded);
            Assert.Empty(viewModel.Systems);
            Assert.Single(viewModel.Zones);
            Assert.Single(viewModel.WebStreams);
            Assert.StartsWith("Configuration has 1 validation error(s):", viewModel.StatusText);
            Assert.DoesNotContain("Encryption keys unavailable", viewModel.StatusText, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
