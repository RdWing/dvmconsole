namespace DvmConsole.Core.Settings;

[Flags]
public enum SettingsImportScope
{
    None = 0,
    General = 1 << 0,
    Audio = 1 << 1,
    Presets = 1 << 2,
    RecordingAndPatch = 1 << 3,
    Session = 1 << 4,
    Connections = 1 << 5,
    OperatorState = General | Audio | Connections | Presets | RecordingAndPatch,
    All = OperatorState | Session
}

public sealed record SettingsImportPreview(
    string SourcePath,
    int SchemaVersion,
    string? LastCodeplugPath,
    IReadOnlyList<string> PopulatedSections)
{
    public string SummaryText
        => $"Profile format v{SchemaVersion}; sections: " +
            (PopulatedSections.Count == 0 ? "none" : string.Join(", ", PopulatedSections)) +
            (string.IsNullOrWhiteSpace(LastCodeplugPath)
                ? string.Empty
                : $"; saved codeplug: {LastCodeplugPath}");
}
