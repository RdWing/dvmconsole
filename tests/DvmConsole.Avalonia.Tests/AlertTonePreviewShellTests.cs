// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.IO;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for Gate 5.2 preview shell wiring. Validation and playback
    /// stay injected; the alert manager must stop prior/closed previews.
    /// </summary>
    public sealed class AlertTonePreviewShellTests
    {
        [Fact]
        public void AlertToneWindowContainsInjectedPreviewActionsAndShutdownStop()
        {
            string source = File.ReadAllText(WindowSourcePath());
            string xaml = File.ReadAllText(WindowXamlPath());

            Assert.Contains("IAudioWaveFileInspector", source);
            Assert.Contains("IAudioWaveFilePlayer", source);
            Assert.Contains("PreviewAsync", source);
            Assert.Contains("StopAsync", source);
            Assert.Contains("OnClosed", source);
            Assert.Contains("Preview_Click", xaml);
            Assert.Contains("Stop_Click", xaml);
            Assert.Contains("StatusText", xaml);
        }

        [Fact]
        public void MainWindowAndAppComposeASeparateAlertPreviewPlayerWithoutCtorChange()
        {
            string app = File.ReadAllText(AppSourcePath());
            string window = File.ReadAllText(MainWindowSourcePath());

            Assert.Contains("alertWaveFilePlayer", app);
            Assert.Contains("CreateWaveFilePlayer", app);
            Assert.Contains("AttachAlertTonePreview", window);
            Assert.Contains("IAudioWaveFileInspector", window);
            Assert.Contains("IAudioWaveFilePlayer", window);
        }

        private static string WindowSourcePath()
            => Path.Combine(RepositoryRoot(), "DvmConsole.Avalonia", "Views", "AlertToneManagerWindow.axaml.cs");

        private static string WindowXamlPath()
            => Path.Combine(RepositoryRoot(), "DvmConsole.Avalonia", "Views", "AlertToneManagerWindow.axaml");

        private static string AppSourcePath()
            => Path.Combine(RepositoryRoot(), "DvmConsole.Avalonia", "App.axaml.cs");

        private static string MainWindowSourcePath()
            => Path.Combine(RepositoryRoot(), "DvmConsole.Avalonia", "MainWindow.axaml.cs");

        private static string RepositoryRoot()
            => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    }
}
