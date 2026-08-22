using System.Collections;
using System.Collections.Frozen;

namespace DvmConsole.Core.Configuration;

public sealed class RadioAliasIndex : IReadOnlyCollection<RadioAlias>
{
    private readonly RadioAlias[] aliases;
    private readonly FrozenDictionary<uint, string> namesByRadioId;

    public RadioAliasIndex(IEnumerable<RadioAlias>? aliases)
    {
        this.aliases = aliases?
            .Select(alias => new RadioAlias { Rid = alias.Rid, Alias = alias.Alias })
            .ToArray() ?? [];
        var names = new Dictionary<uint, string>();
        foreach (RadioAlias alias in this.aliases)
        {
            if (!names.ContainsKey(alias.Rid))
                names[alias.Rid] = alias.Alias;
        }
        namesByRadioId = names.ToFrozenDictionary();
    }

    public static RadioAliasIndex Empty { get; } = new([]);

    public int Count => aliases.Length;

    public string Find(uint radioId)
        => namesByRadioId.GetValueOrDefault(radioId) ?? string.Empty;

    public IEnumerator<RadioAlias> GetEnumerator()
        => ((IEnumerable<RadioAlias>)aliases).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator()
        => aliases.GetEnumerator();
}
