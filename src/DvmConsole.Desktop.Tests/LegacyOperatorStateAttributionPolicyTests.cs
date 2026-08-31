using DvmConsole.Core.Settings;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class LegacyOperatorStateAttributionPolicyTests
{
    [Fact]
    public void PersistedStartupAttributesLegacyStateToTheActiveConfiguration()
        => Assert.True(LegacyOperatorStateAttributionPolicy
            .ShouldAttributeToOpenedConfiguration(null, "/configs/previous.yml"));

    [Fact]
    public void ExplicitlyOpeningThePreviousCodeplugStillAttributesItsLegacyCardLayout()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-legacy-layout");
        string previous = Path.Combine(root, "codeplug.yml");
        string equivalent = Path.Combine(root, ".", "codeplug.yml");

        Assert.True(LegacyOperatorStateAttributionPolicy
            .ShouldAttributeToOpenedConfiguration(equivalent, previous));
    }

    [Fact]
    public void MatchingExplicitLaunchMigratesWidgetPositionsIntoManagedConfigurationState()
    {
        string codeplugPath = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-legacy-layout",
            "codeplug.yml");
        string channelKey = "Metro\u001FDispatch";
        string configurationId = Guid.NewGuid().ToString("N");
        var settings = new UserSettings
        {
            LastCodeplugPath = codeplugPath,
            ChannelWidgetPositions = new Dictionary<string, WidgetPositionSetting>
            {
                [channelKey] = new WidgetPositionSetting { X = 417, Y = 233 }
            }
        };
        bool allowAttribution = LegacyOperatorStateAttributionPolicy
            .ShouldAttributeToOpenedConfiguration(codeplugPath, settings.LastCodeplugPath);

        ConfigurationOperatorStateStore.Activate(
            settings,
            configurationId,
            "/managed/runtime/codeplug.yml",
            allowAttribution);

        WidgetPositionSetting restored = Assert.Single(settings.ChannelWidgetPositions).Value;
        Assert.Equal(417, restored.X);
        Assert.Equal(233, restored.Y);
        Assert.Single(settings.ConfigurationOperatorStates[configurationId].ChannelWidgetPositions);
    }

    [Fact]
    public void DifferentExplicitCodeplugDoesNotReceiveAmbiguousLegacyState()
        => Assert.False(LegacyOperatorStateAttributionPolicy
            .ShouldAttributeToOpenedConfiguration(
                "/configs/other.yml",
                "/configs/previous.yml"));
}
