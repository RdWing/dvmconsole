namespace DvmConsole.Core.Configuration;

using System.Text.Json;

public sealed record ConfigurationFileChange(
    string Path,
    string Content,
    string? ExpectedSourceHash,
    string Category,
    bool ContainsSecrets);

public sealed record ConfigurationSavePlan(
    IReadOnlyList<ConfigurationFileChange> Files,
    IReadOnlyList<ConfigurationValidationIssue> Issues)
{
    public bool CanSave => Files.Count > 0 && Issues.All(issue => !issue.IsError);
}

public sealed record ConfigurationSaveResult(string BackupDirectory, IReadOnlyList<string> WrittenFiles);

public sealed class ConfigurationExternalChangeException(string path)
    : IOException($"'{path}' changed outside Configuration Studio.")
{
    public string ChangedPath { get; } = path;
}

public static class ConfigurationSaveTransaction
{
    public static ConfigurationSaveResult Execute(ConfigurationSavePlan plan, string backupRoot)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupRoot);
        if (!plan.CanSave)
            throw new InvalidOperationException("The configuration contains errors or no files are scheduled to be saved.");

        StringComparer pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        ConfigurationFileChange[] changes = plan.Files
            .Select(change => change with { Path = Path.GetFullPath(change.Path) })
            .ToArray();
        string? duplicatePath = changes
            .GroupBy(change => change.Path, pathComparer)
            .FirstOrDefault(group => group.Count() > 1)?
            .Key;
        if (duplicatePath is not null)
        {
            throw new InvalidOperationException(
                $"Configuration save contains more than one artifact targeting '{duplicatePath}'.");
        }

        foreach (ConfigurationFileChange change in changes)
        {
            if (change.ExpectedSourceHash is null || !File.Exists(change.Path))
                continue;
            if (!string.Equals(
                    ConfigurationDocument.ComputeFileHash(change.Path),
                    change.ExpectedSourceHash,
                    StringComparison.Ordinal))
            {
                throw new ConfigurationExternalChangeException(change.Path);
            }
        }

        ValidateStagedContent(changes);

        string backupDirectory = Path.Combine(
            Path.GetFullPath(backupRoot),
            $"configuration-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(backupDirectory);
        TryRestrictDirectory(backupDirectory);

        var staged = new List<(ConfigurationFileChange Change, string Stage, string? Backup)>();
        var replaced = new List<(ConfigurationFileChange Change, string? Backup)>();
        try
        {
            for (int index = 0; index < changes.Length; index++)
            {
                ConfigurationFileChange change = changes[index];
                string? directory = Path.GetDirectoryName(change.Path);
                if (string.IsNullOrWhiteSpace(directory))
                    throw new InvalidOperationException($"Could not resolve the parent directory for '{change.Path}'.");
                Directory.CreateDirectory(directory);

                string stage = Path.Combine(directory, $".{Path.GetFileName(change.Path)}.{Guid.NewGuid():N}.studio.tmp");
                File.WriteAllText(stage, change.Content);
                if (change.ContainsSecrets)
                    TryRestrictFile(stage);
                string? backup = null;
                if (File.Exists(change.Path))
                {
                    backup = Path.Combine(backupDirectory, $"{index:D3}-{Path.GetFileName(change.Path)}");
                    File.Copy(change.Path, backup, overwrite: false);
                    TryRestrictFile(backup);
                }
                staged.Add((change, stage, backup));
            }

            foreach ((ConfigurationFileChange change, string stage, string? backup) in staged)
            {
                File.Move(stage, change.Path, overwrite: true);
                replaced.Add((change, backup));
            }

            return new ConfigurationSaveResult(backupDirectory, changes.Select(change => change.Path).ToArray());
        }
        catch
        {
            foreach ((ConfigurationFileChange change, string? backup) in replaced.AsEnumerable().Reverse())
            {
                try
                {
                    if (backup is not null && File.Exists(backup))
                        File.Copy(backup, change.Path, overwrite: true);
                    else if (File.Exists(change.Path))
                        File.Delete(change.Path);
                }
                catch
                {
                    // Preserve the original exception. The restricted backup remains for manual recovery.
                }
            }
            throw;
        }
        finally
        {
            foreach ((_, string stage, _) in staged)
            {
                try
                {
                    if (File.Exists(stage))
                        File.Delete(stage);
                }
                catch
                {
                    // A stale staging file is preferable to obscuring a save result.
                }
            }
        }
    }

    private static void TryRestrictDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (PlatformNotSupportedException)
        {
        }
    }

    private static void ValidateStagedContent(IEnumerable<ConfigurationFileChange> changes)
    {
        foreach (ConfigurationFileChange change in changes)
        {
            switch (change.Category)
            {
                case "Codeplug":
                    ConfigurationDocument document = ConfigurationDocument.Parse(change.Content, change.Path);
                    ConfigurationValidationIssue? error = document.Validate().FirstOrDefault(issue => issue.IsError);
                    if (error is not null)
                        throw new InvalidDataException($"Staged codeplug validation failed: {error.Message}");
                    break;
                case "Encryption key file":
                    ConfigurationValidationIssue? keyError = KeyFileValidator.Validate(KeyFileLoader.Parse(change.Content))
                        .FirstOrDefault(issue => issue.IsError);
                    if (keyError is not null)
                        throw new InvalidDataException($"Staged encryption key validation failed: {keyError.Message}");
                    break;
                case "RID alias file":
                    List<RadioAlias> aliases = AliasFileLoader.Parse(change.Content);
                    if (aliases.GroupBy(alias => alias.Rid).Any(group => group.Count() > 1))
                        throw new InvalidDataException("Staged RID alias validation found a duplicate RID.");
                    break;
                case "Operator settings":
                    using (JsonDocument.Parse(change.Content))
                    {
                    }
                    break;
            }
        }
    }

    private static void TryRestrictFile(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (PlatformNotSupportedException)
        {
        }
    }
}
