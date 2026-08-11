// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using DvmConsole.Avalonia.Persistence;
using DvmConsole.Avalonia.Services;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Platform.Audio;
using dvmconsole;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for the operator-preferences shell plumbing. The new
    /// adapter is attached after construction so the existing MainWindow
    /// constructor and TAR/viewer dependency order remain source-compatible.
    /// </summary>
    public sealed class MainWindowPreferencesShellWiringTests
    {
        [Fact]
        public void MainWindowConstructor_PreservesExistingViewerDependencies()
        {
            ConstructorInfo constructor = Assert.Single(
                typeof(MainWindow)
                    .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                    .Where(candidate => candidate.GetParameters().Any(
                        parameter => parameter.ParameterType == typeof(AudioSettingsPersistence))
                        && candidate.GetParameters().Any(
                            parameter => parameter.ParameterType == typeof(AliasResolver))));
            ParameterInfo[] parameters = constructor.GetParameters();

            Assert.Equal(typeof(TarRecorder), parameters[^3].ParameterType);
            Assert.Equal(typeof(IAudioWaveFilePlayer), parameters[^2].ParameterType);
            Assert.Equal(typeof(TarViewerColumnSettingsPersistence), parameters[^1].ParameterType);
            Assert.True(parameters[^3].IsOptional);
            Assert.Null(parameters[^3].DefaultValue);
            Assert.True(parameters[^2].IsOptional);
            Assert.Null(parameters[^2].DefaultValue);
            Assert.True(parameters[^1].IsOptional);
            Assert.Null(parameters[^1].DefaultValue);
        }

        [Fact]
        public void AppAndMainWindowSourceForwardOneSharedPreferencesAdapter()
        {
            var appSource = File.ReadAllText(AppSourcePath());
            var windowSource = File.ReadAllText(MainWindowSourcePath());

            Assert.Equal(1, Count(appSource, "new PreferencesSettingsPersistence(settingsStore)"));
            Assert.Contains("preferencesPersistence", appSource);
            var mainWindowCall = appSource.Substring(
                appSource.IndexOf("var mainWindow = new MainWindow(", StringComparison.Ordinal));
            var attachCall = appSource.IndexOf(
                "mainWindow.AttachPreferencesPersistence(preferencesPersistence);",
                StringComparison.Ordinal);
            Assert.True(mainWindowCall.IndexOf("tarViewerColumnPersistence", StringComparison.Ordinal) >= 0);
            Assert.True(attachCall > appSource.IndexOf("var mainWindow = new MainWindow(", StringComparison.Ordinal));
            Assert.Contains(
                "public void AttachPreferencesPersistence(PreferencesSettingsPersistence preferencesPersistence)",
                windowSource);
            Assert.Contains("viewModel.AttachPreferencesPersistence(preferencesPersistence);", windowSource);
        }

        [Fact]
        public void MainWindowViewModelConstructor_AppendsPreferencesPersistenceAfterPtt()
        {
            ConstructorInfo constructor = Assert.Single(
                typeof(MainWindowViewModel)
                    .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                    .Where(candidate => candidate.GetParameters().Any(
                        parameter => parameter.ParameterType == typeof(PttSettingsPersistence))));
            ParameterInfo[] parameters = constructor.GetParameters();

            Assert.Equal(typeof(PttSettingsPersistence), parameters[^2].ParameterType);
            Assert.Equal(typeof(PreferencesSettingsPersistence), parameters[^1].ParameterType);
            Assert.True(parameters[^2].IsOptional);
            Assert.Null(parameters[^2].DefaultValue);
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
