// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Settings;
using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class DesktopStartupSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationPreservesStartupSelectionAndOwnsUnpublishedResources(bool demo)
    {
        string root = Path.Combine(Path.GetTempPath(), "neo-startup", Guid.NewGuid().ToString("N"));
        var store = new UserSettingsStore(Path.Combine(root, "settings.json"));
        store.Save(new UserSettings { RecordingRootPath = Path.Combine(root, "recordings") });
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        try
        {
            await using var prepared = await DesktopStartupSession.PrepareAsync(path, store, demo);
            await prepared.InitializeViewModelAsync();
            Assert.True(prepared.ViewModel.IsCodeplugLoaded);
            if (demo)
            {
                Assert.NotNull(prepared.ActiveConfiguration);
                Assert.NotNull(prepared.Materialization);
                Assert.Null(prepared.PendingImportPath);
                await using var reopening = await DesktopStartupSession.PrepareAsync(null, store, demo);
                Assert.Equal(prepared.ActiveConfiguration, reopening.PendingActiveConfiguration);
                await reopening.InitializeViewModelAsync();
                Assert.Empty(reopening.ViewModel.Systems);
            }
            else
            {
                Assert.Equal(path, prepared.PendingImportPath);
                Assert.Null(prepared.ActiveConfiguration);
                Assert.Null(prepared.Materialization);
            }
            await prepared.DisposeAsync();
            Assert.True(prepared.ViewModel.IsSessionInputSuppressed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task OwnershipTransferKeepsTheWindowSessionAlive()
    {
        string root = Path.Combine(Path.GetTempPath(), "neo-startup-transfer", Guid.NewGuid().ToString("N"));
        var store = new UserSettingsStore(Path.Combine(root, "settings.json"));
        try
        {
            await using var prepared = await DesktopStartupSession.PrepareAsync(null, store, false);
            await prepared.InitializeViewModelAsync();
            try
            {
                prepared.TransferOwnership();
                await prepared.DisposeAsync();
                Assert.False(prepared.ViewModel.IsSessionInputSuppressed);
            }
            finally { await prepared.ViewModel.DisposeAsync(); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
