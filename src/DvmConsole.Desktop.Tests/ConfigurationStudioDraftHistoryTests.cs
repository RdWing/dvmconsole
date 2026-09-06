// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using Xunit;

using static DvmConsole.Desktop.Tests.ConfigurationStudioDraftTestBuilder;

namespace DvmConsole.Desktop.Tests;

public sealed class ConfigurationStudioDraftHistoryTests
{
    [Fact]
    public void UndoAndRedoRestoreWholeDraftSnapshots()
    {
        var history = new ConfigurationStudioDraftHistory();
        ConfigurationStudioDraftSnapshot before = CreateSnapshot("before", keyId: 1, x: 10, system: "North");
        ConfigurationStudioDraftSnapshot after = CreateSnapshot("after", keyId: 2, x: 40, system: "South");

        history.Record(before, after);

        ConfigurationStudioDraftSnapshot undone = Assert.IsType<ConfigurationStudioDraftSnapshot>(history.Undo(after));
        Assert.Equal("before", undone.Yaml);
        Assert.Contains("KeyId: 1", undone.ReferencedFiles.KeyFileContent);
        Assert.Equal(10, Assert.Single(undone.WidgetPositions).Value.X);
        Assert.Equal("North", Assert.Single(undone.ZoneSystemAssignments).Value);
        Assert.Single(undone.CallPrioritySystemIds);
        Assert.True(history.CanRedo);

        ConfigurationStudioDraftSnapshot redone = Assert.IsType<ConfigurationStudioDraftSnapshot>(history.Redo(undone));
        Assert.Equal("after", redone.Yaml);
        Assert.Contains("KeyId: 2", redone.ReferencedFiles.KeyFileContent);
        Assert.Equal(40, Assert.Single(redone.WidgetPositions).Value.X);
        Assert.Equal("South", Assert.Single(redone.ZoneSystemAssignments).Value);
        Assert.Single(redone.CallPrioritySystemIds);
    }

    [Fact]
    public void IdenticalSnapshotsDoNotCreateUndoEntries()
    {
        var history = new ConfigurationStudioDraftHistory();
        ConfigurationStudioDraftSnapshot snapshot = CreateSnapshot("same", keyId: 1, x: 10, system: "North");

        history.Record(snapshot, snapshot);

        Assert.False(history.CanUndo);
        Assert.Null(history.Undo(snapshot));
    }

    [Fact]
    public void UndoHistoryRetainsOnlyTheMostRecentHundredEntries()
    {
        var history = new ConfigurationStudioDraftHistory();
        ConfigurationStudioDraftSnapshot current = CreateSnapshot("0", 0, 0, "North");
        for (int index = 1; index <= 110; index++)
        {
            ConfigurationStudioDraftSnapshot next = CreateSnapshot(
                index.ToString(),
                index,
                index,
                "North");
            history.Record(current, next);
            current = next;
        }

        var restored = new List<string>();
        while (history.Undo(current) is { } previous)
        {
            restored.Add(previous.Yaml);
            current = previous;
        }

        Assert.Equal(100, restored.Count);
        Assert.Equal("10", restored[^1]);
    }

}
