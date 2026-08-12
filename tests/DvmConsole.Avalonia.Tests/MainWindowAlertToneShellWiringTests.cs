// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using DvmConsole.Avalonia.Persistence;
using DvmConsole.Avalonia.ViewModels;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for Gate 5.1 shell composition. The manager is attached
    /// after the shared settings store and uses injected dialog services.
    /// </summary>
    public sealed class MainWindowAlertToneShellWiringTests
    {
        [Fact]
        public void AppAndMainWindowComposeOneSharedAlertPersistenceAfterWindowCreation()
        {
            string appSource = File.ReadAllText(AppSourcePath());
            string windowSource = File.ReadAllText(MainWindowSourcePath());

            Assert.Equal(1, Count(appSource, "new AlertSettingsPersistence(settingsStore)"));
            Assert.Contains("alertPersistence", appSource);

            int mainWindowCall = appSource.IndexOf(
                "var mainWindow = new MainWindow(",
                StringComparison.Ordinal);
            int alertAttach = appSource.IndexOf(
                "mainWindow.AttachAlertSettingsPersistence(alertPersistence);",
                StringComparison.Ordinal);

            Assert.True(mainWindowCall >= 0);
            Assert.True(alertAttach > mainWindowCall);
            Assert.Contains(
                "public void AttachAlertSettingsPersistence(AlertSettingsPersistence persistence)",
                windowSource);
            Assert.Contains("OpenAlertToneManager", windowSource);
        }

        [Fact]
        public void MainWindowManagerUsesInjectedDialogAndConfirmationServicesAndUnsubscribesOnClose()
        {
            string source = File.ReadAllText(MainWindowSourcePath());
            int start = source.IndexOf("OpenAlertToneManager", StringComparison.Ordinal);

            Assert.True(start >= 0);
            string body = source[start..];
            Assert.Contains("FileDialogService", body);
            Assert.Contains("AlertToneManagerWindow", body);
            Assert.Contains("AlertSettingsPersistence", body);
            Assert.Contains("SaveRequested +=", body);
            Assert.Contains("SaveRequested -=", body);
            Assert.Contains("Closed +=", body);
            Assert.Contains("TarConfirmationService", body);
        }

        [Fact]
        public void AppAddsOneTonesManageMenuItemWithNativeClickHandler()
        {
            string source = File.ReadAllText(AppSourcePath());

            Assert.Equal(1, Count(source, "internal static NativeMenuItem CreateAlertToneManagerMenuItem"));
            Assert.Contains("Manage Custom Alert Tones", source);
            Assert.Contains("Tones", source);
            Assert.Contains("OpenAlertToneManager", source);
        }

        [Fact]
        public void MainWindowAlertAttachHasOneRequiredParameterAndDoesNotChangeCtorShape()
        {
            MethodInfo method = Assert.Single(
                typeof(MainWindow).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(candidate => candidate.Name == nameof(MainWindow.AttachAlertSettingsPersistence)));
            ParameterInfo parameter = Assert.Single(method.GetParameters());

            Assert.Equal(typeof(void), method.ReturnType);
            Assert.Equal(typeof(AlertSettingsPersistence), parameter.ParameterType);
            Assert.False(parameter.IsOptional);
        }

        private static int Count(string source, string value)
            => source.Split(value, StringSplitOptions.None).Length - 1;

        private static string AppSourcePath()
            => Path.Combine(RepositoryRoot(), "DvmConsole.Avalonia", "App.axaml.cs");

        private static string MainWindowSourcePath()
            => Path.Combine(RepositoryRoot(), "DvmConsole.Avalonia", "MainWindow.axaml.cs");

        private static string RepositoryRoot()
            => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    }
}
