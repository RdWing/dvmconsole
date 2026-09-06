// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Presentation;
using Xunit;
using static DvmConsole.Desktop.Tests.ConfigurationStudioDraftTestBuilder;

namespace DvmConsole.Desktop.Tests;

public sealed class ConfigurationStudioEditTransactionsTests
{
    [Theory]
    [InlineData(ConfigurationStudioSection.Zones, "field")]
    [InlineData(ConfigurationStudioSection.Streams, "field")]
    [InlineData(ConfigurationStudioSection.Groups, "field")]
    [InlineData(ConfigurationStudioSection.EncryptionKeys, "key")]
    [InlineData(ConfigurationStudioSection.Files, "alias")]
    public void PendingCommitUsesTheActiveEditor(ConfigurationStudioSection section, string expected)
    {
        var port = new Port { Section = section };
        var transactions = Create(port);
        transactions.CommitPending();
        Assert.Equal(expected, port.Calls.First());
    }

    [Fact]
    public void RefreshPreservesSelectionAndRejectsReentrantBindingCommits()
    {
        var port = new Port();
        var transactions = Create(port);
        port.DuringRefresh = () =>
        {
            Assert.True(transactions.IsRefreshing);
            transactions.Refresh(true);
            transactions.CommitField();
        };
        transactions.Refresh(true);
        Assert.Equal(["capture names", "refresh"], port.Calls);
        Assert.Same(port.Names, port.RefreshedSelection);
        Assert.False(transactions.IsRefreshing);
    }

    [Fact]
    public void FailedRefreshReleasesGuardForTheNextEdit()
    {
        var port = new Port();
        var transactions = Create(port);
        port.DuringRefresh = () => throw new InvalidOperationException("projection failure");
        Assert.Throws<InvalidOperationException>(() => transactions.Refresh());
        Assert.False(transactions.IsRefreshing);
        port.DuringRefresh = null;
        transactions.CommitField();
        Assert.Contains("field", port.Calls);
    }

