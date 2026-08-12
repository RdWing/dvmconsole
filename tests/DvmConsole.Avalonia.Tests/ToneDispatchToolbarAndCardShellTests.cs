// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.IO;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    public sealed class ToneDispatchToolbarAndCardShellTests
    {
        [Fact]
        public void MainWindowExposesThreeAlertActionsAndCardPageMarkerActions()
        {
            string xaml = File.ReadAllText(Path.Combine(RepositoryRoot(), "DvmConsole.Avalonia", "MainWindow.axaml"));
            string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "DvmConsole.Avalonia", "MainWindow.axaml.cs"));

            Assert.Contains("AlertTone1_Click", xaml);
            Assert.Contains("AlertTone2_Click", xaml);
            Assert.Contains("AlertTone3_Click", xaml);
            Assert.Contains("AlertTone1_Click", source);
            Assert.Contains("AlertTone2_Click", source);
            Assert.Contains("AlertTone3_Click", source);
            Assert.Contains("alert1.wav", source);
            Assert.Contains("alert2.wav", source);
            Assert.Contains("alert3.wav", source);
            Assert.Contains("ReadAndSendWaveFileAsync", source);

            Assert.Contains("PageSelect_Click", xaml);
            Assert.Contains("Marker_Click", xaml);
            Assert.Contains("RequestPageSelect", source);
            Assert.Contains("RequestMarker", source);
        }

        [Fact]
        public void AlertToolbarUsesTargetSnapshotAndDoesNotClaimMonitorOnlySuccess()
        {
            string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "DvmConsole.Avalonia", "MainWindow.axaml.cs"));
            int alert1 = source.IndexOf("AlertTone1_Click", StringComparison.Ordinal);
            Assert.True(alert1 >= 0);
            string tail = source[alert1..];
            Assert.Contains("ResolveToneDispatchTargets", tail);
            Assert.Contains("Where(slot => slot.PageState)", tail);
            Assert.Contains("pageSelectedTargets", tail);
            Assert.Contains("ReadAndSendWaveFileAsync", tail);
            Assert.Contains("SendAlertToolbarTone", tail);
            Assert.Contains("AudioStatusMessage", tail);
        }

        private static string RepositoryRoot()
            => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    }
}
