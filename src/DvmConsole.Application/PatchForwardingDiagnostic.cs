// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;

namespace DvmConsole.Application;

public enum PatchForwardingDiagnosticKind
{
    TargetStarted,
    TargetUnavailable,
    TargetFailed,
    TargetEnded,
    TargetOverloaded
}

public sealed record PatchForwardingDiagnostic(
    DateTimeOffset ObservedAt,
    PatchForwardingDiagnosticKind Kind,
    PatchMemberAddress Target,
    uint StreamId,
    string Message,
    Exception? Exception = null)
{
    public bool IsFailure => Kind is
        PatchForwardingDiagnosticKind.TargetUnavailable or
        PatchForwardingDiagnosticKind.TargetFailed or
        PatchForwardingDiagnosticKind.TargetOverloaded;
}
