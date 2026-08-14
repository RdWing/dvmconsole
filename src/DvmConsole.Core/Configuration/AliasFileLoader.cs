using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DvmConsole.Core.Configuration;

public static class AliasFileLoader
{
    public static List<RadioAlias> Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Alias file not found.", fullPath);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        return deserializer.Deserialize<List<RadioAlias>>(File.ReadAllText(fullPath)) ?? [];
    }

    public static string FindAlias(IEnumerable<RadioAlias>? aliases, uint rid)
    {
        return aliases?.FirstOrDefault(alias => alias.Rid == rid)?.Alias ?? string.Empty;
    }
}
