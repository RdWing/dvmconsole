// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Settings;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class AlertToneViewModelTests
{
    [Fact]
    public void ManagedAssetUsesPortableStorageDescription()
    {
        AssetId assetId = AssetId.New();
        var viewModel = new AlertToneViewModel(new AlertToneSetting
        {
            Name = "Dispatch",
            AssetId = assetId.ToString(),
            FileName = "dispatch.wav"
        });

        Assert.True(viewModel.IsAvailable);
        Assert.Equal("Managed asset · dispatch.wav", viewModel.StorageText);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, viewModel.StorageText);
    }

    [Fact]
    public void LegacyAssetStillShowsItsMigrationSource()
    {
        string path = Path.Combine("legacy", "dispatch.wav");
        var viewModel = new AlertToneViewModel(new AlertToneSetting
        {
            Name = "Dispatch",
            FilePath = path
        });

        Assert.Equal(path, viewModel.StorageText);
    }
}
