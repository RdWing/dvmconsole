// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Threading;
using DvmConsole.Application;
using DvmConsole.Core.Diagnostics;
using DvmConsole.Presentation;

namespace DvmConsole.Mobile;

/// <summary>Retains session logs while pages change, using the desktop retention and redaction pipeline.</summary>
internal sealed class MobileSessionDiagnostics : IDisposable
{
    private readonly IConsoleApplicationSession session;
    private int disposed;

    public MobileSessionDiagnostics(IConsoleApplicationSession session)
    {
        this.session = session;
        Workspace = new DebugLogWorkspace(Dispatcher.UIThread.CheckAccess,
            action => Dispatcher.UIThread.Post(action), () => Volatile.Read(ref disposed) != 0);
        session.LogPublished += OnLog;
    }

    public DebugLogWorkspace Workspace { get; }

    private void OnLog(object? sender, ConsoleLogEvent entry)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        DebugLogSeverity severity = entry.Level switch
        {
            ConsoleLogLevel.Trace or ConsoleLogLevel.Debug => DebugLogSeverity.Debug,
            ConsoleLogLevel.Information => DebugLogSeverity.Info,
            ConsoleLogLevel.Warning => DebugLogSeverity.Warning,
            _ => DebugLogSeverity.Error
        };
        Workspace.Add(entry.Timestamp, entry.Category, severity, entry.Message);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        session.LogPublished -= OnLog;
        Workspace.Dispose();
    }
}
