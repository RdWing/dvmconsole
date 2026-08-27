using System.Text.Json.Serialization;

namespace DvmConsole.Desktop;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(RecordingFinalizationDescriptor))]
[JsonSerializable(typeof(OperatorViewSettings))]
internal sealed partial class DesktopSettingsJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(CallRecordingMetadata))]
internal sealed partial class RecordingMetadataJsonContext : JsonSerializerContext;
