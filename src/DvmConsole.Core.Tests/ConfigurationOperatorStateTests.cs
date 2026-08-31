using DvmConsole.Core.Settings;
using Xunit;

namespace DvmConsole.Core.Tests;

public sealed class ConfigurationOperatorStateTests
{
    [Fact]
    public void LegacyStateIsAttributedOnceAndManagedConfigurationsRemainIsolated()
    {
        string firstPath = Path.Combine(Path.GetTempPath(), "managed-a", "codeplug.yml");
        string secondPath = Path.Combine(Path.GetTempPath(), "managed-b", "codeplug.yml");
        string firstId = Guid.NewGuid().ToString("N");
        string secondId = Guid.NewGuid().ToString("N");
        var settings = new UserSettings
        {
            ChannelVolumes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["Metro|P25|3101"] = 1.5
            },
            SelectedWebStreams = ["Dispatch stream"],
            LastSelectedChannelKey = "Metro|P25|3101"
        };

        ConfigurationOperatorStateStore.Activate(
            settings,
            firstId,
            firstPath,
            allowLegacyAttribution: true);
        ConfigurationOperatorStateStore.CaptureActive(settings, firstId, firstPath);
        ConfigurationOperatorStateStore.Activate(
            settings,
            secondId,
            secondPath,
            allowLegacyAttribution: false);

        Assert.Empty(settings.ChannelVolumes);
        Assert.Empty(settings.SelectedWebStreams);
        Assert.Null(settings.LastSelectedChannelKey);

        settings.ChannelVolumes["Metro|P25|4101"] = 0.75;
        ConfigurationOperatorStateStore.CaptureActive(settings, secondId, secondPath);
        ConfigurationOperatorStateStore.Activate(
            settings,
            firstId,
            firstPath,
            allowLegacyAttribution: false);

        Assert.Equal(1.5, settings.ChannelVolumes["Metro|P25|3101"]);
        Assert.Equal(["Dispatch stream"], settings.SelectedWebStreams);
        Assert.Equal("Metro|P25|3101", settings.LastSelectedChannelKey);
        Assert.DoesNotContain("Metro|P25|4101", settings.ChannelVolumes);
        Assert.True(settings.LegacyConfigurationOperatorStateMigrated);
    }

    [Fact]
    public void SaveCopyPreservesNonTrustStateButDropsWebStreamAuthorization()
    {
        string path = Path.Combine(Path.GetTempPath(), "managed-source", "codeplug.yml");
        string sourceId = Guid.NewGuid().ToString("N");
        string copyId = Guid.NewGuid().ToString("N");
        var settings = new UserSettings
        {
            ChannelStereoBalances = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["Metro|DMR|3101|1"] = -0.25
            },
            SelectedWebStreams = ["Credentialed dispatch stream"]
        };
        ConfigurationOperatorStateStore.Activate(
            settings,
            sourceId,
            path,
            allowLegacyAttribution: true);
        ConfigurationOperatorStateStore.CaptureActive(settings, sourceId, path);

        ConfigurationOperatorStateStore.Copy(
            settings,
            sourceId,
            copyId,
            includeWebStreamAuthorization: false);

        ConfigurationOperatorState copy = settings.ConfigurationOperatorStates[copyId];
        Assert.Equal(-0.25, copy.ChannelStereoBalances["Metro|DMR|3101|1"]);
        Assert.Empty(copy.SelectedWebStreams);
    }

    [Fact]
    public void ConfigurationStateRoundTripsThroughSettingsStore()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"dvmconsole-configuration-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string id = Guid.NewGuid().ToString("N");
            var store = new UserSettingsStore(Path.Combine(directory, "UserSettings.json"));
            var settings = new UserSettings
            {
                ConfigurationOperatorStates = new Dictionary<string, ConfigurationOperatorState>
                {
                    [id] = new ConfigurationOperatorState
                    {
                        ReceiveEnabledChannelKeys = ["Metro|P25|3101"],
                        SelectedWebStreams = ["Dispatch stream"],
                        StudioState = new CodeplugStudioState
                        {
                            CallPrioritySystemNames = ["Metro"]
                        }
                    }
                },
                ActiveConfigurationOperatorStateId = id,
                LegacyConfigurationOperatorStateMigrated = true
            };

            store.Save(settings);
            UserSettings loaded = store.Load();

            Assert.Equal(id, loaded.ActiveConfigurationOperatorStateId);
            ConfigurationOperatorState state = loaded.ConfigurationOperatorStates[id];
            Assert.Equal(["Metro|P25|3101"], state.ReceiveEnabledChannelKeys);
            Assert.Equal(["Dispatch stream"], state.SelectedWebStreams);
            Assert.Equal(["Metro"], state.StudioState.CallPrioritySystemNames);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
