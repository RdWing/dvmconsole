// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json.Serialization;

namespace DvmConsole.Desktop;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(RecordingFinalizationDescriptor))]
[JsonSerializable(typeof(OperatorViewSettings))]
[JsonSerializable(typeof(DocumentationManifest))]
[JsonSerializable(typeof(LegacyImportMarker))]
internal sealed partial class DesktopSettingsJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(CallRecordingMetadata))]
internal sealed partial class RecordingMetadataJsonContext : JsonSerializerContext;
