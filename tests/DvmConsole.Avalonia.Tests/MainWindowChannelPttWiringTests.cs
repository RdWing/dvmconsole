// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.IO;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED source contract for channel-card PTT wiring. The card owns a
    /// momentary pointer lifecycle and the window disposes its coordinator.
    /// </summary>
    public sealed class MainWindowChannelPttWiringTests
    {
        [Fact]
        public void MainWindowSourceComposesAndDisposesTheChannelPttCoordinator()
        {
            var source = File.ReadAllText(SourcePath("MainWindow.axaml.cs"));

            Assert.Contains("ChannelPttRuntimeCoordinator? channelPttRuntimeCoordinator", source);
            Assert.Contains("channelPttRuntimeCoordinator = new ChannelPttRuntimeCoordinator(", source);
            Assert.Contains("channelPttRuntimeCoordinator.HandlePointerDownAsync(slot)", source);
            Assert.Contains("channelPttRuntimeCoordinator.HandlePointerUpAsync()", source);
            Assert.Contains("await channelPttRuntimeCoordinator.DisposeAsync()", source);
        }

        [Fact]
        public void ChannelCardXamlContainsIndependentMomentaryPttButtonLifecycle()
        {
            var source = File.ReadAllText(SourcePath("MainWindow.axaml"));

            Assert.Contains("Content=\"PTT\"", source);
            Assert.Contains("IsEnabled=\"{Binding CanChannelPtt}\"", source);
            Assert.Contains("PointerPressed=\"ChannelPttButton_PointerPressed\"", source);
            Assert.Contains("PointerReleased=\"ChannelPttButton_PointerReleased\"", source);
            Assert.Contains("PointerCaptureLost=\"ChannelPttButton_PointerCaptureLost\"", source);
        }

        private static string SourcePath(string fileName)
            => Path.Combine(
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../")),
                "DvmConsole.Avalonia",
                fileName);
    }
}
