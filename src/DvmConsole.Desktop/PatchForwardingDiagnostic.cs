using DvmConsole.Core.Runtime;

namespace DvmConsole.Desktop;

public enum PatchForwardingDiagnosticKind
{
    TargetStarted,
    TargetUnavailable,
    TargetFailed,
    TargetEnded
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
        PatchForwardingDiagnosticKind.TargetFailed;
}
