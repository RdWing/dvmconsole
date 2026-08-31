using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DvmConsole.Application;

namespace DvmConsole.Storage;

internal static class AtomicJsonFile
{
    public static void Write<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        string pending = path + ".pending";
        string backup = path + ".backup";
        File.WriteAllText(pending, JsonSerializer.Serialize(value, typeInfo));
        if (File.Exists(path))
            File.Replace(pending, path, backup, ignoreMetadataErrors: true);
        else
            File.Move(pending, path);
        if (File.Exists(backup))
            File.Delete(backup);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<AssetDescriptor>))]
[JsonSerializable(typeof(List<RecordingDescriptor>))]
internal sealed partial class StorageJsonContext : JsonSerializerContext;
