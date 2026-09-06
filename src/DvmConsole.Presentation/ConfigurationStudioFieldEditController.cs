// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;

namespace DvmConsole.Presentation;

internal interface IConfigurationStudioFieldEditSession
{
    bool CanEditFields { get; }
    bool IsCollectionRefreshInProgress { get; }
    bool IsRestoreBindingSettling { get; }
    ConfigurationStudioDraftSnapshot CurrentDraft { get; }
    void SynchronizeFieldState();
    void CommitFieldTransition(ConfigurationStudioDraftSnapshot before);
    void RefreshFieldCollections();
    void NotifySelectedChannelChanged();
}

/// <summary>
/// Serializes bound field commits and distinguishes user edits from selection
/// feedback raised while Studio rebuilds its projections.
/// </summary>
internal sealed class ConfigurationStudioFieldEditController
{
    private readonly IConfigurationStudioFieldEditSession session;
    private bool commitInProgress;

    public ConfigurationStudioFieldEditController(IConfigurationStudioFieldEditSession session)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public bool Commit()
    {
        if (!session.CanEditFields ||
            commitInProgress ||
            session.IsCollectionRefreshInProgress ||
            session.IsRestoreBindingSettling)
        {
            return false;
        }

        commitInProgress = true;
        try
        {
            ConfigurationStudioDraftSnapshot before = session.CurrentDraft;
            session.SynchronizeFieldState();
            session.CommitFieldTransition(before);
            session.RefreshFieldCollections();
            session.NotifySelectedChannelChanged();
            return true;
        }
        finally
        {
            commitInProgress = false;
        }
    }
}
