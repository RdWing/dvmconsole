using System.Text.Json;
using DvmConsole.Core.Settings;

namespace DvmConsole.Desktop;

// Optional presentation state stays outside UserSettings schema 6 so the
// established operator, audio, protocol, and card-layout contract is
// unchanged.
internal sealed class OperatorViewSettings
{
    public const int CurrentSchemaVersion = 1;
    public const double DefaultEngineeringHealthHeight = 92;
    public const double MinimumEngineeringHealthHeight = 72;
    public const double MaximumEngineeringHealthHeight = 240;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public bool EngineeringHealthVisible { get; set; }
    public double EngineeringHealthHeight { get; set; } = DefaultEngineeringHealthHeight;

    public OperatorViewSettings Snapshot()
        => new()
        {
            SchemaVersion = SchemaVersion,
            EngineeringHealthVisible = EngineeringHealthVisible,
            EngineeringHealthHeight = EngineeringHealthHeight
        };
}

internal sealed class OperatorViewStore
{
    private readonly AtomicTextFileStore fileStore;

    public OperatorViewStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        fileStore = new AtomicTextFileStore(Path);
    }

    public string Path { get; }

    public static string DefaultPath
        => System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(DvmConsole.Core.Settings.UserSettingsStore.DefaultPath)
                ?? AppContext.BaseDirectory,
            "OperatorView.json");

    public OperatorViewSettings Load()
    {
        if (!fileStore.Exists)
            return new OperatorViewSettings();

        try
        {
            OperatorViewSettings settings = JsonSerializer.Deserialize<OperatorViewSettings>(
                    fileStore.ReadAllText(),
                    DesktopSettingsJsonContext.Default.OperatorViewSettings)
                ?? throw new JsonException("Operator view settings were empty.");
            if (settings.SchemaVersion != OperatorViewSettings.CurrentSchemaVersion)
                throw new JsonException("Unsupported operator view settings schema.");
            Normalize(settings);
            return settings;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // A malformed optional view preference must never prevent the
            // console from opening. Preserve the source file for diagnosis.
            return new OperatorViewSettings();
        }
    }

    public void Save(OperatorViewSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        OperatorViewSettings snapshot = settings.Snapshot();
        Normalize(snapshot);

        fileStore.WriteAllText(JsonSerializer.Serialize(
            snapshot,
            DesktopSettingsJsonContext.Default.OperatorViewSettings));
    }

    private static void Normalize(OperatorViewSettings settings)
    {
        settings.SchemaVersion = OperatorViewSettings.CurrentSchemaVersion;
        settings.EngineeringHealthHeight = double.IsFinite(settings.EngineeringHealthHeight)
            ? Math.Clamp(
                settings.EngineeringHealthHeight,
                OperatorViewSettings.MinimumEngineeringHealthHeight,
                OperatorViewSettings.MaximumEngineeringHealthHeight)
            : OperatorViewSettings.DefaultEngineeringHealthHeight;
    }
}
