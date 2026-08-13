// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using DvmConsole.Avalonia.Views;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for the Gate 7.2 Avalonia shell boundary. The dialog owns
    /// picker/confirmation interactions; App owns the shared settings path and
    /// candidate-window rebuild so one confirmed import causes one runtime
    /// replacement.
    /// </summary>
    public sealed class SettingsTransferShellWiringTests
    {
        [Fact]
        public void AppCreatesSettingsTransferMenuItem()
        {
            NativeMenuItem item = DvmConsole.Avalonia.App.CreateSettingsTransferMenuItem(null);

            Assert.Equal("Import / Export Settings", item.Header);
        }

        [Fact]
        public void SettingsTransferWindowRequiresViewModelPickersConfirmationAndReload()
        {
            ConstructorInfo constructor = Assert.Single(
                typeof(SettingsTransferWindow).GetConstructors(
                    BindingFlags.Public | BindingFlags.Instance));
            Assert.Equal(
                new[]
                {
                    typeof(DvmConsole.Avalonia.ViewModels.SettingsTransferViewModel),
                    typeof(DvmConsole.Platform.Dialogs.IFileDialogService),
                    typeof(DvmConsole.Avalonia.Dialogs.IConfirmationService),
                    typeof(Func<System.Threading.Tasks.Task>),
                },
                constructor.GetParameters().Select(parameter => parameter.ParameterType));
        }

        [Fact]
        public void MainWindowExposesSettingsTransferEntryPoint()
        {
            MethodInfo? method = typeof(DvmConsole.Avalonia.MainWindow).GetMethod(
                "OpenSettingsTransfer",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(method);
            Assert.Empty(method!.GetParameters());
            Assert.Equal(typeof(void), method.ReturnType);
        }

        [Fact]
        public void AppAndMainWindowSourcePinSharedServiceAndSingleRuntimeReload()
        {
            string appSource = File.ReadAllText(SourcePath("App.axaml.cs"));
            string windowSource = File.ReadAllText(SourcePath("MainWindow.axaml.cs"));
            string dialogSource = File.ReadAllText(
                SourcePath(Path.Combine("Views", "SettingsTransferWindow.axaml.cs")));

            Assert.Contains(
                "new SettingsTransferService(fileSystemPaths.SettingsFilePath)",
                appSource);
            Assert.Contains("CreateSettingsTransferMenuItem", appSource);
            Assert.Contains("mainWindow.AttachSettingsTransfer", appSource);
            Assert.Contains("SettingsTransferWindow", windowSource);
            Assert.Contains("FileDialogService", windowSource);
            Assert.Contains("ImportAsync", dialogSource);
            Assert.Contains("ResetAsync", dialogSource);
            Assert.Contains("new SettingsTransferWindow(", windowSource);
            Assert.Contains("TarConfirmationService", windowSource);
            Assert.Contains("reloadRuntimeAsync", windowSource);
        }

        private static int Count(string source, string value)
            => source.Split(value, StringSplitOptions.None).Length - 1;

        private static string SourcePath(string fileName)
            => Path.Combine(RepositoryRoot(), "DvmConsole.Avalonia", fileName);

        private static string RepositoryRoot()
            => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    }
}
