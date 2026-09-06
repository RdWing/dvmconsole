// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Desktop;
using DvmConsole.Application;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class RecordingRootOwnershipTests
{
    [Fact]
    public async Task ReplacementStoreAcquiresReleasedRootOnFirstRecording()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-root-replacement-" + Guid.NewGuid().ToString("N"));
        try
        {
            var outgoing = new DesktopRecordingStore(root, null, 0);
            await using var replacement = new DesktopRecordingStore(root, null, 0);
            Assert.False(replacement.CanWriteRecordings);
            await Assert.ThrowsAsync<IOException>(() => replacement.CreateAsync(
                CallId.New(), default, DateTimeOffset.UtcNow, "audio/wav").AsTask());
            await outgoing.DisposeAsync();
            await using IRecordingWriteHandle first = await replacement.CreateAsync(
                CallId.New(), default, DateTimeOffset.UtcNow, "audio/wav");
            await using IRecordingWriteHandle second = await replacement.CreateAsync(
                CallId.New(), default, DateTimeOffset.UtcNow, "audio/wav");
            Assert.True(replacement.CanWriteRecordings);
            Assert.Equal(2, replacement.ActivePaths.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task NewRecordingIsPrivateWithoutChangingCustomRootPermissions()
    {
        if (OperatingSystem.IsWindows())
            return;
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-private-recording-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        UnixFileMode original = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
        File.SetUnixFileMode(root, original);
        try
        {
            await using var store = new DesktopRecordingStore(root, null, 0);
            await using IRecordingWriteHandle handle = await store.CreateAsync(CallId.New(), default, DateTimeOffset.UtcNow, "audio/wav");
            string wave = Assert.Single(store.ActivePaths);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(wave));
            Assert.Equal(original, File.GetUnixFileMode(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SecondStoreCannotRecoverOrMutateAnOwnedRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-root-owner-" + Guid.NewGuid().ToString("N"));
        try
        {
            var owner = new DesktopRecordingStore(root, null, 0);
            await using (var reader = new DesktopRecordingStore(root, null, 0))
            {
                Assert.True(owner.CanWriteRecordings);
                Assert.False(reader.CanWriteRecordings);
                Assert.Contains("another console", reader.FinalizationHealth.LastError, StringComparison.Ordinal);
                Assert.Empty(reader.LoadRecordings());
                Assert.False(reader.TrySetRootPath(root, out string error));
                Assert.Contains("another console", error, StringComparison.Ordinal);
                await owner.DisposeAsync();
                Assert.True(reader.TrySetRootPath(root, out error), error);
                Assert.True(reader.CanWriteRecordings);
            }
            await using var recovered = new DesktopRecordingStore(root, null, 0);
            Assert.True(recovered.CanWriteRecordings);
        }
        finally { Directory.Delete(root, true); }
    }
}
