// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DvmConsole.Avalonia.ViewModels;
using dvmconsole;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for Gate 5.1: managed alert-tone CRUD and request-only save
    /// semantics. Dialogs and persistence remain shell-owned.
    /// </summary>
    public sealed class AlertToneManagerViewModelTests
    {
        [Fact]
        public void LoadRenameAndReplace_PreserveStableId_AndProjectAvailability()
        {
            var vm = new AlertToneManagerViewModel(
                new[]
                {
                    new UserSettingsAlertToneConfig
                    {
                        Id = "stable-alert",
                        DisplayName = "Old name",
                        FilePath = "missing.wav",
                        TabName = "Alerts",
                        Position = new UserSettingsLayoutPosition { X = 12, Y = 34 },
                    },
                },
                new[] { "Alerts", "Dispatch" },
                path => string.Equals(path, "present.wav", StringComparison.Ordinal));

            var item = Assert.Single(vm.AlertTones);
            Assert.Equal("stable-alert", item.Id);
            Assert.False(item.IsAvailable);
            Assert.Equal("Alerts", item.TabName);

            item.DisplayName = "Renamed";
            vm.ReplaceFile(item, "present.wav");

            Assert.Equal("stable-alert", item.Id);
            Assert.Equal("Renamed", item.DisplayName);
            Assert.Equal("present.wav", item.FilePath);
            Assert.True(item.IsAvailable);
            Assert.Equal(12, item.Position.X);
            Assert.Equal(34, item.Position.Y);
        }

        [Fact]
        public void AddDeleteAndCommit_UsesStableIds_DropsEmptyFiles_AndRaisesSnapshot()
        {
            var vm = new AlertToneManagerViewModel(
                Array.Empty<UserSettingsAlertToneConfig>(),
                Array.Empty<string>(),
                _ => true);
            IReadOnlyList<UserSettingsAlertToneConfig>? saved = null;
            vm.SaveRequested += snapshot => saved = snapshot;

            vm.AddFiles(new[] { "one.wav", "one.wav", "two.wav" });

            Assert.Equal("Tab 1", vm.AlertTones[0].TabName);
            Assert.Equal(2, vm.AlertTones.Count);
            Assert.All(vm.AlertTones, item => Assert.Matches("^[0-9a-f]{32}$", item.Id));

            var removed = vm.AlertTones[0];
            vm.Delete(removed);
            vm.Commit();

            var committed = Assert.Single(saved!);
            Assert.Equal("two.wav", committed.FilePath);
            Assert.NotEqual(removed.Id, committed.Id);
            Assert.Equal("Tab 1", committed.TabName);
        }

        [Fact]
        public void ViewModelSource_DoesNotPerformFileSystemWork()
        {
            string path = Path.Combine(
                RepositoryRoot(),
                "DvmConsole.Avalonia",
                "ViewModels",
                "AlertToneManagerViewModel.cs");
            string source = File.ReadAllText(path);

            Assert.DoesNotContain("using System.IO", source, StringComparison.Ordinal);
            Assert.DoesNotContain("File.Exists", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Directory.", source, StringComparison.Ordinal);
        }

        private static string RepositoryRoot()
            => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    }
}
