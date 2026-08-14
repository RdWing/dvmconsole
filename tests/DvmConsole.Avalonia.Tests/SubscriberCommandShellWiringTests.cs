// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using DvmConsole.Core.Networking;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    public sealed class SubscriberCommandShellWiringTests
    {
        [Fact]
        public void AppCreatesSubscriberCommandsSubmenuWithAllWpfCommands()
        {
            NativeMenuItem item = DvmConsole.Avalonia.App.CreateSubscriberCommandsMenuItem(null);

            Assert.Equal("Commands", item.Header);
            Assert.False(item.IsEnabled);
            Assert.NotNull(item.Menu);
            Assert.Equal(
                new[] { "Page Subscriber", "Radio Check Subscriber", "Inhibit Subscriber", "Uninhibit Subscriber" },
                item.Menu!.Items.OfType<NativeMenuItem>().Select(child => child.Header));
            Assert.All(item.Menu.Items.OfType<NativeMenuItem>(), child => Assert.False(child.IsEnabled));
        }

        [Fact]
        public void SubscriberCommandDialogUsesCompiledBindingsAndViewModelSubmitBoundary()
        {
            string source = File.ReadAllText(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "DvmConsole.Avalonia",
                    "Views",
                    "SubscriberCommandWindow.axaml"));
            string codeBehind = File.ReadAllText(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "DvmConsole.Avalonia",
                    "Views",
                    "SubscriberCommandWindow.axaml.cs"));
            string mainWindow = File.ReadAllText(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "DvmConsole.Avalonia",
                    "MainWindow.axaml.cs"));
            string app = File.ReadAllText(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "DvmConsole.Avalonia",
                    "App.axaml.cs"));
            string mainWindowXaml = File.ReadAllText(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "DvmConsole.Avalonia",
                    "MainWindow.axaml"));

            Assert.Contains("x:DataType=\"vm:SubscriberCommandViewModel\"", source);
            Assert.Contains("ItemsSource=\"{Binding Systems}\"", source);
            Assert.Contains("SelectedItem=\"{Binding SelectedSystemOption", source);
            Assert.Contains("Click=\"Submit_Click\"", source);
            Assert.Contains("SubmitAsync", codeBehind);
            Assert.Contains("CreateSubscriberCommandsMenuItem", app);
            Assert.Contains("<NativeMenuItem Header=\"Commands\">", mainWindowXaml);
            Assert.Contains("AnyConnected", app);
            Assert.Contains("OpenSubscriberCommand", mainWindow);
        }

        [Fact]
        public void CommandKindsRemainThePortableCoreContract()
        {
            Assert.Equal(4, Enum.GetValues<SubscriberCommandKind>().Length);
        }
    }
}
