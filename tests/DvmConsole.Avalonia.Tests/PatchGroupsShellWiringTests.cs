// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using DvmConsole.Avalonia.Persistence;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Avalonia.Views;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for Gate 4.2b: the owner-bound Avalonia Groups editor
    /// shell. The window owns presentation and close cleanup; MainWindow owns
    /// construction, shared persistence, and request handling. PatchManager,
    /// native services, and runtime patch transmit remain later gates.
    /// </summary>
    public sealed class PatchGroupsShellWiringTests
    {
        [Fact]
        public void WindowConstructor_ReceivesOnlyThePublishedHeadlessViewModel()
        {
            ConstructorInfo constructor = Assert.Single(
                typeof(PatchGroupsWindow)
                    .GetConstructors(BindingFlags.Public | BindingFlags.Instance));
            ParameterInfo[] parameters = constructor.GetParameters();

            Assert.Single(parameters);
            Assert.Equal(typeof(PatchGroupsViewModel), parameters[0].ParameterType);
            Assert.False(parameters[0].IsOptional);
        }

        [Fact]
        public void WindowSource_BindsThePublishedStateAndClosesThroughTheVm()
        {
            string source = File.ReadAllText(WindowSourcePath());
            string xaml = File.ReadAllText(WindowXamlPath());

            Assert.Contains("DataContext = viewModel", source);
            Assert.Contains("Closed += OnWindowClosed", source);
            Assert.Contains("vm.Commit()", source);
            Assert.Contains("viewModel.Close();", source);
            Assert.Contains("vm.EnterEdit", source);
            Assert.Contains("vm.ExitEdit", source);
            Assert.Contains("vm.RequestPtt", source);

            Assert.Contains("x:Class=\"DvmConsole.Avalonia.Views.PatchGroupsWindow\"", xaml);
            Assert.Contains("x:ClassModifier=\"internal\"", xaml);
            Assert.Contains("x:DataType=\"vm:PatchGroupsViewModel\"", xaml);
            Assert.Contains("ItemsSource=\"{Binding Groups}\"", xaml);
            Assert.Contains("{Binding Name}", xaml);
            Assert.Contains("{Binding GroupType}", xaml);
            Assert.Contains("ItemsSource=\"{Binding SelectedGroup.Members}\"", xaml);
            Assert.Contains("Content=\"Save\"", xaml);
            Assert.Contains("Content=\"Close\"", xaml);
        }

        [Fact]
        public void MainWindowExposesOnePatchGroupsEntryPointAndPreservesTrailingDependencies()
        {
            MethodInfo method = Assert.Single(
                typeof(MainWindow).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                    .Where(candidate => candidate.Name == "OpenPatchGroups"));
            Assert.Empty(method.GetParameters());
            Assert.Equal(typeof(void), method.ReturnType);

            MethodInfo attach = typeof(MainWindow).GetMethod(
                "AttachGroupsPersistence",
                BindingFlags.Instance | BindingFlags.Public)!;
            Assert.NotNull(attach);
            Assert.Single(attach.GetParameters());
            Assert.Equal(typeof(GroupSettingsPersistence), attach.GetParameters()[0].ParameterType);

            ConstructorInfo constructor = Assert.Single(
                typeof(MainWindow).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                    .Where(candidate => candidate.GetParameters()
                        .Any(parameter => parameter.ParameterType == typeof(TarViewerColumnSettingsPersistence))));
            ParameterInfo[] parameters = constructor.GetParameters();
            Assert.Equal(typeof(TarViewerColumnSettingsPersistence), parameters[^1].ParameterType);
            Assert.True(parameters[^1].IsOptional);
            Assert.Null(parameters[^1].DefaultValue);
        }

        [Fact]
        public void MainWindowSource_ConstructsTheVmFromCodeplugAndOwnsRequests()
        {
            string source = File.ReadAllText(MainWindowSourcePath());

            Assert.Contains("PatchGroupsViewModel", source);
            Assert.Contains("new PatchGroupsViewModel", source);
            Assert.Contains("new PatchGroupsWindow", source);
            Assert.Contains("Show(this)", source);
            Assert.Contains("SaveRequested +=", source);
            Assert.Contains("PttRequested +=", source);
            Assert.Contains("OnPatchPttRequested", source);
            Assert.Contains("patchPttRuntimeCoordinator", source);
            Assert.Contains("GroupSettingsPersistence", source);
            Assert.Contains("groupsPersistence.Save", source);
            Assert.Contains("membershipContextKey: groupsMembershipContextKey", source);
            Assert.Contains("patchGroupsWindow?.Close();", source);
            Assert.Contains("patchPtt.DisposeAsync()", source);
            Assert.DoesNotContain("PatchManager", source.Substring(
                source.IndexOf("OpenPatchGroups", StringComparison.Ordinal)));
        }

        [Fact]
        public void AppAndMainWindowForwardOneSharedGroupsPersistenceAdapter()
        {
            string appSource = File.ReadAllText(AppSourcePath());
            string windowSource = File.ReadAllText(MainWindowSourcePath());

            Assert.Equal(1, Count(appSource, "new GroupSettingsPersistence(settingsStore)"));
            int mainWindowCall = appSource.IndexOf(
                "var mainWindow = new MainWindow(", StringComparison.Ordinal);
            int groupsAttachment = appSource.IndexOf(
                "mainWindow.AttachGroupsPersistence(groupsPersistence);",
                mainWindowCall,
                StringComparison.Ordinal);

            Assert.True(mainWindowCall >= 0);
            Assert.True(groupsAttachment > mainWindowCall);
            Assert.Contains(
                "AttachGroupsPersistence(GroupSettingsPersistence persistence)",
                windowSource);
        }

        [Fact]
        public void MainWindowXaml_ProvidesTheSettingsGroupsAction()
        {
            string xaml = File.ReadAllText(MainWindowXamlPath());
            string appSource = File.ReadAllText(AppSourcePath());

            Assert.Contains("<NativeMenuItem Header=\"Settings\">", xaml);
            Assert.Contains("CreatePatchGroupsMenuItem", appSource);
            Assert.Contains("settingsMenu.Items.Insert(0, groupsItem)", appSource);
        }

        [Fact]
        public void GroupsShellSourceKeepsRuntimePatchOwnershipDeferred()
        {
            string windowSource = File.ReadAllText(WindowSourcePath());
            string vmSource = File.ReadAllText(ViewModelSourcePath());

            Assert.DoesNotContain("PatchManager", windowSource);
            Assert.DoesNotContain("Fnecore", windowSource);
            Assert.DoesNotContain("Native", windowSource);
            Assert.DoesNotContain("Dispatcher", windowSource);
            Assert.DoesNotContain("persistence.Save", vmSource);
        }

        private static int Count(string source, string value)
            => source.Split(value, StringSplitOptions.None).Length - 1;

        private static string RepositoryRoot()
            => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

        private static string AppSourcePath()
            => Path.Combine(RepositoryRoot(), "DvmConsole.Avalonia", "App.axaml.cs");

        private static string MainWindowSourcePath()
            => Path.Combine(RepositoryRoot(), "DvmConsole.Avalonia", "MainWindow.axaml.cs");

        private static string MainWindowXamlPath()
            => Path.Combine(RepositoryRoot(), "DvmConsole.Avalonia", "MainWindow.axaml");

        private static string WindowSourcePath()
            => Path.Combine(
                RepositoryRoot(),
                "DvmConsole.Avalonia",
                "Views",
                "PatchGroupsWindow.axaml.cs");

        private static string WindowXamlPath()
            => Path.Combine(
                RepositoryRoot(),
                "DvmConsole.Avalonia",
                "Views",
                "PatchGroupsWindow.axaml");

        private static string ViewModelSourcePath()
            => Path.Combine(
                RepositoryRoot(),
                "DvmConsole.Avalonia",
                "ViewModels",
                "PatchGroupsViewModel.cs");
    }
}
