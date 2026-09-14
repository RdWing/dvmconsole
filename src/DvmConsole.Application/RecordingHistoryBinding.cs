// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>Attaches session-owned finalized media to History independently of catalog presentation.</summary>
internal sealed class RecordingHistoryBinding : IDisposable
{
    private readonly IRecordingCallAttachmentSource source;
    private readonly ConsoleCallHistory history;
    private readonly Action? changed;

    public RecordingHistoryBinding(IRecordingCallAttachmentSource source, ConsoleCallHistory history,
        Action? changed = null)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.history = history ?? throw new ArgumentNullException(nameof(history));
        this.changed = changed;
        source.RecordingAttached += Attach;
    }

    private void Attach(RecordingCallIdentity identity)
    {
        // History owns synchronization. Finalization can complete during quiescence;
        // hosts decide whether a resulting presentation notification is still useful.
        if (history.AttachRecording(identity) is not null) changed?.Invoke();
    }

    public void Dispose() => source.RecordingAttached -= Attach;
}
