// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Settings;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ConsoleSessionCompositionTests
{
    [Fact]
    public async Task SessionAppliesTheCodeplugPatchSourceIdPassthroughSetting()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dvmconsole-session-{Guid.NewGuid():N}");
        string codeplugPath = Path.Combine(root, "passthrough.yml");
        Directory.CreateDirectory(root);
        File.WriteAllText(codeplugPath, """
            patchSourceIdPassthrough: true
            systems:
              - name: Test
                identity: Test Console
                address: 127.0.0.1
                port: 62031
                peerId: 1
                rid: "1001"
            zones:
              - name: Operations
                channels:
                  - name: Dispatch
                    system: Test
                    tgid: "100"
                    mode: analog
                web_streams:
                  - name: Dispatch feed
                    url: "https://example.test/live"
            groups:
              - name: Dispatch Patch
                type: patch
            """);

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(
                codeplugPath,
                new UserSettingsStore(Path.Combine(root, "settings.json")));

            Assert.True(viewModel.IsCodeplugLoaded);
            Assert.True(viewModel.PatchSourceIdPassthroughEnabled);
            WebStreamViewModel stream = Assert.Single(viewModel.WebStreams);
            ZoneViewModel zone = Assert.Single(Assert.Single(viewModel.Systems).Zones);
            Assert.Same(stream, Assert.Single(zone.WebStreamCards));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WebStreamCardAppearsOnlyUnderItsZonesOwningSystem()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dvmconsole-session-{Guid.NewGuid():N}");
        string codeplugPath = Path.Combine(root, "streams.yml");
        Directory.CreateDirectory(root);
        File.WriteAllText(codeplugPath, """
            systems:
              - name: Primary
                identity: Primary Console
                address: 127.0.0.1
                port: 62031
                peerId: 1
                rid: "1001"
              - name: Secondary
                identity: Secondary Console
                address: 127.0.0.1
                port: 62032
                peerId: 2
                rid: "1002"
            zones:
              - name: Operations
                channels:
                  - name: Dispatch
                    system: Primary
                    tgid: "100"
                    mode: analog
                web_streams:
                  - name: Dispatch feed
                    url: "https://example.test/live"
            """);

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(
                codeplugPath,
                new UserSettingsStore(Path.Combine(root, "settings.json")));

            WebStreamViewModel stream = Assert.Single(viewModel.WebStreams);
            SystemViewModel primary = Assert.Single(viewModel.Systems, system => system.Name == "Primary");
            ZoneViewModel zone = Assert.Single(primary.Zones);
            Assert.Same(stream, Assert.Single(zone.WebStreamCards));
            Assert.Empty(Assert.Single(viewModel.Systems, system => system.Name == "Secondary").Zones);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WebStreamCardPositionPersistsAndResetsWithTheZoneLayout()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dvmconsole-stream-layout-{Guid.NewGuid():N}");
        string codeplugPath = Path.Combine(root, "streams.yml");
        string settingsPath = Path.Combine(root, "settings.json");
        Directory.CreateDirectory(root);
        File.WriteAllText(codeplugPath, """
            systems:
              - name: Primary
                identity: Primary Console
                address: 127.0.0.1
                port: 62031
                peerId: 1
                rid: "1001"
            zones:
              - name: Operations
                channels:
                  - name: Dispatch
                    system: Primary
                    tgid: "100"
                    mode: analog
                web_streams:
                  - name: Dispatch feed
                    url: "https://example.test/live"
            """);
        var store = new UserSettingsStore(settingsPath);
        store.Save(new UserSettings { LockWidgets = false });

        try
        {
            string streamKey;
            await using (MainWindowViewModel viewModel = MainWindowViewModel.Load(codeplugPath, store))
            {
                WebStreamViewModel stream = Assert.Single(viewModel.WebStreams);
                streamKey = stream.SettingsKey;
                viewModel.MoveWebStreamWidget(stream, 347, 186, persist: true);

                Assert.Equal(347, stream.WidgetX);
                Assert.Equal(186, stream.WidgetY);
                await viewModel.FlushUserSettingsAsync();
                Assert.Equal(347, store.Load().ChannelWidgetPositions[streamKey].X);
            }

            await using MainWindowViewModel restored = MainWindowViewModel.Load(codeplugPath, store);
            WebStreamViewModel restoredStream = Assert.Single(restored.WebStreams);
            Assert.Equal(347, restoredStream.WidgetX);
            Assert.Equal(186, restoredStream.WidgetY);

            restored.ResetLayout();

            Assert.True(restored.LockWidgets);
            await restored.FlushUserSettingsAsync();
            Assert.Empty(store.Load().ChannelWidgetPositions);
            Assert.NotEqual(347, restoredStream.WidgetX);
            Assert.NotEqual(186, restoredStream.WidgetY);

            double resetX = restoredStream.WidgetX;
            double resetY = restoredStream.WidgetY;
            restored.MoveWebStreamWidget(restoredStream, 500, 500, persist: true);

            Assert.Equal(resetX, restoredStream.WidgetX);
            Assert.Equal(resetY, restoredStream.WidgetY);
            Assert.Empty(store.Load().ChannelWidgetPositions);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RejectedConfigurationBuildsPresentationWithoutKeyOrConnectionServices()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dvmconsole-session-{Guid.NewGuid():N}");
        string codeplugPath = Path.Combine(root, "invalid.yml");
        string settingsPath = Path.Combine(root, "settings.json");
        Directory.CreateDirectory(root);
        File.WriteAllText(codeplugPath, """
            keyFile: "missing-keys.clear"
            systems: []
            zones:
              - name: Streams
                channels: []
                web_streams:
                  - name: Dispatch
                    url: "https://example.test/live"
            """);

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(
                codeplugPath,
                new UserSettingsStore(settingsPath));

            Assert.False(viewModel.IsCodeplugLoaded);
            Assert.Empty(viewModel.Systems);
            Assert.Single(viewModel.Zones);
            Assert.Single(viewModel.WebStreams);
            Assert.StartsWith("Configuration has 1 validation error(s):", viewModel.StatusText);
            Assert.DoesNotContain("Encryption keys unavailable", viewModel.StatusText, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
