// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dvmconsole;
using DvmConsole.Avalonia.Persistence;
using DvmConsole.Avalonia.ViewModels;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// Parent RED for Gate 1.2: WPF-parity column descriptors, unknown-key
    /// no-op behavior, merge-preserving persistence, reopen defaults, and the
    /// view-model boundary that keeps column toggles separate from rows/filters.
    /// </summary>
    public sealed class TarViewerColumnVisibilityGateTests
    {
        [Fact]
        public void DefaultsExposeWpfColumnOrderAndVisibility()
        {
            using var temp = new TemporaryDirectory();
            var model = new TarViewerColumnVisibilityModel(
                new TarViewerColumnSettingsPersistence(new SettingsSectionStore(temp.SettingsPath)));

            Assert.Equal(
                new[] { "Time", "Duration", "Channel", "Talkgroup", "SourceId", "Alias", "Direction", "Protocol", "System", "Encryption" },
                model.Columns.Select(column => column.Key));
            Assert.Equal(
                new[] { "Time", "Duration", "Channel", "TG", "Source ID", "Alias", "Dir", "Protocol", "System", "Enc" },
                model.Columns.Select(column => column.Header));
            Assert.Equal(
                new[] { 155d, 90d, 190d, 90d, 90d, 160d, 60d, 85d, 140d, 120d },
                model.Columns.Select(column => column.Width));
            Assert.Equal(
                new[] { true, true, true, true, true, true, false, false, false, false },
                model.Columns.Select(column => column.IsVisible));
        }

        [Fact]
        public void ToggleEveryKnownColumnAndIgnoreUnknownKeys()
        {
            using var temp = new TemporaryDirectory();
            var model = new TarViewerColumnVisibilityModel(
                new TarViewerColumnSettingsPersistence(new SettingsSectionStore(temp.SettingsPath)));
            var notifications = new List<string>();
            foreach (TarViewerColumnDescriptor column in model.Columns)
            {
                column.PropertyChanged += (_, args) => notifications.Add(args.PropertyName ?? string.Empty);
            }

            foreach (TarViewerColumnDescriptor column in model.Columns)
                Assert.True(model.TrySetVisibility(column.Key, !column.IsVisible));

            Assert.Equal(
                new[] { false, false, false, false, false, false, true, true, true, true },
                model.Columns.Select(column => column.IsVisible));
            Assert.Equal(10, notifications.Count(name => name == nameof(TarViewerColumnDescriptor.IsVisible)));
            Assert.False(model.TrySetVisibility("UnknownColumn", true));
            Assert.DoesNotContain(model.Columns, column => column.Key == "UnknownColumn");
        }

        [Fact]
        public void SaveThenReopen_PreservesVisibilityAndUnrelatedSettings()
        {
            using var temp = new TemporaryDirectory();
            File.WriteAllText(
                temp.SettingsPath,
                "{\"KeepMe\":true,\"ColumnVisibility\":{\" direction \":true,\"UnknownColumn\":false,\" \":true}}");
            var persistence = new TarViewerColumnSettingsPersistence(new SettingsSectionStore(temp.SettingsPath));
            var model = new TarViewerColumnVisibilityModel(persistence);

            Assert.True(model.TrySetVisibility("Direction", true));
            Assert.True(model.TrySetVisibility("Alias", false));
            model.Save();

            JObject json = JObject.Parse(File.ReadAllText(temp.SettingsPath));
            Assert.True(json["KeepMe"]!.Value<bool>());
            Assert.NotNull(json["ColumnVisibility"]);

            var reopened = new TarViewerColumnVisibilityModel(persistence);
            Assert.Equal(
                model.Columns.Select(column => column.IsVisible),
                reopened.Columns.Select(column => column.IsVisible));
            Assert.True(reopened.Columns.Single(column => column.Key == "Direction").IsVisible);
            Assert.False(reopened.Columns.Single(column => column.Key == "Alias").IsVisible);
            Assert.False(reopened.Columns.Single(column => column.Key == "Protocol").IsVisible);
        }

        [Fact]
        public void ColumnToggleDoesNotChangeRowsOrFilterState()
        {
            using var temp = new TemporaryDirectory();
            var recorder = new TarRecorder(
                temp.Root,
                temp.Root,
                (_, _, _) => new TarChannelConfig { Enabled = true });
            var metadata = new TarRecordingMetadata
            {
                Direction = TarRecordingDirection.RX,
                RecordingSourceType = TarRecordingSourceType.InboundRadio,
                Protocol = "DMR",
                UtcStartTime = DateTime.UtcNow,
                SystemName = "System A",
                ChannelName = "Dispatch",
                TalkgroupId = 77,
                SubscriberId = 456,
                SubscriberAlias = "Radio 456",
                FileName = "dispatch.wav",
                FilePath = Path.Combine(temp.Root, "dispatch.wav"),
                UtcEndTime = DateTime.UtcNow.AddSeconds(2),
                DurationMs = 2000,
                StreamId = 1,
            };
            File.WriteAllBytes(metadata.FilePath, new byte[] { 0, 1, 2, 3 });
            File.WriteAllText(
                Path.Combine(temp.Root, metadata.FileName + ".json"),
                JsonConvert.SerializeObject(metadata));
            var model = new TarViewerViewModel(
                recorder,
                new TarViewerColumnSettingsPersistence(new SettingsSectionStore(temp.SettingsPath)));

            model.Refresh(rebuildIndex: true);
            Assert.Single(model.Rows);
            Assert.Equal(10, model.Rows[0].Cells.Count);
            Assert.Equal("Dispatch", model.Rows[0].Cells.Single(cell => cell.Column.Key == "Channel").Value);

            model.SearchText = "dispatch";
            Assert.Single(model.Rows);
            Assert.True(model.ColumnVisibility.TrySetVisibility("System", true));
            Assert.Equal("dispatch", model.SearchText);
            Assert.Single(model.Rows);
        }

        private sealed class TemporaryDirectory : IDisposable
        {
            public TemporaryDirectory()
            {
                Root = Path.Combine(Path.GetTempPath(), "dvmconsole-column-gate-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Root);
                SettingsPath = Path.Combine(Root, "settings.json");
            }

            public string Root { get; }
            public string SettingsPath { get; }

            public void Dispose()
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
        }
    }
}
