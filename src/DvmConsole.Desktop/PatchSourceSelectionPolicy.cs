using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

// Keeps patch direction semantics out of the receive pipeline. One-way
// patches decode only their explicit source; two-way patches can source from
// any selected member. Multi-select groups never participate in patch decode.
internal static class PatchSourceSelectionPolicy
{
    public static ChannelViewModel[] SelectEnabledSources(
        IEnumerable<PatchGroupEditorViewModel> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        return groups
            .Where(group => group.IsPatchGroup && group.IsEnabled)
            .SelectMany(SelectGroupSources)
            .Distinct()
            .ToArray();
    }

    private static IEnumerable<ChannelViewModel> SelectGroupSources(
        PatchGroupEditorViewModel group)
    {
        if (group.IsOneWay)
        {
            if (group.SelectedSource?.Channel is ChannelViewModel source)
                yield return source;
            yield break;
        }

        foreach (PatchMemberEditorViewModel member in group.Members.Where(member => member.IsMember))
            if (member.Channel is ChannelViewModel channel)
                yield return channel;
    }
}
