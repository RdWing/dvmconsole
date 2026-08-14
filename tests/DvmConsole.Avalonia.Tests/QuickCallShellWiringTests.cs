// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.IO;
using Avalonia.Controls;
using DvmConsole.Avalonia;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    public sealed class QuickCallShellWiringTests
    {
        [Fact]
        public void QuickCallMenuItem_IsInertWithoutAWindow()
        {
            NativeMenuItem item = App.CreateQuickCallMenuItem(null);

            Assert.Equal("Quick Call II", item.Header);
            Assert.False(item.IsEnabled);
        }

        [Fact]
        public void QuickCallShell_UsesTheCoordinatorAndPageStateBoundary()
        {
            string app = File.ReadAllText(AppSourcePath());
            string window = File.ReadAllText(MainWindowSourcePath());
            string xaml = File.ReadAllText(WindowXamlPath());

            Assert.Contains("CreateQuickCallMenuItem", app);
            Assert.Contains("Quick Call II", app);
            Assert.Contains("OpenManualQuickCall", app);
            Assert.Contains("commandsMenu.Items.Add(quickCallItem)", app);
            Assert.Contains("SendQuickCallAsync", window);
            Assert.Contains("PageState", window);
            Assert.Contains("slot.IsSelected && slot.PageState && !slot.IsRxOnly", window);
            Assert.Contains("ClearPageStateAfterSend", window);
            Assert.Contains("SendGeneratedPcmAsync", window);
            Assert.Contains("sendStartSignal: true", window);
            Assert.Contains("Dispatcher.UIThread.Post", window);
            Assert.Contains("slot.PageState = false", window);
            Assert.Contains("x:DataType=\"vm:QuickCallViewModel\"", xaml);
            Assert.Contains("ToneA", xaml);
            Assert.Contains("ToneB", xaml);
        }

        private static string RepositoryRoot()
            => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

        private static string AppSourcePath()
            => Path.Combine(RepositoryRoot(), "DvmConsole.Avalonia", "App.axaml.cs");

        private static string MainWindowSourcePath()
            => Path.Combine(RepositoryRoot(), "DvmConsole.Avalonia", "MainWindow.axaml.cs");

        private static string WindowXamlPath()
            => Path.Combine(
                RepositoryRoot(),
                "DvmConsole.Avalonia",
                "Views",
                "QuickCallWindow.axaml");
    }
}
