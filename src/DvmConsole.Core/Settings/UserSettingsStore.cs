using System.Text.Json;

namespace DvmConsole.Core.Settings;

// JSON-backed user settings store with resilient reads and atomic replacement.
// The path is injectable so tests and packaged hosts do not depend on a
// platform-specific profile location.
public sealed class UserSettingsStore
{
    private readonly UserSettingsSerializer serializer;
    private readonly AtomicTextFileStore fileStore;
    private readonly SettingsProfileRepository profiles;
    private readonly UserSettingsNormalizationPipeline normalization;

    public UserSettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        serializer = new UserSettingsSerializer();
        fileStore = new AtomicTextFileStore(Path);
        profiles = new SettingsProfileRepository(ProfilesDirectoryPath);
        normalization = new UserSettingsNormalizationPipeline();
    }

    public string Path { get; }

    public string ProfilesDirectoryPath
        => System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(Path) ?? AppContext.BaseDirectory,
            "Profiles");

    public static string DefaultPath
    {
        get
        {
            string baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(baseDirectory))
                baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDirectory))
                baseDirectory = AppContext.BaseDirectory;
            return System.IO.Path.Combine(baseDirectory, "DVMProject", "dvmconsole", "UserSettings.json");
        }
    }

    public UserSettings Load()
    {
        if (!fileStore.Exists)
            return new UserSettings();

        try
        {
            UserSettings settings = serializer.Deserialize(fileStore.ReadAllText())
                ?? new UserSettings();
            return normalization.NormalizeAfterLoad(settings);
        }
        catch (JsonException)
        {
            return new UserSettings();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new UserSettings();
        }
    }

    public void Save(UserSettings settings)
    {
        SaveSnapshot(CaptureSnapshot(settings));
    }

    public UserSettingsSnapshot CaptureSnapshot(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        normalization.NormalizeBeforeWrite(settings);
        return new UserSettingsSnapshot(serializer.Serialize(settings));
    }

    public void SaveSnapshot(UserSettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        fileStore.WriteAllText(snapshot.Json);
    }

    public void Export(UserSettings settings, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        string destination = System.IO.Path.GetFullPath(destinationPath);
        if (destination.Equals(Path, StringComparison.OrdinalIgnoreCase))
        {
            Save(settings);
            return;
        }

        Save(settings);
        fileStore.CopyTo(destination);
    }

    public SettingsImportPreview PreviewImport(string sourcePath)
    {
        string source = ResolveSettingsFilePath(sourcePath);
        UserSettings settings = ReadSettingsFile(source);
        return SettingsImportPolicy.CreatePreview(source, settings);
    }

    public SettingsImportPreview PreviewNamedProfile(string profileName)
        => PreviewImport(GetNamedProfilePath(profileName));

    public IReadOnlyList<string> ListNamedProfiles()
        => profiles.ListNames();

    public void SaveNamedProfile(string profileName, UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string profilePath = GetNamedProfilePath(profileName);
        new UserSettingsStore(profilePath).Save(settings);
    }

    public UserSettings LoadNamedProfile(string profileName)
    {
        string path = GetNamedProfilePath(profileName);
        if (!File.Exists(path))
            throw new FileNotFoundException("Named settings profile not found.", path);
        return new UserSettingsStore(path).Load();
    }

    public UserSettings ImportNamedProfile(
        string profileName,
        SettingsImportScope scope = SettingsImportScope.OperatorState)
        => Import(GetNamedProfilePath(profileName), scope);

    public void DeleteNamedProfile(string profileName)
        => profiles.Delete(profileName);

    public UserSettings Import(
        string sourcePath,
        SettingsImportScope scope = SettingsImportScope.All)
    {
        string source = ResolveSettingsFilePath(sourcePath);
        UserSettings imported = ReadSettingsFile(source);
        if (scope == SettingsImportScope.All)
        {
            Save(imported);
            return Load();
        }

        UserSettings current = Load();
        SettingsImportPolicy.Merge(current, imported, scope);
        Save(current);
        return Load();
    }

    private static string ResolveSettingsFilePath(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        string source = System.IO.Path.GetFullPath(sourcePath);
        if (!File.Exists(source))
            throw new FileNotFoundException("Settings file not found.", source);
        return source;
    }

    private UserSettings ReadSettingsFile(string source)
    {
        UserSettings imported;

        try
        {
            imported = serializer.Deserialize(File.ReadAllText(source))
                ?? throw new InvalidDataException("The settings file did not contain a settings object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The settings file is not valid DVM Console JSON.", exception);
        }

        normalization.NormalizeBeforeWrite(imported);
        return imported;
    }



    public void Reset()
        => fileStore.Delete();

    private string GetNamedProfilePath(string profileName)
        => profiles.GetPath(profileName);

}

public sealed class UserSettingsSnapshot
{
    internal UserSettingsSnapshot(string json)
    {
        Json = json;
    }

    public string Json { get; }
}
