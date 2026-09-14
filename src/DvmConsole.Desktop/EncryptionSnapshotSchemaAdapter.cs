// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Runtime;
using DvmConsole.FneClient;
using DvmConsole.Storage;

namespace DvmConsole.Desktop;

internal static class EncryptionSnapshotSchemaAdapter
{
    public static EncryptionSnapshot FromDescriptor(RecordingFinalizationDescriptor descriptor)
        => RecordingEncryptionSchema.FromDescriptor(descriptor);
    public static EncryptionSnapshot FromMetadata(CallRecordingMetadata metadata)
        => RecordingEncryptionSchema.FromMetadata(metadata);
    public static void ApplyToMetadata(CallRecordingMetadata metadata, EncryptionSnapshot encryption, RadioMediaProtocol protocol)
        => RecordingEncryptionSchema.ApplyToMetadata(metadata, encryption, protocol);
    public static void ApplyToMetadata(CallRecordingMetadata metadata, EncryptionSnapshot encryption, FneTrafficProtocol protocol)
        => RecordingEncryptionSchema.ApplyToMetadata(metadata, encryption, EncryptionPresentation.ToMediaProtocol(protocol));
}
