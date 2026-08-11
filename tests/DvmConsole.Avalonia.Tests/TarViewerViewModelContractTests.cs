// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using DvmConsole.Avalonia.Persistence;
using DvmConsole.Avalonia.ViewModels;
using dvmconsole;
using Newtonsoft.Json;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for the headless TAR recording list/view-model slice.
    /// Playback, file reveal, confirmation dialogs, and window composition
    /// remain later Avalonia seams.
    /// </summary>
    public sealed class TarViewerViewModelContractTests
    {
        [Fact]
        public void Shape_HasOnePublicConstructorAndWpfFilterDefaults()
        {
            var type = typeof(TarViewerViewModel);
            var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);

            Assert.Equal("DvmConsole.Avalonia.ViewModels", type.Namespace);
            Assert.True(type.IsPublic);
            Assert.True(type.IsSealed);
            Assert.Single(constructors);
            Assert.Equal(
                new[] { typeof(TarRecorder), typeof(TarViewerColumnSettingsPersistence) },
                constructors[0].GetParameters().Select(parameter => parameter.ParameterType));
            Assert.True(constructors[0].GetParameters()[1].IsOptional);
            Assert.Null(constructors[0].GetParameters()[1].DefaultValue);

            using var temp = new TempDirectory();
            var viewModel = new TarViewerViewModel(CreateRecorder(temp.Root));

            Assert.Empty(viewModel.Rows);
            Assert.Equal("All", viewModel.SelectedDirectionFilter);
            Assert.Equal("All", viewModel.SelectedProtocolFilter);
            Assert.Equal("All", viewModel.SelectedEncryptionFilter);
            Assert.Equal(string.Empty, viewModel.SearchText);
            Assert.Null(viewModel.StartDateFilter);
            Assert.Null(viewModel.EndDateFilter);
        }

        [Fact]
        public void Constructor_RejectsNullRecorder()
        {
            Assert.Throws<ArgumentNullException>(() => new TarViewerViewModel(null!));
        }

        [Fact]
        public void Refresh_ProjectsWpfRowsNewestFirstWithStableDisplayFields()
        {
            using var temp = new TempDirectory();
            DateTime older = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);
            DateTime newer = older.AddHours(1);
            var olderMetadata = CreateMetadata(temp.Root, "older.wav", older, TarRecordingDirection.RX);
            olderMetadata.DurationMs = -10;
            olderMetadata.Protocol = "P25";
            olderMetadata.SystemName = "System A";
            olderMetadata.ChannelName = "Dispatch";
            olderMetadata.TalkgroupId = 123;
            olderMetadata.SubscriberId = 456;
            olderMetadata.SubscriberAlias = "Radio 456";
            olderMetadata.IsEncrypted = false;
            var newerMetadata = CreateMetadata(temp.Root, "newer.wav", newer, TarRecordingDirection.TX);
            newerMetadata.DurationMs = 2345;
            newerMetadata.Protocol = "DMR";
            newerMetadata.SystemName = "System B";
            newerMetadata.ChannelName = "Operations";
            newerMetadata.TalkgroupId = 789;
            newerMetadata.SubscriberId = null;
            newerMetadata.SubscriberAlias = null;
            newerMetadata.IsEncrypted = true;
            newerMetadata.EncryptionAlgorithm = "AES-256";
            newerMetadata.EncryptionKeyId = 0x2A;
            WriteRecording(temp.Root, olderMetadata);
            WriteRecording(temp.Root, newerMetadata);

            var viewModel = new TarViewerViewModel(CreateRecorder(temp.Root));
            viewModel.Refresh(rebuildIndex: true);

            Assert.Equal(2, viewModel.Rows.Count);
            var row = viewModel.Rows[0];
            Assert.Equal(newerMetadata.FileName, row.Metadata.FileName);
            Assert.Equal(newer, row.UtcStartSortKey);
            Assert.Equal(newer.ToLocalTime(), row.LocalStartTime);
            Assert.Equal(row.LocalStartTime.ToString("g", CultureInfo.CurrentCulture), row.LocalStartDisplay);
            Assert.Equal("TX", row.Direction);
            Assert.Equal("DMR", row.Protocol);
            Assert.Equal("System B", row.SystemName);
            Assert.Equal("Operations", row.ChannelName);
            Assert.Equal("789", row.TalkgroupId);
            Assert.Equal(string.Empty, row.SubscriberId);
            Assert.Equal(string.Empty, row.SubscriberAlias);
            Assert.Equal("00:00:02", row.DurationDisplay);
            Assert.Equal("AES-256 / 002A", row.EncryptionSummary);

            Assert.Equal(olderMetadata.FileName, viewModel.Rows[1].Metadata.FileName);
            Assert.Equal("Clear", viewModel.Rows[1].EncryptionSummary);
        }

        [Fact]
        public void Filters_RebuildRowsAcrossWpfFilterFields()
        {
            using var temp = new TempDirectory();
            var rx = CreateMetadata(temp.Root, "rx.wav", new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc), TarRecordingDirection.RX);
            rx.Protocol = "P25";
            rx.SystemName = "Alpha";
            rx.ChannelName = "Dispatch";
            rx.TalkgroupId = 123;
            rx.SubscriberId = 456;
            rx.SubscriberAlias = "Alice";
            rx.IsEncrypted = true;
            var tx = CreateMetadata(temp.Root, "tx.wav", new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc), TarRecordingDirection.TX);
            tx.Protocol = "DMR";
            tx.SystemName = "Bravo";
            tx.ChannelName = "Operations";
            tx.TalkgroupId = 789;
            tx.SubscriberId = 999;
            tx.SubscriberAlias = "Bob";
            tx.IsEncrypted = false;
            WriteRecording(temp.Root, rx);
            WriteRecording(temp.Root, tx);

            var viewModel = new TarViewerViewModel(CreateRecorder(temp.Root));
            viewModel.Refresh(rebuildIndex: true);
            Assert.Equal(2, viewModel.Rows.Count);

            viewModel.SelectedDirectionFilter = "RX";
            Assert.Single(viewModel.Rows);
            viewModel.ClearFilters();
            viewModel.SelectedProtocolFilter = "DMR";
            Assert.Single(viewModel.Rows);
            viewModel.ClearFilters();
            viewModel.SelectedEncryptionFilter = "Encrypted";
            Assert.Single(viewModel.Rows);
            viewModel.ClearFilters();
            viewModel.SystemFilter = "rav";
            Assert.Single(viewModel.Rows);
            viewModel.ClearFilters();
            viewModel.ChannelFilter = "oper";
            Assert.Single(viewModel.Rows);
            viewModel.ClearFilters();
            viewModel.TalkgroupFilter = "123";
            Assert.Single(viewModel.Rows);
            viewModel.ClearFilters();
            viewModel.SourceIdFilter = "456";
            Assert.Single(viewModel.Rows);
            viewModel.ClearFilters();
            viewModel.AliasFilter = "ali";
            Assert.Single(viewModel.Rows);
            viewModel.ClearFilters();
            viewModel.SearchText = "p25";
            Assert.Single(viewModel.Rows);
            viewModel.ClearFilters();
            DateTime txLocalDate = tx.UtcStartTime.ToLocalTime().Date;
            viewModel.StartDateFilter = txLocalDate;
            viewModel.EndDateFilter = txLocalDate;
            Assert.Single(viewModel.Rows);
        }

        [Fact]
        public void FilterProperties_RaiseChangeOnlyNotifications()
        {
            using var temp = new TempDirectory();
            var metadata = CreateMetadata(temp.Root, "one.wav", new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc), TarRecordingDirection.RX);
            WriteRecording(temp.Root, metadata);
            var viewModel = new TarViewerViewModel(CreateRecorder(temp.Root));
            viewModel.Refresh(rebuildIndex: true);
            var notifications = new List<string?>();
            viewModel.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

            viewModel.SearchText = "one";
            viewModel.SearchText = "one";
            viewModel.ClearFilters();

            Assert.Equal(2, notifications.Count(name => name == nameof(TarViewerViewModel.SearchText)));
            Assert.Contains(nameof(TarViewerViewModel.SearchText), notifications);
        }

        private static TarRecorder CreateRecorder(string root)
            => new(root, root, (_, _, _) => new TarChannelConfig { Enabled = true });

        private static TarRecordingMetadata CreateMetadata(
            string root,
            string fileName,
            DateTime start,
            TarRecordingDirection direction)
        {
            string filePath = Path.Combine(root, fileName);
            return new TarRecordingMetadata
            {
                Direction = direction,
                RecordingSourceType = direction == TarRecordingDirection.RX
                    ? TarRecordingSourceType.InboundRadio
                    : TarRecordingSourceType.ConsoleTx,
                Protocol = "P25",
                UtcStartTime = start,
                UtcEndTime = start.AddSeconds(2),
                DurationMs = 2000,
                FilePath = filePath,
                FileName = fileName,
                SystemName = "System",
                ChannelName = "Channel",
                TalkgroupName = "Channel",
                StreamId = 7
            };
        }

        private static void WriteRecording(string root, TarRecordingMetadata metadata)
        {
            File.WriteAllBytes(metadata.FilePath, new byte[] { 0, 1, 2, 3 });
            string sidecarPath = Path.Combine(root, metadata.FileName + ".json");
            File.WriteAllText(sidecarPath, JsonConvert.SerializeObject(metadata, Formatting.Indented));
        }

        private sealed class TempDirectory : IDisposable
        {
            public TempDirectory()
            {
                Root = Path.Combine(Path.GetTempPath(), "dvmconsole-tar-viewer-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Root);
            }

            public string Root { get; }

            public void Dispose()
            {
                try
                {
                    if (Directory.Exists(Root))
                        Directory.Delete(Root, recursive: true);
                }
                catch (IOException)
                {
                    // Best-effort cleanup; never mask the test result.
                }
            }
        }
    }
}
