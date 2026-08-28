using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DvmConsole.Core.Configuration;

public static class AliasFileLoader
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
        .Build();

    public static List<RadioAlias> Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Alias file not found.", fullPath);

        return Parse(File.ReadAllText(fullPath));
    }

    public static List<RadioAlias> Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        return deserializer.Deserialize<List<RadioAlias>>(yaml) ?? [];
    }

    public static string FindAlias(IEnumerable<RadioAlias>? aliases, uint rid)
    {
        return aliases is RadioAliasIndex index
            ? index.Find(rid)
            : aliases?.FirstOrDefault(alias => alias.Rid == rid)?.Alias ?? string.Empty;
    }

    public static string Serialize(IEnumerable<RadioAlias> aliases)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        return Serializer.Serialize(aliases.ToList());
    }
}
