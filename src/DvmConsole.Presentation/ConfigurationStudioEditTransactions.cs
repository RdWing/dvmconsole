// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;

namespace DvmConsole.Presentation;

internal sealed record ConfigurationStudioSelectionNames(
    string? System, string? Zone, string? Channel, string? Stream,
    string? Group, string? AliasSystem);

internal sealed record ConfigurationStudioSelectionIds(
    Guid? System, Guid? Zone, Guid? Channel, Guid? Stream, Guid? Group);

internal interface IConfigurationStudioEditTransactionPort
{
    bool IsReadOnly { get; }
    ConfigurationStudioSection Section { get; }
    void CommitKeyEditor();
    void CommitAliasEditor();
    ConfigurationStudioDraftSnapshot CaptureDraft();
    void MarkDocumentDirty();
    ConfigurationStudioSelectionNames CaptureSelectionNames();
    void RefreshEditorCollections(ConfigurationStudioSelectionNames? selection);
    ConfigurationStudioSelectionIds CaptureSelectionIds();
    void ApplyDraft(ConfigurationStudioDraftSnapshot snapshot);
    void RestoreSelection(ConfigurationStudioSelectionIds selection);
    void NotifyDraftRestored();
}

/// <summary>
/// Orders commits, projection refresh and history restoration. The view-model
/// supplies editor-specific projections; views only schedule binding settlement.
/// </summary>
internal sealed class ConfigurationStudioEditTransactions
{
    private readonly IConfigurationStudioEditTransactionPort port;
    private readonly ConfigurationStudioFieldEditController fields;
    private ConfigurationStudioDraftSession? draft;
    private int settlementVersion;

    public ConfigurationStudioEditTransactions(
        IConfigurationStudioEditTransactionPort port,
        IConfigurationStudioFieldEditSession fieldSession)
    {
        this.port = port ?? throw new ArgumentNullException(nameof(port));
        fields = new ConfigurationStudioFieldEditController(fieldSession);
    }

    public ConfigurationStudioDraftSession Draft => draft
        ?? throw new InvalidOperationException("The initial Studio draft has not been captured.");
    public bool IsRefreshing { get; private set; }

    public void Initialize(ConfigurationStudioDraftSnapshot initial, bool startsDirty)
    {
        if (draft is not null)
            throw new InvalidOperationException("The Studio draft is already initialized.");
        draft = new ConfigurationStudioDraftSession(initial, startsDirty);
    }

    public void CommitField() => fields.Commit();

    public void CommitPending()
    {
        switch (port.Section)
        {
            case ConfigurationStudioSection.EncryptionKeys:
                port.CommitKeyEditor();
                break;
            case ConfigurationStudioSection.Files:
                port.CommitAliasEditor();
                break;
            default:
                CommitField();
                break;
        }
    }

    public void RecordTransition(ConfigurationStudioDraftSnapshot before, bool markDocumentDirty)
    {
        ConfigurationStudioDraftSnapshot after = port.CaptureDraft();
        Draft.RecordTransition(before, after);
        if (markDocumentDirty && !string.Equals(before.Yaml, after.Yaml, StringComparison.Ordinal))
            port.MarkDocumentDirty();
    }

    public void Refresh(bool preserveSelection = false)
    {
        if (IsRefreshing)
            return;
        IsRefreshing = true;
        try
        {
            ConfigurationStudioSelectionNames? selection = preserveSelection
                ? port.CaptureSelectionNames() : null;
            port.RefreshEditorCollections(selection);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    public void Undo() => Restore(undo: true);
    public void Redo() => Restore(undo: false);

    private void Restore(bool undo)
    {
        if (port.IsReadOnly)
            return;
        bool available = undo
            ? Draft.TryUndo(out ConfigurationStudioDraftSnapshot? snapshot)
            : Draft.TryRedo(out snapshot);
        if (!available)
            return;
        ConfigurationStudioSelectionIds selection = port.CaptureSelectionIds();
        port.ApplyDraft(snapshot!);
        Draft.ReplaceCurrent(port.CaptureDraft());
        if (Draft.IsDirty)
            port.MarkDocumentDirty();
        Refresh();
        port.RestoreSelection(selection);
        port.NotifyDraftRestored();
    }

    public int BeginBindingSettlement()
    {
        Draft.BeginRestoreBindingSettlement();
        return ++settlementVersion;
    }

    public void CompleteBindingSettlement(int version)
    {
        if (version == settlementVersion)
            Draft.CompleteRestoreBindingSettlement();
    }
}
