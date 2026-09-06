// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DvmConsole.Core.IO;
using DvmConsole.Core.Settings;

namespace DvmConsole.Configuration.Yaml;

internal static class AtomicLibraryFile
{
    public static void Recover<T>(string path, JsonTypeInfo<T> typeInfo)
    {
        if (File.Exists(path) && new FileInfo(path).Length > ManagedResourceLimits.ManagedMetadataBytes)
        {
            throw new InvalidDataException(
                $"Managed configuration metadata exceeds the {ManagedResourceLimits.ManagedMetadataBytes / (1024 * 1024)} MiB safety limit.");
        }
        string pending = PendingPath(path);
        string backup = BackupPath(path);

        if (!File.Exists(path))
        {
            if (TryRead(pending, typeInfo, out _))
                File.Move(pending, path, overwrite: false);
            else if (TryRead(backup, typeInfo, out _))
                File.Move(backup, path, overwrite: false);
        }

        if (File.Exists(path) && !TryRead(path, typeInfo, out _))
        {
            if (!TryRead(backup, typeInfo, out T? recovered))
                throw new InvalidDataException($"Managed configuration metadata '{path}' is corrupt and has no valid backup.");
            Write(path, recovered!, typeInfo);
        }

        DeleteIfExists(pending);
        DeleteIfExists(backup);
    }

    public static T Read<T>(string path, JsonTypeInfo<T> typeInfo)
    {
        byte[] content = BoundedResourceReader.ReadFile(
            path,
            ManagedResourceLimits.ManagedMetadataBytes,
            "Managed configuration metadata");
        return JsonSerializer.Deserialize(content, typeInfo)
            ?? throw new InvalidDataException($"Managed configuration metadata '{path}' was empty.");
    }

    public static void Write<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        string? parent = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parent))
            throw new InvalidOperationException($"Cannot resolve metadata parent for '{path}'.");
        AppDataFileProtection.EnsureDirectory(parent);

        string pending = PendingPath(path);
        string backup = BackupPath(path);
        using (FileStream stream = new(
                   pending,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None,
                   4096,
                   FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, value, typeInfo);
            stream.Flush(flushToDisk: true);
        }
        AppDataFileProtection.EnsureFile(pending);

        if (File.Exists(path))
            File.Copy(path, backup, overwrite: true);
        File.Move(pending, path, overwrite: true);
        AppDataFileProtection.EnsureFile(path);
        DeleteIfExists(backup);
    }

    private static bool TryRead<T>(string path, JsonTypeInfo<T> typeInfo, out T? value)
    {
        value = default;
        if (!File.Exists(path))
            return false;
        try
        {
            value = Read(path, typeInfo);
            return true;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            return false;
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static string PendingPath(string path) => path + ".pending";
    private static string BackupPath(string path) => path + ".backup";
}
