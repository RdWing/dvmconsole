// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DvmConsole.Application;
using DvmConsole.Core.IO;
using DvmConsole.Core.Settings;

namespace DvmConsole.Storage;

internal static class AtomicJsonFile
{
    public static T Read<T>(string path, JsonTypeInfo<T> typeInfo)
    {
        byte[] content = BoundedResourceReader.ReadFile(
            path,
            ManagedResourceLimits.ManagedMetadataBytes,
            "Managed storage catalog");
        return JsonSerializer.Deserialize(content, typeInfo)
            ?? throw new InvalidDataException($"Managed storage catalog '{path}' was empty.");
    }

    public static void Recover<T>(string path, JsonTypeInfo<T> typeInfo)
    {
        RejectOversized(path);
        string pending = path + ".pending";
        string backup = path + ".backup";
        if (!TryRead(path, typeInfo, out _))
        {
            if (TryRead(backup, typeInfo, out T? recovered))
            {
                File.Copy(backup, pending, overwrite: true);
                AppDataFileProtection.EnsureFile(pending);
                File.Move(pending, path, overwrite: true);
            }
            else if (File.Exists(path))
            {
                throw new InvalidDataException($"Managed storage catalog '{path}' is corrupt and has no valid backup.");
            }
        }

        if (File.Exists(pending))
            File.Delete(pending);
    }

    private static void RejectOversized(string path)
    {
        if (File.Exists(path) && new FileInfo(path).Length > ManagedResourceLimits.ManagedMetadataBytes)
        {
            throw new InvalidDataException(
                $"Managed storage catalog exceeds the {ManagedResourceLimits.ManagedMetadataBytes / (1024 * 1024)} MiB safety limit.");
        }
    }

    public static void Write<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        string pending = path + ".pending";
        string backup = path + ".backup";
        string? parent = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parent))
            throw new InvalidOperationException($"Cannot resolve catalog parent for '{path}'.");
        AppDataFileProtection.EnsureDirectory(parent);
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
        {
            File.Copy(path, backup, overwrite: true);
            AppDataFileProtection.EnsureFile(backup);
        }
        // The rename is the commit point. A later permission failure must not
        // make callers roll back content referenced by this committed catalog.
        File.Move(pending, path, overwrite: true);
    }

    private static bool TryRead<T>(string path, JsonTypeInfo<T> typeInfo, out T? value)
    {
        value = default;
        if (!File.Exists(path))
            return false;
        try
        {
            value = Read(path, typeInfo);
            return value is not null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            return false;
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<AssetDescriptor>))]
[JsonSerializable(typeof(List<RecordingDescriptor>))]
internal sealed partial class StorageJsonContext : JsonSerializerContext;
