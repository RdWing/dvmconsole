using DvmConsole.Application;
using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel : IConfigurationStudioRuntimeContext
{
    IReadOnlyList<PatchGroupEditorViewModel> IConfigurationStudioRuntimeContext.OperationalGroups
        => PatchGroups;
    double IConfigurationStudioRuntimeContext.DefaultCanvasWidth => DefaultWidgetCanvasWidth;
    double IConfigurationStudioRuntimeContext.CardSpacing => ChannelWidgetSpacing;
    double IConfigurationStudioRuntimeContext.CardHeight => ChannelCardHeight;
    double IConfigurationStudioRuntimeContext.UiFontSize => UiFontSize;
    double IConfigurationStudioRuntimeContext.UiSmallFontSize => UiSmallFontSize;
    double IConfigurationStudioRuntimeContext.UiCompactFontSize => UiCompactFontSize;
    bool IConfigurationStudioRuntimeContext.DarkMode => DarkMode;

    bool IConfigurationStudioRuntimeContext.IsActiveConfiguration(
        ConfigurationId? configurationId,
        string legacyIdentity)
        => configurationId is { } id
            ? ConfigurationReference?.Id == id
            : !string.IsNullOrWhiteSpace(CurrentCodeplugPath) &&
              FileSystemPathIdentity.AreEquivalent(legacyIdentity, CurrentCodeplugPath);

    double IConfigurationStudioRuntimeContext.ResolveCardWidth(string? cardSize)
        => ChannelViewModel.ResolveCardWidth(cardSize);

    string? IConfigurationStudioRuntimeContext.ApplyOperationalGroups(
        IEnumerable<PatchGroupEditorViewModel> groups)
        => ApplyGroupOperatorStates(groups);

    void IConfigurationStudioRuntimeContext.SetOperationalGroupEnabled(
        PatchGroupEditorViewModel group)
        => SetPatchGroupEnabled(group);
}
