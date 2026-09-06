// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using System.Reflection;
using System.Security.Cryptography;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.IO;

namespace DvmConsole.Core.Settings;

// JSON-backed user settings store with resilient reads and atomic replacement.
// The path is injectable so tests and packaged hosts do not depend on a
// platform-specific profile location.
public sealed class UserSettingsStore
{
    private const string ApplicationDataDirectoryName = "dvmconsole-neo";

    private static readonly PropertyInfo[] WritableSettingsProperties = typeof(UserSettings)
        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(property => property.CanRead && property.SetMethod?.IsPublic == true)
        .ToArray();
    private readonly UserSettingsSerializer serializer;
    private readonly AtomicTextFileStore fileStore;
    private readonly SettingsProfileRepository profiles;
    private readonly UserSettingsNormalizationPipeline normalization;
    private readonly object generationSync = new();
    private string? observedFingerprint;
    private bool hasObservedFingerprint;
    private bool writesSuppressed;

    public SettingsLoadDiagnostics LastLoadDiagnostics { get; private set; } = SettingsLoadDiagnostics.None;
    public SettingsReadState LastReadState { get; private set; } = SettingsReadState.NotRead;

    public UserSettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        string? appDataDirectory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrWhiteSpace(appDataDirectory))
        {
            bool isDefaultManagedRoot = string.Equals(
                Path,
                System.IO.Path.GetFullPath(DefaultPath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            if (isDefaultManagedRoot)
                AppDataFileProtection.EnsureDirectory(appDataDirectory, repairExistingTree: true);
        }
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

    private static string DefaultDirectoryPath
    {
        get
        {
            string baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(baseDirectory))
                baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDirectory))
                baseDirectory = AppContext.BaseDirectory;
            return System.IO.Path.Combine(baseDirectory, "DVMProject", ApplicationDataDirectoryName);
        }
    }

    public static string DefaultPath
        => System.IO.Path.Combine(DefaultDirectoryPath, "UserSettings.json");

    public UserSettings Load()
    {
        try
        {
            string json = fileStore.ReadAllText(ManagedResourceLimits.SettingsBytes, "Settings file");
            UserSettings settings = serializer.Deserialize(json)
                ?? throw new InvalidDataException("The settings document must contain an object.");
            ObserveFingerprint(ComputeFingerprint(json));
            writesSuppressed = false;
            LastReadState = SettingsReadState.Loaded;
            LastLoadDiagnostics = SettingsLoadDiagnostics.None;
            return normalization.NormalizeAfterLoad(settings);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            LastReadState = SettingsReadState.Missing;
            ObserveFingerprint(MissingFingerprint);
            if (!writesSuppressed)
                LastLoadDiagnostics = SettingsLoadDiagnostics.None;
            return new UserSettings();
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidDataException or System.Text.DecoderFallbackException)
        {
            LastReadState = SettingsReadState.Corrupt;
            return RecoverMalformedSettings(exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            writesSuppressed = true;
            LastReadState = SettingsReadState.Unreadable;
            LastLoadDiagnostics = new SettingsLoadDiagnostics(
                RecoveredFromBackup: false,
                DefaultsUsed: true,
                AutomaticWritesSuppressed: true,
                "Settings could not be read. Automatic writes are paused; reload settings when access is restored or explicitly reset them. " + exception.Message);
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
        if (writesSuppressed)
        {
            throw new InvalidOperationException(
                "Automatic settings writes are disabled because settings could not be loaded safely. " +
                "Reload settings, restore their backup, or explicitly reset settings before saving.");
        }
        using FileStream storeLock = CrossProcessStoreLock.Acquire(Path + ".lock");
        string currentFingerprint = ReadCurrentFingerprint();
        lock (generationSync)
        {
            if (hasObservedFingerprint && !string.Equals(
                    observedFingerprint,
                    currentFingerprint,
                    StringComparison.Ordinal))
            {
                throw new SettingsConflictException(
                    "Settings changed in another DVM Console process. Reload them or save this configuration separately.");
            }
        }

        fileStore.WriteAllText(snapshot.Json);
        string savedFingerprint = ComputeFingerprint(snapshot.Json);
        ObserveFingerprint(savedFingerprint);
    }

    public void ApplySerializedSnapshot(UserSettings target, string json)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        UserSettings source = serializer.Deserialize(json)
            ?? throw new InvalidDataException("The operator-settings snapshot was empty.");
        normalization.NormalizeAfterLoad(source);
        foreach (PropertyInfo property in WritableSettingsProperties)
            property.SetValue(target, property.GetValue(source));
    }

    public void Export(UserSettings settings, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        string destination = System.IO.Path.GetFullPath(destinationPath);
        if (FileSystemPathIdentity.Equals(destination, Path))
        {
            Save(settings);
            return;
        }

        Save(settings);
        fileStore.CopyTo(destination);
    }

    public void Export(UserSettings settings, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("The settings destination must be writable.", nameof(destination));

        UserSettingsSnapshot snapshot = CaptureSnapshot(settings);
        SaveSnapshot(snapshot);
        if (destination.CanSeek)
            destination.SetLength(0);
        using var writer = new StreamWriter(
            destination,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 4096,
            leaveOpen: true);
        writer.Write(snapshot.Json);
        writer.Flush();
    }

    public SettingsImportPreview PreviewImport(string sourcePath)
        => StageImport(sourcePath).Preview;

    public SettingsImportStage StageImport(string sourcePath)
    {
        string source = ResolveSettingsFilePath(sourcePath);
        UserSettings settings = ReadSettingsFile(source);
        return new SettingsImportStage(
            settings,
            SettingsImportPolicy.CreatePreview(source, settings, Load()));
    }

    public SettingsImportStage StageImport(Stream source, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        if (!source.CanRead)
            throw new ArgumentException("The settings source must be readable.", nameof(source));
        UserSettings settings = ReadSettingsJson(BoundedResourceReader.ReadUtf8(
            source,
            ManagedResourceLimits.SettingsBytes,
            "Settings import"));
        return new SettingsImportStage(
            settings,
            SettingsImportPolicy.CreatePreview(sourceName, settings, Load()));
    }

    public SettingsImportPreview PreviewNamedProfile(string profileName)
        => PreviewImport(GetNamedProfilePath(profileName));

    public SettingsImportStage StageNamedProfile(string profileName)
        => StageImport(GetNamedProfilePath(profileName));

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
        SettingsImportScope scope = SettingsImportScope.All,
        bool acceptRecordingPolicy = false)
        => Import(StageImport(sourcePath), scope, acceptRecordingPolicy);

    public UserSettings Import(
        Stream source,
        SettingsImportScope scope = SettingsImportScope.All,
        bool acceptRecordingPolicy = false)
        => Import(StageImport(source, "Imported settings"), scope, acceptRecordingPolicy);

    public UserSettings Import(
        SettingsImportStage stage,
        SettingsImportScope scope = SettingsImportScope.All,
        bool acceptRecordingPolicy = false)
    {
        ArgumentNullException.ThrowIfNull(stage);
        UserSettings imported = stage.Settings;
        UserSettings current = Load();
        bool policyAccepted = acceptRecordingPolicy || current.RecordingRetentionPolicyAccepted;
        if (scope == SettingsImportScope.All)
        {
            imported.RecordingRetentionPolicyAccepted = policyAccepted;
            SettingsImportPolicy.ApplyChannelPresentationToActiveConfiguration(
                imported, imported, current.ActiveConfigurationOperatorStateId, scope);
            Save(imported);
            return Load();
        }

        string? destinationConfigurationId = current.ActiveConfigurationOperatorStateId;
        SettingsImportPolicy.Merge(current, imported, scope);
        SettingsImportPolicy.ApplyChannelPresentationToActiveConfiguration(
            current, imported, destinationConfigurationId, scope);
        current.RecordingRetentionPolicyAccepted = policyAccepted;
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
        => ReadSettingsJson(BoundedResourceReader.ReadUtf8File(
            source,
            ManagedResourceLimits.SettingsBytes,
            "Settings file"));

    private UserSettings ReadSettingsJson(string json)
    {
        try
        {
            UserSettings imported = serializer.Deserialize(json)
                ?? throw new InvalidDataException("The settings file did not contain a settings object.");
            normalization.NormalizeBeforeWrite(imported);
            return imported;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The settings file is not valid DVM Console JSON.", exception);
        }
    }



    public void Reset()
    {
        using FileStream storeLock = CrossProcessStoreLock.Acquire(Path + ".lock");
        fileStore.Delete();
        string generationPath = Path + ".generation";
        if (File.Exists(generationPath))
            File.Delete(generationPath);
        string backupPath = Path + ".backup";
        if (File.Exists(backupPath))
            File.Delete(backupPath);
        writesSuppressed = false;
        LastLoadDiagnostics = SettingsLoadDiagnostics.None;
        ObserveFingerprint(MissingFingerprint);
        LastReadState = SettingsReadState.Missing;
    }

    public bool RestoreLastKnownGood()
    {
        using FileStream storeLock = CrossProcessStoreLock.Acquire(Path + ".lock");
        string backupPath = Path + ".backup";
        if (!File.Exists(backupPath))
            return false;
        string json = BoundedResourceReader.ReadUtf8File(
            backupPath,
            ManagedResourceLimits.SettingsBytes,
            "Settings backup");
        _ = ReadSettingsJson(json);
        fileStore.WriteAllText(json, preserveBackup: true);
        writesSuppressed = false;
        LastLoadDiagnostics = SettingsLoadDiagnostics.None;
        ObserveFingerprint(ComputeFingerprint(json));
        LastReadState = SettingsReadState.Loaded;
        return true;
    }

    private UserSettings RecoverMalformedSettings(Exception failure)
    {
        string backupPath = Path + ".backup";
        if (File.Exists(backupPath))
        {
            try
            {
                string backupJson = BoundedResourceReader.ReadUtf8File(
                    backupPath,
                    ManagedResourceLimits.SettingsBytes,
                    "Settings backup");
                UserSettings backup = serializer.Deserialize(backupJson)
                    ?? throw new JsonException("The backup did not contain a settings object.");
                QuarantineMalformedPrimary();
                writesSuppressed = true;
                ObserveFingerprint(ReadDamagedFingerprint());
                LastLoadDiagnostics = new SettingsLoadDiagnostics(
                    RecoveredFromBackup: true,
                    DefaultsUsed: false,
                    AutomaticWritesSuppressed: true,
                    "The primary settings file was damaged. DVM Console loaded the last-known-good backup and paused settings writes until the backup is restored or settings are reset.");
                return normalization.NormalizeAfterLoad(backup);
            }
            catch (Exception exception) when (
                exception is IOException or JsonException or InvalidDataException or
                System.Text.DecoderFallbackException or UnauthorizedAccessException)
            {
            }
        }

        QuarantineMalformedPrimary();
        writesSuppressed = true;
        ObserveFingerprint(ReadDamagedFingerprint());
        LastLoadDiagnostics = new SettingsLoadDiagnostics(
            RecoveredFromBackup: false,
            DefaultsUsed: true,
            AutomaticWritesSuppressed: true,
            $"The settings file was damaged and no usable backup was available ({failure.Message}). Defaults are loaded, but writes remain paused until settings are reset.");
        return new UserSettings();
    }

    private void QuarantineMalformedPrimary()
    {
        if (!File.Exists(Path))
            return;
        string quarantinePath = Path + $".corrupt.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        try
        {
            File.Copy(Path, quarantinePath, overwrite: false);
            AppDataFileProtection.EnsureFile(quarantinePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private const string MissingFingerprint = "missing";

    private string ReadCurrentFingerprint()
        => fileStore.Exists
            ? ComputeFingerprint(fileStore.ReadAllText(ManagedResourceLimits.SettingsBytes, "Settings file"))
            : MissingFingerprint;

    private string ReadDamagedFingerprint()
    {
        if (!fileStore.Exists)
            return MissingFingerprint;
        var file = new FileInfo(Path);
        return $"damaged:{file.Length}:{file.LastWriteTimeUtc.Ticks}";
    }

    private static string ComputeFingerprint(string content)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));

    private void ObserveFingerprint(string fingerprint)
    {
        lock (generationSync)
        {
            observedFingerprint = fingerprint;
            hasObservedFingerprint = true;
        }
    }

    private string GetNamedProfilePath(string profileName)
        => profiles.GetPath(profileName);

}

public enum SettingsReadState { NotRead, Missing, Loaded, Corrupt, Unreadable }

public sealed record SettingsLoadDiagnostics(
    bool RecoveredFromBackup,
    bool DefaultsUsed,
    bool AutomaticWritesSuppressed,
    string? Warning)
{
    public static SettingsLoadDiagnostics None { get; } = new(false, false, false, null);
}

public sealed class UserSettingsSnapshot
{
    internal UserSettingsSnapshot(string json)
    {
        Json = json;
    }

    public string Json { get; }
}
