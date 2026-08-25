namespace DvmConsole.Desktop;

public enum OperatorToolSection
{
    General,
    Audio,
    Tones,
    Streams,
    Recorder,
    History,
    Groups,
    Connections,
    Ptt,
    Clock,
    EncryptionKeys
}

internal sealed record OperatorToolSectionDefinition(
    OperatorToolSection Section,
    string DisplayName,
    string SearchTerms,
    string CommandId);

internal static class OperatorToolSectionCatalog
{
    public static IReadOnlyList<OperatorToolSectionDefinition> All { get; } =
    [
        new(OperatorToolSection.General, "General", "appearance theme widgets startup console", "settings.general"),
        new(OperatorToolSection.Audio, "Audio", "microphone input output processing devices vocoder", "settings.audio"),
        new(OperatorToolSection.Tones, "Tones & DTMF", "tones dtmf quick call alerts sequence presets", "settings.tones"),
        new(OperatorToolSection.Streams, "Web streams", "web streams output route volume", "settings.streams"),
        new(OperatorToolSection.Recorder, "Recorder", "tar recording retention folder channels", "settings.recorder"),
        new(OperatorToolSection.History, "History", "history recordings calls filters export playback", "settings.history"),
        new(OperatorToolSection.Groups, "Groups & patches", "groups patches multi select ptt", "settings.groups"),
        new(OperatorToolSection.Connections, "Connections", "fne systems host port reconnect encryption status", "settings.connections"),
        new(OperatorToolSection.Ptt, "PTT", "ptt keyboard global active serial hold toggle microphone", "settings.ptt"),
        new(OperatorToolSection.Clock, "Clocks", "clock toolbar timezone utc seconds 24 hour", "settings.clock"),
        new(OperatorToolSection.EncryptionKeys, "Encryption keys", "encryption keys key status algorithms", "settings.encryption-keys")
    ];
}
