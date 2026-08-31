using System.Collections;

namespace DvmConsole.Presentation;

public interface IConfigurationHierarchyNode
{
    string Label { get; }
    string CountText { get; }
    bool IsExpanded { get; set; }
    IEnumerable Children { get; }
}

public interface IConfigurationStudioNavigationViewModel
{
    string SearchText { get; set; }
    string SystemNavigationHeading { get; }
    string StreamNavigationHeading { get; }
    string GroupNavigationHeading { get; }
    string KeyNavigationHeading { get; }
    string FileNavigationHeading { get; }
    IEnumerable ConfigurationHierarchy { get; }
    IConfigurationHierarchyNode? SelectedHierarchyNode { get; set; }
}

public sealed class ConfigurationStudioSectionEventArgs(ConfigurationStudioSection section) : EventArgs
{
    public ConfigurationStudioSection Section { get; } = section;
}
