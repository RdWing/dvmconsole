using System.Text.Json;
using System.Text.Json.Serialization;

namespace DvmConsole.Core.Settings;

internal sealed class UserSettingsSerializer
{
    private readonly JsonSerializerOptions options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public UserSettings? Deserialize(string json)
        => JsonSerializer.Deserialize<UserSettings>(json, options);

    public string Serialize(UserSettings settings)
        => JsonSerializer.Serialize(settings, options);
}

internal sealed class SettingsProfileRepository
{
    public SettingsProfileRepository(string directoryPath)
    {
        DirectoryPath = directoryPath;
    }

    public string DirectoryPath { get; }

    public IReadOnlyList<string> ListNames()
    {
        if (!Directory.Exists(DirectoryPath))
            return [];

        return Directory.EnumerateFiles(DirectoryPath, "*.json", SearchOption.TopDirectoryOnly)
            .Select(file => System.IO.Path.GetFileNameWithoutExtension(file))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string GetPath(string profileName)
    {
        string normalized = NormalizeName(profileName);
        Directory.CreateDirectory(DirectoryPath);
        return System.IO.Path.Combine(DirectoryPath, $"{normalized}.json");
    }

    public void Delete(string profileName)
    {
        string path = GetPath(profileName);
        if (File.Exists(path))
            File.Delete(path);
    }

    private static string NormalizeName(string profileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        string normalized = profileName.Trim();
        if (normalized is "." or ".." ||
            normalized.Length > 64 ||
            normalized.Any(char.IsControl) ||
            normalized.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ||
            normalized.Contains(':') ||
            normalized.Contains(System.IO.Path.DirectorySeparatorChar) ||
            normalized.Contains(System.IO.Path.AltDirectorySeparatorChar) ||
            normalized.EndsWith('.') ||
            normalized.EndsWith(' ') ||
            IsReservedWindowsName(normalized))
        {
            throw new ArgumentException(
                "Profile names must be 1-64 characters and cannot contain path separators or control characters.",
                nameof(profileName));
        }

        return normalized;
    }

    private static bool IsReservedWindowsName(string profileName)
    {
        string stem = profileName.Split('.')[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 &&
             (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
              stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
             stem[3] is >= '1' and <= '9');
    }
}
