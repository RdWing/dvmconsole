using System.Diagnostics.CodeAnalysis;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DvmConsole.Core.Configuration;

internal static class YamlConfigurationReader
{
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification =
        "Temporary Phase 2 YAML allowlist: migrate this builder to a generated StaticContext without changing codeplug compatibility.")]
    public static ConsoleConfiguration Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The codeplug file was not found.", fullPath);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        using var reader = File.OpenText(fullPath);
        ConsoleConfiguration configuration = deserializer.Deserialize<ConsoleConfiguration>(reader)
            ?? throw new InvalidDataException("The codeplug file did not contain a configuration.");
        configuration.SourcePath = fullPath;
        return configuration;
    }
}
