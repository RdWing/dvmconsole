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
    /// RED contract for Gate 3.4b shell composition. The restore adapter is
    /// built from the same shared settings store and attached only after the
    /// MainWindow and operator-preferences persistence are composed.
    /// </summary>
    public sealed class MainWindowRestoreShellWiringTests
    {
        [Fact]
        public void AppAndMainWindowSourceForwardOneSharedRestoreAdapterAfterPreferences()
        {
            var appSource = File.ReadAllText(AppSourcePath());
            var windowSource = File.ReadAllText(MainWindowSourcePath());

            Assert.Equal(1, Count(appSource, "new RestoreSettingsPersistence(settingsStore)"));
            Assert.Contains("restorePersistence", appSource);

            var mainWindowCall = appSource.IndexOf(
                "var mainWindow = new MainWindow(",
                StringComparison.Ordinal);
            var preferencesAttach = appSource.IndexOf(
                "mainWindow.AttachPreferencesPersistence(preferencesPersistence);",
                StringComparison.Ordinal);
            var restoreAttach = appSource.IndexOf(
                "mainWindow.AttachRestorePersistence(restorePersistence);",
                StringComparison.Ordinal);

            Assert.True(mainWindowCall >= 0);
            Assert.True(preferencesAttach > mainWindowCall);
            Assert.True(restoreAttach > preferencesAttach);
            Assert.Contains(
                "public void AttachRestorePersistence(RestoreSettingsPersistence restorePersistence)",
                windowSource);
            Assert.Contains(
                "viewModel.AttachRestorePersistence(restorePersistence);",
                windowSource);
        }

        [Fact]
        public void MainWindowExposesExactlyOnePublicRestoreAttachMethodWithoutCtorArityChange()
        {
            MethodInfo method = Assert.Single(
                typeof(MainWindow).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(candidate => candidate.Name == nameof(MainWindow.AttachRestorePersistence)));
            ParameterInfo parameter = Assert.Single(method.GetParameters());

            Assert.Equal(typeof(void), method.ReturnType);
            Assert.Equal(typeof(RestoreSettingsPersistence), parameter.ParameterType);
            Assert.False(parameter.IsOptional);

            ConstructorInfo constructor = Assert.Single(
                typeof(MainWindowViewModel)
                    .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                    .Where(candidate => candidate.GetParameters().Any(
                        candidateParameter => candidateParameter.ParameterType ==
                            typeof(PreferencesSettingsPersistence))));
            ParameterInfo[] parameters = constructor.GetParameters();

            Assert.Equal(typeof(PreferencesSettingsPersistence), parameters[^1].ParameterType);
            Assert.True(parameters[^1].IsOptional);
            Assert.Null(parameters[^1].DefaultValue);
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
