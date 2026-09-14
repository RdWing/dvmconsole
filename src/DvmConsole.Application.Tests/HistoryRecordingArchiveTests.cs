// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class HistoryRecordingArchiveTests
{
    [Fact]
    public async Task DeletingOneRecordingRetainsAttachmentUntilLastRecordingIsRemoved()
    {
        var history = new ConsoleCallHistory();
        var call = CreateCall();
        history.Add(call);
        var first = Recording(call);
        var second = Recording(call);
        var source = new Archive { Entries = [first, second] };
        var archive = new HistoryRecordingArchive(source, history);
        await archive.LoadAsync();
        Assert.True(history.Find(call.Id)!.HasRecording);
        Assert.True(await archive.DeleteAsync(first.Id));
        Assert.True(history.Find(call.Id)!.HasRecording);
        Assert.True(await archive.DeleteAsync(second.Id));
        Assert.False(history.Find(call.Id)!.HasRecording);
    }

    [Fact]
    public async Task CatalogChangedDuringReadCannotOverwriteNewFinalization()
    {
        var history = new ConsoleCallHistory();
        var call = CreateCall();
        history.Add(call);
        var source = new Archive();
        source.DuringRead = () =>
        {
            source.Revision++;
            history.AttachRecording(RecordingCallIdentity.FromCall(call));
        };
        var archive = new HistoryRecordingArchive(source, history);
        await archive.LoadAsync();
        Assert.True(history.Find(call.Id)!.HasRecording);
        source.DuringRead = null;
        await archive.LoadAsync();
        Assert.False(history.Find(call.Id)!.HasRecording);
        history.Clear();
        source.Entries = [Recording(call)];
        await archive.LoadAsync();
        Assert.Empty(history.Snapshot);
    }

    private static ConsoleCallHistoryRecord CreateCall()
        => new(CallId.New(), DateTimeOffset.UnixEpoch, null, SystemId.FromName("North"), "North", null,
            "Dispatch", RadioMediaProtocol.P25, 1001, 2001, 10, [10], null, "Unit 1001",
            ConsoleCallDirection.Receive, RecordingEncryptionDescriptor.Clear, "", "", "", "");

    private static RecordingArchiveEntry Recording(ConsoleCallHistoryRecord call)
        => new(new RecordingId(Guid.NewGuid()), call.StartedAt, TimeSpan.FromSeconds(1),
            call.SystemName, call.ChannelName, "RX", "P25", "1001", "2001", "", "", [10],
            CallRecordingEncryptionState.Clear, "Clear", true, "test.opus", "")
        { CallIdentity = RecordingCallIdentity.FromCall(call) };

    private sealed class Archive : IConsoleRecordingArchive
    {
        public long Revision { get; set; }
        public IReadOnlyList<RecordingArchiveEntry> Entries { get; set; } = [];
        public Action? DuringRead { get; set; }
        public Task<IReadOnlyList<RecordingArchiveEntry>> LoadAsync(CancellationToken cancellationToken = default)
        {
            var entries = Entries;
            DuringRead?.Invoke();
            return Task.FromResult(entries);
        }
        public Task ExportAsync(RecordingId id, Stream destination, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task<bool> DeleteAsync(RecordingId id, CancellationToken cancellationToken = default)
        {
            var updated = Entries.Where(entry => entry.Id != id).ToArray();
            if (updated.Length == Entries.Count) return Task.FromResult(false);
            Entries = updated;
            Revision++;
            return Task.FromResult(true);
        }
    }
}
