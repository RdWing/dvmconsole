// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Presentation;
using Xunit;

using static DvmConsole.Desktop.Tests.ConfigurationStudioDraftTestBuilder;

namespace DvmConsole.Desktop.Tests;

public sealed class ConfigurationStudioFieldEditControllerTests
{
    [Fact]
    public void CommitRunsTheOrderedFieldTransactionOnce()
    {
        var session = new RecordingSession();
        var controller = new ConfigurationStudioFieldEditController(session);

        Assert.True(controller.Commit());

        Assert.Equal(["synchronize", "commit", "refresh", "notify"], session.Calls);
        Assert.Same(session.Snapshot, session.CommittedBefore);
    }

    [Fact]
    public void BindingFeedbackDuringRefreshDoesNotReenterCommit()
    {
        var session = new RecordingSession();
        var controller = new ConfigurationStudioFieldEditController(session);
        session.DuringRefresh = () => Assert.False(controller.Commit());

        Assert.True(controller.Commit());
        Assert.Equal(1, session.Calls.Count(call => call == "synchronize"));
    }

    private sealed class RecordingSession : IConfigurationStudioFieldEditSession
    {
        public ConfigurationStudioDraftSnapshot Snapshot { get; } = CreateSnapshot("draft", 1, 0, "North");
        public List<string> Calls { get; } = [];
        public Action? DuringRefresh { get; set; }
        public ConfigurationStudioDraftSnapshot? CommittedBefore { get; private set; }
        public bool CanEditFields => true;
        public bool IsCollectionRefreshInProgress { get; private set; }
        public bool IsRestoreBindingSettling => false;
        public ConfigurationStudioDraftSnapshot CurrentDraft => Snapshot;
        public void SynchronizeFieldState() => Calls.Add("synchronize");
        public void CommitFieldTransition(ConfigurationStudioDraftSnapshot before)
        {
            Calls.Add("commit");
            CommittedBefore = before;
        }
        public void RefreshFieldCollections()
        {
            Calls.Add("refresh");
            IsCollectionRefreshInProgress = true;
            try
            {
                DuringRefresh?.Invoke();
            }
            finally
            {
                IsCollectionRefreshInProgress = false;
            }
        }
        public void NotifySelectedChannelChanged() => Calls.Add("notify");
    }
}
