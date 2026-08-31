namespace DvmConsole.Presentation;

public enum ConfigurationStudioSection
{
    Overview,
    Systems,
    Zones,
    Streams,
    Groups,
    EncryptionKeys,
    Files
}

public sealed record ConfigurationStudioNavigationItem(
    ConfigurationStudioSection Section,
    string Label,
    string Description);
