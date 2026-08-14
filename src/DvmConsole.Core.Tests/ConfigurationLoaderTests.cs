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
}
