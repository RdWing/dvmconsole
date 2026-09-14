// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json.Serialization;

namespace DvmConsole.Storage;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(RecordingFinalizationDescriptor))]
internal sealed partial class RecordingSpoolJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(CallRecordingMetadata))]
internal sealed partial class RecordingMetadataJsonContext : JsonSerializerContext;
