#nullable enable
using System.Collections.Generic;
using dvmconsole;
using DvmConsole.Avalonia.Services;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Platform.Audio;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    public sealed class WebStreamShellProjectionTests
    {
        [Fact]
        public void Projection_RestoresKnownStreamsAndIgnoresStaleNames()
        {
            var projection = new WebStreamShellProjection(
                new[]
                {
                    new WebStreamShellDefinition(
                        new Codeplug.WebStream { Name = " Dispatch ", Url = "https://radio.example.test/dispatch" },
                        "North"),
                    new WebStreamShellDefinition(
                        new Codeplug.WebStream { Name = "News", Url = "https://radio.example.test/news" },
                        "North"),
                    new WebStreamShellDefinition(
                        new Codeplug.WebStream { Name = "Missing URL" },
                        "North"),
                },
                restoreSelected: true,
                selectedNames: new[] { "dispatch", "stale" },
                volumes: new Dictionary<string, double> { ["Dispatch"] = 2.34 },
                positions: new Dictionary<string, UserSettingsLayoutPosition>
                {
                    ["North|Dispatch"] = new() { X = 12.5, Y = 34.75 },
                });

            Assert.Equal(2, projection.Items.Count);
            var item = Assert.Single(
                projection.Items,
                candidate => candidate.DisplayName == "Dispatch");
            Assert.Equal("Dispatch", item.DisplayName);
            Assert.True(item.ShouldRestoreActive);
            Assert.Equal(2.3, item.Volume);
            Assert.Equal(new WebStreamShellPosition(12.5, 34.75), item.Position);
        }

        [Fact]
        public void Projection_DisablesRestoreWhenPreferenceIsOffAndUsesSafeDefaults()
        {
            var projection = new WebStreamShellProjection(
                new[]
                {
                    new WebStreamShellDefinition(
                        new Codeplug.WebStream { Name = "News", Url = "https://radio.example.test/news" },
                        "North"),
                },
                restoreSelected: false,
                selectedNames: new[] { "News" },
                volumes: new Dictionary<string, double> { ["News"] = double.NaN },
                positions: new Dictionary<string, UserSettingsLayoutPosition>());

            var item = Assert.Single(projection.Items);
            Assert.False(item.ShouldRestoreActive);
            Assert.Equal(1.0, item.Volume);
            Assert.Equal(new WebStreamShellPosition(20, 20), item.Position);
        }

        [Fact]
        public async Task ShellViewModel_WithoutFactoriesSnapshotsStateWithoutCreatingPlayback()
        {
            var shell = new WebStreamShellViewModel(
                new[]
                {
                    new WebStreamShellDefinition(
                        new Codeplug.WebStream { Name = "News", Url = "https://radio.example.test/news" },
                        "North"),
                },
                restoreSelected: true,
                selectedNames: new[] { "News" },
                volumes: new Dictionary<string, double> { ["News"] = 1.7 },
                positions: new Dictionary<string, UserSettingsLayoutPosition>(),
                sourceFactory: null,
                audioFactory: null,
                outputDevice: () => AudioDeviceId.Default);

            Assert.False(shell.CanPlay);
            var item = Assert.Single(shell.Items);
            Assert.True(item.ShouldRestoreActive);
            await item.StartAsync();
            Assert.Equal("Off", item.StatusText);

            item.SetPosition(44, 55);
            var snapshot = shell.Snapshot();
            Assert.Empty(snapshot.SelectedNames);
            Assert.Equal(1.7, snapshot.Volumes["News"]);
            Assert.Equal(44, snapshot.Positions["North|News"].X);
            Assert.Equal(55, snapshot.Positions["North|News"].Y);

            await shell.DisposeAsync();
        }
    }
}