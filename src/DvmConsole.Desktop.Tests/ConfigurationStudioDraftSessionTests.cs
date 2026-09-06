// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Presentation;
using Xunit;

using static DvmConsole.Desktop.Tests.ConfigurationStudioDraftTestBuilder;

namespace DvmConsole.Desktop.Tests;

public sealed class ConfigurationStudioDraftSessionTests
{
    [Fact]
    public void SavedBaselineTracksTransitionsUndoAndRedo()
    {
        ConfigurationStudioDraftSnapshot initial = CreateSnapshot("initial");
        ConfigurationStudioDraftSnapshot edited = CreateSnapshot("edited", keyId: 2);
        var session = new ConfigurationStudioDraftSession(initial, startsDirty: false);

        session.RecordTransition(initial, edited);

        Assert.True(session.IsDirty);
        Assert.True(session.CanUndo);
        Assert.True(session.TryUndo(out ConfigurationStudioDraftSnapshot? undone));
        Assert.Same(initial, undone);
        session.ReplaceCurrent(undone!);
        Assert.False(session.IsDirty);

        Assert.True(session.TryRedo(out ConfigurationStudioDraftSnapshot? redone));
        Assert.Same(edited, redone);
        session.ReplaceCurrent(redone!);
        Assert.True(session.IsDirty);

        session.AcceptSaved(edited);
        Assert.False(session.IsDirty);
        Assert.False(session.CanUndo);
        Assert.False(session.CanRedo);
    }

    [Fact]
    public void PreviewMoveAndBindingSettlementHaveExplicitLifetimes()
    {
        ConfigurationStudioDraftSnapshot initial = CreateSnapshot("initial");
        ConfigurationStudioDraftSnapshot moved = CreateSnapshot("moved", x: 40);
        var session = new ConfigurationStudioDraftSession(initial, startsDirty: true);

        Assert.True(session.IsDirty);
        session.BeginPreviewMove();
        session.ReplaceCurrent(moved);
        session.BeginPreviewMove();

        Assert.True(session.TryCompletePreviewMove(out ConfigurationStudioDraftSnapshot? before));
        Assert.Same(initial, before);
        Assert.False(session.TryCompletePreviewMove(out _));

        session.BeginRestoreBindingSettlement();
        Assert.True(session.IsRestoreBindingSettling);
        session.CompleteRestoreBindingSettlement();
        Assert.False(session.IsRestoreBindingSettling);
    }
}
