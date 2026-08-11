// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.IO;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// Gate 0.2 RED contracts for TAR viewer playback cleanup and filter-status
    /// synchronization. These source pins keep the shell headless on Linux.
    /// </summary>
    public sealed class TarViewerRobustnessGateTests
    {
        [Fact]
        public void StopPlaybackCleanupUsesIdentityCheckAfterAwaitingStop()
        {
            string source = File.ReadAllText(SourcePath());

            Assert.Contains("await waveFilePlayer.StopAsync()", source);
            Assert.True(
                Count(source, "ReferenceEquals(playbackCancellation,") >= 2,
                "The active-playback completion and StopPlaybackAsync must both preserve a newer playback session.");
        }

        [Fact]
        public void FilterChangesAreObservedAndRefreshStatusFromCurrentRows()
        {
            string source = File.ReadAllText(SourcePath());

            Assert.Contains("viewModel.PropertyChanged += ViewModel_PropertyChanged;", source);
            Assert.Contains("private void ViewModel_PropertyChanged", source);
            Assert.Contains("e.PropertyName", source);
            Assert.True(
                Count(source, "viewModel.Rows.Count == 0") >= 2,
                "The filter-change handler must apply the same empty/non-empty status rule as initial refresh.");
        }

        private static string SourcePath()
            => Path.Combine(
                RepositoryRoot(),
                "DvmConsole.Avalonia",
                "Views",
                "TarViewerWindow.axaml.cs");

        private static string RepositoryRoot()
            => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

        private static int Count(string source, string value)
        {
            int count = 0;
            int index = 0;
            while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }

            return count;
        }
    }
}
