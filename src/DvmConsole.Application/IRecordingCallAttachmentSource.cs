// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>Session-owned, successfully finalized media identities, independent of catalog UI.</summary>
public interface IRecordingCallAttachmentSource
{
    event Action<RecordingCallIdentity>? RecordingAttached;
}
