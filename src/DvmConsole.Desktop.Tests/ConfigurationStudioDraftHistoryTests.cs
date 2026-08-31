using DvmConsole.Application;
using DvmConsole.Core.Settings;
using Xunit;

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

    private static ConfigurationStudioDraftSnapshot CreateSnapshot(
        string yaml,
        int keyId,
        double x,
        string system)
    {
        Guid systemId = Guid.NewGuid();
        Guid zoneId = Guid.NewGuid();
        Guid channelId = Guid.NewGuid();
        var references = new ConfigurationStudioReferencedFilesSnapshot(
            null,
            null,
            string.Empty,
            null,
            null,
            false,
            $"KeyId: {keyId}",
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            [],
            [],
            string.Empty);
        string fingerprint = ConfigurationStudioDraftSnapshot.ComputeFingerprint(
            [yaml, references.KeyFileContent, x.ToString(), system]);
        return new ConfigurationStudioDraftSnapshot(
            yaml,
            new ConfigurationDraftIdentityLayout(
                [systemId],
                [new ConfigurationZoneIdentityLayout(zoneId, [channelId], [])],
                []),
            references,
            new Dictionary<Guid, WidgetPositionSetting>
            {
                [channelId] = new() { X = x, Y = 20 }
            },
            new Dictionary<Guid, string> { [zoneId] = system },
            new HashSet<Guid> { systemId },
            fingerprint);
    }
}
