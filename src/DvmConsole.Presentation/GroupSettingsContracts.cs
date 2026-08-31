using System.Collections;

namespace DvmConsole.Presentation;

public interface IGroupSettingsViewModel
{
    IEnumerable PatchGroups { get; }
}

public sealed class PatchGroupEventArgs(PatchGroupEditorViewModel group) : EventArgs
{
    public PatchGroupEditorViewModel Group { get; } = group ?? throw new ArgumentNullException(nameof(group));
}
