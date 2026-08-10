// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dvmconsole;
using DvmConsole.Avalonia.ViewModels;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for the headless TAR configuration surface: WPF-compatible
    /// zone/channel projection and per-resource edits, ignored-RID parsing,
    /// recording-root validation, and save payload emission. XAML and settings
    /// persistence are later seams.
    /// </summary>
    public sealed class TarConfigurationViewModelTests
    {
        private static readonly string DefaultRoot = Path.Combine(Path.GetTempPath(), "dvmconsole-tar-default");

        [Fact]
        public void Constructor_BuildsZonesSkipsInvalidChannels_AndLoadsResourceConfig()
        {
            var zones = new[]
            {
                new Codeplug.Zone
                {
                    Name = "  Zone A ",
                    Channels = new List<Codeplug.Channel>
                    {
                        new() { Name = "Alpha", System = "Sys One", Tgid = "100", Mode = "dmr" },
                        new() { Name = "", System = "Sys One", Tgid = "101" },
                        new() { Name = "No TG", System = "Sys One", Tgid = "" },
                    },
                },
            };

            var viewModel = new TarConfigurationViewModel(
                zones,
                (key, _, _) => key == "sys one|100"
                    ? new TarChannelConfig
                    {
                        Enabled = true,
                        RetentionDays = 14,
                        IgnoredSubscriberIds = new List<uint> { 7, 2, 7 },
                    }
                    : new TarChannelConfig(),
                "  /tmp/tar-root  ",
                DefaultRoot);

            var group = Assert.Single(viewModel.ZoneGroups);
            Assert.Equal("Zone A", group.ZoneName);
            var item = Assert.Single(group.Channels);
            Assert.Equal("sys one|100", item.ResourceKey);
            Assert.Equal("SYS ONE", item.SystemName.ToUpperInvariant());
            Assert.Equal("DMR", item.Mode);
            Assert.True(item.Enabled);
            Assert.Equal(14, item.RetentionDays);
            Assert.Equal("2, 7", item.IgnoredSubscriberIdsText);
            Assert.Equal("/tmp/tar-root", viewModel.RecordingFolderPath);
        }

        [Fact]
        public void Constructor_WithNoUsableZones_CreatesResourcesGroup()
        {
            var viewModel = new TarConfigurationViewModel(
                new[]
                {
                    new Codeplug.Zone { Name = "Empty", Channels = null },
                    null!,
                },
                (_, _, _) => new TarChannelConfig(),
                "",
                DefaultRoot);

            var group = Assert.Single(viewModel.ZoneGroups);
            Assert.Equal("Resources", group.ZoneName);
            Assert.Empty(group.Channels);
            Assert.Equal(DefaultRoot, viewModel.RecordingFolderPath);
        }

        [Fact]
        public void SameResourceAcrossZones_SynchronizesEditedProperties()
        {
            var zones = new[]
            {
                new Codeplug.Zone
                {
                    Name = "A",
                    Channels = new List<Codeplug.Channel>
                    {
                        new() { Name = "One", System = "SYS", Tgid = "42", Mode = "p25" },
                    },
                },
                new Codeplug.Zone
                {
                    Name = "B",
                    Channels = new List<Codeplug.Channel>
                    {
                        new() { Name = "Two", System = "sys", Tgid = "42", Mode = "P25" },
                    },
                },
            };

            var viewModel = new TarConfigurationViewModel(
                zones,
                (_, _, _) => new TarChannelConfig(),
                Path.Combine(Path.GetTempPath(), "tar-sync"),
                DefaultRoot);

            var first = Assert.Single(viewModel.ZoneGroups[0].Channels);
            var second = Assert.Single(viewModel.ZoneGroups[1].Channels);

            first.Enabled = true;
            first.RetentionDays = 21;
            first.IgnoredSubscriberIdsText = "3, 9";

            Assert.True(second.Enabled);
            Assert.Equal(21, second.RetentionDays);
            Assert.Equal("3, 9", second.IgnoredSubscriberIdsText);
        }

        [Fact]
        public void Save_ParsesIgnoredIds_NormalizesRetention_AndRaisesPayloadOnce()
        {
            var root = Path.Combine(Path.GetTempPath(), "dvmconsole-tar-save-" + Guid.NewGuid().ToString("N"));
            try
            {
                var zones = new[]
                {
                    new Codeplug.Zone
                    {
                        Name = "Zone",
                        Channels = new List<Codeplug.Channel>
                        {
                            new() { Name = "TG", System = "SYS", Tgid = "9", Mode = "dmr" },
                        },
                    },
                };
                var viewModel = new TarConfigurationViewModel(
                    zones,
                    (_, _, _) => new TarChannelConfig(),
                    root,
                    DefaultRoot);
                var item = Assert.Single(viewModel.ZoneGroups[0].Channels);
                item.Enabled = true;
                item.RetentionDays = -4;
                item.IgnoredSubscriberIdsText = "9; 2 9, 5";

                string? savedRoot = null;
                IReadOnlyDictionary<string, TarChannelConfig>? saved = null;
                var saveCount = 0;
                viewModel.SaveRequested += (path, configs) =>
                {
                    saveCount++;
                    savedRoot = path;
                    saved = configs;
                };

                Assert.True(viewModel.Save());
                Assert.Equal(1, saveCount);
                Assert.Equal(root, savedRoot);
                var config = Assert.Single(saved!);
                Assert.Equal("sys|9", config.Key);
                Assert.True(config.Value.Enabled);
                Assert.Equal(0, config.Value.RetentionDays);
                Assert.Equal(new uint[] { 2, 5, 9 }, config.Value.IgnoredSubscriberIds);
                Assert.Equal("Changes saved.", viewModel.StatusText);
                Assert.Empty(viewModel.ErrorText);
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        [Theory]
        [InlineData("0", "'0' is not a valid subscriber ID.")]
        [InlineData("abc", "'abc' is not a valid subscriber ID.")]
        public void Save_InvalidIgnoredId_RejectsWithoutRaisingPayload(string text, string expectedError)
        {
            var root = Path.Combine(Path.GetTempPath(), "dvmconsole-tar-invalid-" + Guid.NewGuid().ToString("N"));
            try
            {
                var viewModel = new TarConfigurationViewModel(
                    new[]
                    {
                        new Codeplug.Zone
                        {
                            Name = "Zone",
                            Channels = new List<Codeplug.Channel>
                            {
                                new() { Name = "TG", System = "SYS", Tgid = "9" },
                            },
                        },
                    },
                    (_, _, _) => new TarChannelConfig(),
                    root,
                    DefaultRoot);
                Assert.Single(viewModel.ZoneGroups).Channels[0].IgnoredSubscriberIdsText = text;
                var saveCount = 0;
                viewModel.SaveRequested += (_, _) => saveCount++;

                Assert.False(viewModel.Save());
                Assert.Equal(0, saveCount);
                Assert.Equal(expectedError, viewModel.ErrorText);
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void Save_WhitespaceRoot_RejectsWithoutRaisingPayload()
        {
            var viewModel = new TarConfigurationViewModel(
                Array.Empty<Codeplug.Zone>(),
                (_, _, _) => new TarChannelConfig(),
                "   ",
                "   ");

            var saveCount = 0;
            viewModel.SaveRequested += (_, _) => saveCount++;

            Assert.False(viewModel.Save());
            Assert.Equal(0, saveCount);
            Assert.Contains("empty or whitespace-only", viewModel.ErrorText);
        }
    }
}
