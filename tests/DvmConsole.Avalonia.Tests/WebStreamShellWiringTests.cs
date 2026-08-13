#nullable enable
using System;
using System.IO;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    public sealed class WebStreamShellWiringTests
    {
        [Fact]
        public void AppAndWindowComposeSharedWebStreamSourceAndPersistence()
        {
            var app = File.ReadAllText(AppSourcePath());
            var window = File.ReadAllText(MainWindowSourcePath());

            Assert.Contains("new WebStreamSourceFactory()", app);
            Assert.Contains("AttachWebStreamSourceFactory", app);
            Assert.Contains("AttachWebStreamPersistence", app);
            Assert.Contains("public void AttachWebStreamPersistence", window);
            Assert.Contains("viewModel.AttachWebStreams", window);
        }

        [Fact]
        public void MainWindowXamlBindsStreamItemsAndStateActions()
        {
            var xaml = File.ReadAllText(MainWindowXamlPath());

            Assert.Contains("ItemsSource=\"{Binding WebStreams.Items}\"", xaml);
            Assert.Contains("x:DataType=\"vm:WebStreamShellItemViewModel\"", xaml);
            Assert.Contains("WebStreamToggle_Click", xaml);
            Assert.Contains("WebStreamCard_PointerPressed", xaml);
            Assert.Contains("WebStreamCard_PointerMoved", xaml);
            Assert.Contains("WebStreamCard_PointerReleased", xaml);
            Assert.Contains("StatusText", xaml);
            Assert.Contains("Volume", xaml);
        }

        private static string AppSourcePath()
            => Path.Combine(RepositoryRoot(), "DvmConsole.Avalonia", "App.axaml.cs");

        private static string MainWindowSourcePath()
            => Path.Combine(RepositoryRoot(), "DvmConsole.Avalonia", "MainWindow.axaml.cs");

        private static string MainWindowXamlPath()
            => Path.Combine(RepositoryRoot(), "DvmConsole.Avalonia", "MainWindow.axaml");

        private static string RepositoryRoot()
            => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    }
}
