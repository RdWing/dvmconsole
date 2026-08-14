// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using DvmConsole.Avalonia;
using dvmconsole;
using DvmConsole.Avalonia.Services;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Platform.Audio;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// Gate 7.5 contracts for the remaining operator shell controls.
    /// These tests deliberately exercise state transitions at the portable
    /// view-model seam and exact shell-menu/file contracts; keyword-only menu
    /// presence is not sufficient for the behavior tests.
    /// </summary>
    public sealed class Gate75ShellControlsTests
    {
        [Fact]
        public void WidgetVisibility_IsMutableAndRaisesOnlyEffectiveChanges()
        {
            var vm = new MainWindowViewModel();
            var raised = 0;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(MainWindowViewModel.ShowSystemStatus)
                    or nameof(MainWindowViewModel.ShowChannels)
                    or nameof(MainWindowViewModel.ShowAlertTones))
                {
                    raised++;
                }
            };

            vm.SetWidgetVisibility(showSystemStatus: false, showChannels: true, showAlertTones: false);

            Assert.False(vm.ShowSystemStatus);
            Assert.True(vm.ShowChannels);
            Assert.False(vm.ShowAlertTones);
            Assert.Equal(2, raised);

            vm.SetWidgetVisibility(showSystemStatus: false, showChannels: true, showAlertTones: false);
            Assert.Equal(2, raised);
        }

        [Fact]
        public void CallHistoryFilter_ProjectsMatchingRowsWithoutMutatingStore()
        {
            var store = new CallHistoryStore();
            store.AddFrame(
                new ReceivedCallMetadata(
                    "System A", 1001, 31001, 1, VoiceMode.Dmr, 1, "a", false),
                "Alpha");
            store.AddFrame(
                new ReceivedCallMetadata(
                    "System B", 1002, 31002, 1, VoiceMode.Dmr, 2, "b", false),
                "Bravo");

            var vm = new CallHistoryViewModel(store);
            vm.Refresh();
            vm.FilterText = "alpha";

            Assert.Single(vm.VisibleRows);
            Assert.Equal("Alpha", vm.VisibleRows.Single().ChannelName);
            Assert.Equal(2, store.Entries.Count);
        }

        [Fact]
        public void Gate75Menus_MapEveryRequiredWpfControlToAnAvaloniaAction()
        {
            var app = File.ReadAllText(AppSourcePath());
            var xaml = File.ReadAllText(MainWindowXamlPath());
            var window = File.ReadAllText(MainWindowSourcePath());

            Assert.Contains("Text=\"CALL HISTORY\"", xaml);
            Assert.Contains("Content=\"SELECT / CLEAR ALL\"", xaml);
            Assert.Contains("Header=\"Dark Mode\"", xaml);
            Assert.Contains("Header=\"Always on Top\"", xaml);

            Assert.Contains("new NativeMenuItem(\"Shell Controls\")", app);
            var shellMenu = App.CreateShellControlsMenuItem(null);
            Assert.NotNull(shellMenu.Menu);
            Assert.Equal(
                new[]
                {
                    "Select/Clear All Current Zone",
                    "Call History",
                    "Select Widgets to Display",
                    "Select User Background",
                    "Reset Settings",
                    "Reset Tab Layout",
                    "Fit Channel Display to Window Size",
                    "Lock Widgets",
                    "Always on Top",
                    "FNE Connection Manager",
                },
                shellMenu.Menu!.Items
                    .OfType<NativeMenuItem>()
                    .Select(item => item.Header?.ToString())
                    .ToArray());
            Assert.All(
                shellMenu.Menu.Items.OfType<NativeMenuItem>(),
                item => Assert.False(item.IsEnabled));

            foreach (var action in new[]
            {
                "AddShellAction(item.Menu, \"Select/Clear All Current Zone\",",
                "AddShellAction(item.Menu, \"Call History\",",
                "AddShellAction(item.Menu, \"Select Widgets to Display\",",
                "AddShellAction(item.Menu, \"Select User Background\",",
                "AddShellAction(item.Menu, \"Reset Settings\",",
                "AddShellAction(item.Menu, \"Reset Tab Layout\",",
                "AddShellAction(item.Menu, \"Fit Channel Display to Window Size\",",
                "AddShellAction(item.Menu, \"Lock Widgets\",",
                "AddShellAction(item.Menu, \"Always on Top\",",
                "AddShellAction(item.Menu, \"FNE Connection Manager\",",
            })
            {
                Assert.Contains(action, app);
            }

            Assert.Contains("ToggleSelectAllCurrentZone", window);
            Assert.Contains("OpenCallHistory", window);
            Assert.Contains("OpenWidgetSelection", window);
            Assert.Contains("OpenUserBackgroundAsync", window);
            Assert.Contains("ResetLayout", window);
            Assert.Contains("FitLayoutToWindow", window);
            Assert.Contains("SetWidgetLayoutLocked", window);
            Assert.Contains("OpenFneConnectionManager", window);
            Assert.Contains(
                "viewModel.SetWidgetVisibility(\n                    showSystemStatus: true",
                window);
            Assert.Contains("AttachLayoutPersistence", window);
        }

        [Fact]
        public void LayoutTransferCategory_PreservesGate75State()
        {
            var service = new SettingsTransferService(
                Path.Combine(Path.GetTempPath(), "dvmconsole-gate75-settings.json"));
            var layout = service.Categories.Single(category => category.Id == "layout");

            foreach (var property in new[]
            {
                nameof(UserSettingsLayoutSection.KeepWindowOnTop),
                nameof(UserSettingsLayoutSection.LockWidgets),
                nameof(UserSettingsLayoutSection.ShowSystemStatus),
                nameof(UserSettingsLayoutSection.ShowChannels),
                nameof(UserSettingsLayoutSection.ShowAlertTones),
                nameof(UserSettingsLayoutSection.UserBackgroundImage),
            })
            {
                Assert.Contains(property, layout.PropertyNames);
            }
        }

        [Fact]
        public void TopmostResolution_UsesPreferencesAndFallsBackToLayout()
        {
            Assert.True(MainWindow.ResolveKeepWindowOnTop(true, layoutValue: false));
            Assert.False(MainWindow.ResolveKeepWindowOnTop(false, layoutValue: true));
            Assert.True(MainWindow.ResolveKeepWindowOnTop(null, layoutValue: true));
        }

        [Fact]
        public void MainWindowXaml_UsesReadableRootAndScrollableContentSurface()
        {
            var xaml = File.ReadAllText(MainWindowXamlPath());

            Assert.Contains("Background=\"#F3F7F7\"", xaml);
            Assert.Contains("Classes=\"title\"", xaml);
            Assert.Contains("Foreground\" Value=\"#0B1114\"", xaml);
            Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", xaml);
            Assert.Contains("RowDefinitions=\"Auto,Auto,Auto,Auto,Auto\"", xaml);
            Assert.Contains("Open a codeplug from File to configure the console.", xaml);
        }

        [Fact]
        public void MainWindowXaml_DoesNotClipCardsAndProvidesAnUnassignedState()
        {
            var xaml = File.ReadAllText(MainWindowXamlPath());

            Assert.Contains("MinHeight=\"{Binding CardHeight}\"", xaml);
            Assert.DoesNotContain("\n                                        Height=\"{Binding CardHeight}\"", xaml);
            Assert.DoesNotContain("ClipToBounds=\"True\"", xaml);
            Assert.Contains("Text=\"No channel assigned\"", xaml);
            Assert.Contains("IsVisible=\"{Binding ChannelName, Converter={x:Static conv:StringConverters.IsNullOrEmpty}}\"", xaml);
            Assert.Contains("IsVisible=\"{Binding ChannelName, Converter={x:Static conv:StringConverters.IsNotNullOrEmpty}}\"", xaml);
        }

        [Fact]
        public void WidgetLock_GatesWebStreamDragStartAndMovement()
        {
            var source = File.ReadAllText(MainWindowSourcePath());

            Assert.True(
                Count(source, "layoutSection?.LockWidgets == true") >= 2,
                "Web-stream drag start and movement must both honor Lock Widgets.");
            Assert.Contains(
                "layoutSection.LockWidgets = !layoutSection.LockWidgets;\n            SaveLayoutSection();\n            if (layoutSection.LockWidgets)\n            {\n                draggedWebStream = null;",
                source);
        }

        [Fact]
        public void UserBackground_ValidatesBeforePersistingAndClearsInvalidPath()
        {
            var source = File.ReadAllText(MainWindowSourcePath());
            int applyIndex = source.IndexOf("private bool ApplyBackground", StringComparison.Ordinal);
            int decodeIndex = source.IndexOf("new Bitmap(path)", applyIndex, StringComparison.Ordinal);
            int persistIndex = source.IndexOf("layoutSection.UserBackgroundImage = applied ? result.Selected : null", StringComparison.Ordinal);

            Assert.True(applyIndex >= 0);
            Assert.True(decodeIndex > applyIndex);
            Assert.True(persistIndex >= 0);
            int methodEnd = source.IndexOf("        private async Task ReloadCurrentRuntimeAsync", decodeIndex, StringComparison.Ordinal);
            Assert.True(methodEnd > decodeIndex);
            Assert.Contains("catch (Exception", source.Substring(applyIndex, methodEnd - applyIndex));
            Assert.Contains("userBackgroundPath = null", source.Substring(applyIndex, methodEnd - applyIndex));
            Assert.True(
                source.IndexOf("ApplyBackground(result.Selected)", StringComparison.Ordinal) < persistIndex,
                "The picker must validate the image before persisting its path.");
        }

        private static string RepositoryRoot()
            => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

        private static string AppSourcePath()
            => Path.Combine(RepositoryRoot(), "DvmConsole.Avalonia", "App.axaml.cs");


        private static string MainWindowSourcePath()
            => Path.Combine(RepositoryRoot(), "DvmConsole.Avalonia", "MainWindow.axaml.cs");

        private static string MainWindowXamlPath()
            => Path.Combine(RepositoryRoot(), "DvmConsole.Avalonia", "MainWindow.axaml");

        private static int Count(string source, string value)
            => source.Split(value, StringSplitOptions.None).Length - 1;
    }
}
