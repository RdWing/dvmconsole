using DvmConsole.Core.Configuration;
using Xunit;

namespace DvmConsole.Core.Tests;

public sealed class ConfigurationLoaderTests
{
    [Fact]
    public void LoadsTheLegacyExampleCodeplug()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "codeplug.example.yml");

        ConsoleConfiguration configuration = ConfigurationLoader.Load(path);

        Assert.Single(configuration.Systems);
        Assert.Equal("System 1", configuration.Systems[0].Name);
        Assert.Equal(3, configuration.Zones.Count);
        Assert.Equal("Channel 1", configuration.Zones[0].Channels[0].Name);
        Assert.Empty(ConfigurationLoader.Validate(configuration));
    }

    [Fact]
    public void ResolvesRelativePathsFromTheCodeplugDirectory()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "codeplug.example.yml");
        ConsoleConfiguration configuration = ConfigurationLoader.Load(path);

        string resolved = ConfigurationLoader.ResolvePath(configuration, "keys.clear");

        Assert.Equal(
            Path.Combine(Path.GetDirectoryName(path)!, "keys.clear"),
            resolved);
    }

    [Fact]
    public void LoadsLegacyKeyAndAliasFiles()
    {
        string testData = Path.Combine(AppContext.BaseDirectory, "TestData");

        KeyContainer keys = KeyFileLoader.Load(Path.Combine(testData, "keys.example.clear"));
        List<RadioAlias> aliases = AliasFileLoader.Load(Path.Combine(testData, "alias.example.yml"));

        Assert.Equal(2, keys.Keys.Count);
        Assert.Equal((ushort)1, keys.Keys[0].KeyId);
        Assert.Single(aliases);
        Assert.Equal("Radio 1", AliasFileLoader.FindAlias(aliases, 1));
    }
}
