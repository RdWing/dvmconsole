// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.IO;
using System.Reflection;
using DvmConsole.Avalonia.Persistence;
using dvmconsole;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for the first Gate 3.5 layout shell slice. The existing
    /// Core/adapter seam is composed once at App startup, attached after the
    /// window exists, hydrated without a write, and saved from the same loaded
    /// section on close.
    /// </summary>
    public sealed class MainWindowLayoutShellWiringTests
    {
        [Fact]
        public void MainWindowExposesPostConstructionLayoutAttach()
        {
            MethodInfo? method = typeof(MainWindow).GetMethod(
                nameof(MainWindow.AttachLayoutPersistence),
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: new[] { typeof(LayoutSettingsPersistence) },
                modifiers: null);

            Assert.NotNull(method);
            Assert.Equal(typeof(void), method!.ReturnType);
        }

        [Fact]
        public void AppComposesOneSharedLayoutAdapterAndAttachesAfterRestore()
        {
            string appSource = File.ReadAllText(AppSourcePath());
            string windowSource = File.ReadAllText(MainWindowSourcePath());
            const string mainWindowConstruction = "var mainWindow = new MainWindow(";
            const string preferencesAttach = "mainWindow.AttachPreferencesPersistence(preferencesPersistence);";
            const string restoreAttach = "mainWindow.AttachRestorePersistence(restorePersistence);";
            const string layoutAttach = "mainWindow.AttachLayoutPersistence(layoutPersistence);";

            Assert.Equal(1, Count(appSource, "new LayoutSettingsPersistence(settingsStore)"));
            int constructionIndex = appSource.IndexOf(mainWindowConstruction, StringComparison.Ordinal);
            int preferencesIndex = appSource.IndexOf(preferencesAttach, StringComparison.Ordinal);
            int restoreIndex = appSource.IndexOf(restoreAttach, StringComparison.Ordinal);
            int layoutIndex = appSource.IndexOf(layoutAttach, StringComparison.Ordinal);

            Assert.True(constructionIndex >= 0);
            Assert.True(preferencesIndex > constructionIndex);
            Assert.True(restoreIndex > preferencesIndex);
            Assert.True(layoutIndex > restoreIndex);
            Assert.Contains(
                "public void AttachLayoutPersistence(LayoutSettingsPersistence layoutPersistence)",
                windowSource);
            Assert.Contains("this.layoutPersistence = layoutPersistence;", windowSource);
        }

        [Fact]
        public void MainWindowSourcePinsLayoutHydrationAndCloseSaveWithoutHydrationWrite()
        {
            string source = File.ReadAllText(MainWindowSourcePath());
            int loadIndex = source.IndexOf("layoutPersistence.TryLoad", StringComparison.Ordinal);
            int saveIndex = source.IndexOf("layoutPersistence.Save", StringComparison.Ordinal);

            Assert.True(loadIndex >= 0);
            Assert.True(saveIndex > loadIndex);
            Assert.Contains("layoutSection = section", source);
            Assert.Contains("Width = section.WindowWidth", source);
            Assert.Contains("Height = section.WindowHeight", source);
            Assert.Contains("Topmost = ResolveKeepWindowOnTop(", source);
            Assert.Contains("preferenceKeepWindowOnTop", source);
            Assert.Contains("section.KeepWindowOnTop", source);
            Assert.Contains("=> preferenceValue ?? layoutValue", source);
            Assert.Contains("WindowState.Maximized", source);
            Assert.Contains("OnWindowClosed", source);
            Assert.Contains("layoutHydrated", source);
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
