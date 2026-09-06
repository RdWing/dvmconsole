// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;

namespace DvmConsole.Presentation;

/// <summary>
/// Owns Configuration Studio's draft checkpoint, dirty baseline, undo/redo,
/// preview-drag transaction, and binding-settlement state. Snapshot projection
/// and restoration remain separate because they depend on the live editor
/// collaborators.
/// </summary>
internal sealed class ConfigurationStudioDraftSession
{
    private readonly ConfigurationStudioDraftHistory history = new();
    private string savedFingerprint;
    private ConfigurationStudioDraftSnapshot? previewMoveStart;

    public ConfigurationStudioDraftSession(
        ConfigurationStudioDraftSnapshot initial,
        bool startsDirty)
    {
        Current = initial ?? throw new ArgumentNullException(nameof(initial));
        savedFingerprint = startsDirty ? string.Empty : initial.Fingerprint;
    }

    public ConfigurationStudioDraftSnapshot Current { get; private set; }
    public bool IsDirty => !string.Equals(
        Current.Fingerprint,
        savedFingerprint,
        StringComparison.Ordinal);
    public bool CanUndo => history.CanUndo;
    public bool CanRedo => history.CanRedo;
    public bool IsRestoreBindingSettling { get; private set; }

    public void RecordTransition(
        ConfigurationStudioDraftSnapshot before,
        ConfigurationStudioDraftSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        history.Record(before, after);
        Current = after;
    }

    public bool TryUndo(out ConfigurationStudioDraftSnapshot? snapshot)
    {
        snapshot = history.Undo(Current);
        return snapshot is not null;
    }

    public bool TryRedo(out ConfigurationStudioDraftSnapshot? snapshot)
    {
        snapshot = history.Redo(Current);
        return snapshot is not null;
    }

    public void ReplaceCurrent(ConfigurationStudioDraftSnapshot snapshot)
        => Current = snapshot ?? throw new ArgumentNullException(nameof(snapshot));

    public void AcceptSaved(ConfigurationStudioDraftSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        history.Clear();
        Current = snapshot;
        savedFingerprint = snapshot.Fingerprint;
        previewMoveStart = null;
    }

    public void BeginPreviewMove()
        => previewMoveStart ??= Current;

    public bool TryCompletePreviewMove(out ConfigurationStudioDraftSnapshot? before)
    {
        before = previewMoveStart;
        previewMoveStart = null;
        return before is not null;
    }

    public void BeginRestoreBindingSettlement()
        => IsRestoreBindingSettling = true;

    public void CompleteRestoreBindingSettlement()
        => IsRestoreBindingSettling = false;
}