    [Fact]
    public void FieldCommitFailureDoesNotLeaveTheEditorLocked()
    {
        var port = new Port();
        var transactions = Create(port);
        port.DuringRefresh = () => throw new InvalidOperationException("projection failure");
        Assert.Throws<InvalidOperationException>(transactions.CommitField);
        port.DuringRefresh = null;
        transactions.CommitField();
        Assert.Equal(2, port.Calls.Count(call => call == "field"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HistoryRestoresDraftBeforeIdentitySelectionAndNotifications(bool undo)
    {
        var port = new Port();
        var transactions = Create(port);
        ConfigurationStudioDraftSnapshot initial = port.Snapshot;
        port.Snapshot = CreateSnapshot("changed");
        transactions.RecordTransition(initial, true);
        if (!undo)
            transactions.Undo();
        port.Calls.Clear();
        if (undo) transactions.Undo(); else transactions.Redo();
        Assert.Equal(undo ? initial.Yaml : "changed", transactions.Draft.Current.Yaml);
        Assert.Equal(undo
            ? ["capture ids", "apply", "capture draft", "refresh", "restore selection", "notify"]
            : new[] { "capture ids", "apply", "capture draft", "dirty", "refresh", "restore selection", "notify" }, port.Calls);
        Assert.Same(port.Ids, port.RestoredSelection);
    }

    [Fact]
    public void ReadOnlyHistoryDoesNotConsumeUndo()
    {
        var port = new Port();
        var transactions = Create(port);
        var before = port.Snapshot;
        port.Snapshot = CreateSnapshot("changed");
        transactions.RecordTransition(before, true);
        port.IsReadOnly = true;
        port.Calls.Clear();
        transactions.Undo();
        Assert.Empty(port.Calls);
        Assert.True(transactions.Draft.CanUndo);
    }

    [Fact]
    public void OlderDispatcherCompletionCannotReleaseNewerHistorySettlement()
    {
        var transactions = Create(new Port());
        int first = transactions.BeginBindingSettlement();
        int second = transactions.BeginBindingSettlement();
        transactions.CompleteBindingSettlement(first);
        Assert.True(transactions.Draft.IsRestoreBindingSettling);
        transactions.CompleteBindingSettlement(second);
        Assert.False(transactions.Draft.IsRestoreBindingSettling);
    }

    [Fact]
    public void BoundFieldFeedbackIsIgnoredUntilHistorySettlementCompletes()
    {
        var port = new Port();
        var transactions = Create(port);
        int version = transactions.BeginBindingSettlement();
        transactions.CommitField();
        Assert.Empty(port.Calls);
        transactions.CompleteBindingSettlement(version);
        transactions.CommitField();
        Assert.Contains("field", port.Calls);
    }

    [Fact]
    public void CompanionOnlyChangeRecordsHistoryWithoutMarkingYamlDirty()
    {
        var port = new Port();
        var transactions = Create(port);
        var before = port.Snapshot;
        port.Snapshot = CreateSnapshot(before.Yaml, keyId: 2);
        transactions.RecordTransition(before, true);
        Assert.True(transactions.Draft.IsDirty);
        Assert.True(transactions.Draft.CanUndo);
        Assert.DoesNotContain("dirty", port.Calls);
    }

    private static ConfigurationStudioEditTransactions Create(Port port)
    {
        var transactions = new ConfigurationStudioEditTransactions(port, port);
        port.Transactions = transactions;
        transactions.Initialize(port.Snapshot, false);
        return transactions;
    }

    private sealed class Port : IConfigurationStudioEditTransactionPort, IConfigurationStudioFieldEditSession
    {
        public ConfigurationStudioEditTransactions Transactions { get; set; } = null!;
        public ConfigurationStudioDraftSnapshot Snapshot { get; set; } = CreateSnapshot("initial");
        public ConfigurationStudioSelectionNames Names { get; } = new("system", "zone", "channel", "stream", "group", "alias");
        public ConfigurationStudioSelectionIds Ids { get; } = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        public ConfigurationStudioSelectionNames? RefreshedSelection { get; private set; }
        public ConfigurationStudioSelectionIds? RestoredSelection { get; private set; }
        public List<string> Calls { get; } = [];
        public Action? DuringRefresh { get; set; }
        public bool IsReadOnly { get; set; }
        public ConfigurationStudioSection Section { get; set; }
        public void CommitKeyEditor() => Calls.Add("key");
        public void CommitAliasEditor() => Calls.Add("alias");
        public ConfigurationStudioDraftSnapshot CaptureDraft() { Calls.Add("capture draft"); return Snapshot; }
        public void MarkDocumentDirty() => Calls.Add("dirty");
        public ConfigurationStudioSelectionNames CaptureSelectionNames() { Calls.Add("capture names"); return Names; }
        public void RefreshEditorCollections(ConfigurationStudioSelectionNames? selection)
        {
            Calls.Add("refresh");
            RefreshedSelection = selection;
            DuringRefresh?.Invoke();
        }
        public ConfigurationStudioSelectionIds CaptureSelectionIds() { Calls.Add("capture ids"); return Ids; }
        public void ApplyDraft(ConfigurationStudioDraftSnapshot snapshot) { Calls.Add("apply"); Snapshot = snapshot; }
        public void RestoreSelection(ConfigurationStudioSelectionIds selection) { Calls.Add("restore selection"); RestoredSelection = selection; }
        public void NotifyDraftRestored() => Calls.Add("notify");
        public bool CanEditFields => !IsReadOnly;
        public bool IsCollectionRefreshInProgress => Transactions.IsRefreshing;
        public bool IsRestoreBindingSettling => Transactions.Draft.IsRestoreBindingSettling;
        public ConfigurationStudioDraftSnapshot CurrentDraft => Transactions.Draft.Current;
        public void SynchronizeFieldState() => Calls.Add("field");
        public void CommitFieldTransition(ConfigurationStudioDraftSnapshot before) => Transactions.RecordTransition(before, true);
        public void RefreshFieldCollections() => Transactions.Refresh(true);
        public void NotifySelectedChannelChanged() => Calls.Add("notify channel");
    }
}
