using DvmConsole.Application;

namespace DvmConsole.Presentation;

/// <summary>
/// Narrow bridge from the shared Configuration Studio presentation to a live
/// console. Configuration identity is opaque to Presentation; the host decides
/// whether it represents the running revision.
/// </summary>
public interface IConfigurationStudioRuntimeContext
{
    IReadOnlyList<PatchGroupEditorViewModel> OperationalGroups { get; }
    double DefaultCanvasWidth { get; }
    double CardSpacing { get; }
    double CardHeight { get; }
    double UiFontSize { get; }
    double UiSmallFontSize { get; }
    double UiCompactFontSize { get; }
    bool DarkMode { get; }

    bool IsActiveConfiguration(ConfigurationId? configurationId, string legacyIdentity);
    double ResolveCardWidth(string? cardSize);
    string? ApplyOperationalGroups(IEnumerable<PatchGroupEditorViewModel> groups);
    void SetOperationalGroupEnabled(PatchGroupEditorViewModel group);
}
