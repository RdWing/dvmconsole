// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.IO;
using DvmConsole.Avalonia.Persistence;
using DvmConsole.Avalonia.Views;
using dvmconsole;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for the Gate 5.4 Avalonia DTMF shell. The window is a thin
    /// owner-bound editor; persistence updates only DtmfPresets and preserves
    /// alert tones, generated tone presets, and unknown data.
    /// </summary>
    public sealed class DtmfPresetManagerShellTests
    {
        [Fact]
        public void WindowBindsOrderedDtmfEditingAndRequestActions()
        {
            _ = typeof(DtmfPresetManagerWindow);
            string xaml = File.ReadAllText(WindowXamlPath());
            string source = File.ReadAllText(WindowSourcePath());

            Assert.Contains("x:DataType=\"vm:DtmfPresetManagerViewModel\"", xaml);
            Assert.Contains("ItemsSource=\"{Binding Presets}\"", xaml);
            Assert.Contains("ItemsSource=\"{Binding SelectedPreset.Steps}\"", xaml);
            Assert.Contains("SelectedItem=\"{Binding SelectedTarget, Mode=TwoWay}\"", xaml);
            Assert.Contains("AddDigit_Click", xaml);
            Assert.Contains("AddHold_Click", xaml);
            Assert.Contains("DeleteStep_Click", xaml);
            Assert.Contains("MoveUp_Click", xaml);
            Assert.Contains("MoveDown_Click", xaml);
            Assert.Contains("Preview_Click", xaml);
            Assert.Contains("Send_Click", xaml);
            Assert.Contains("Save_Click", xaml);
            Assert.Contains("DataContext = viewModel", source);
            Assert.Contains("viewModel.Commit()", source);
            Assert.Contains("viewModel.Preview()", source);
            Assert.Contains("viewModel.Send()", source);
            Assert.Contains("Closed += OnWindowClosed", source);
        }

        [Fact]
        public void MainWindowAndAppReachDtmfPresetManagerWithoutChangingMainWindowCtor()
        {
            string app = File.ReadAllText(AppSourcePath());
            string window = File.ReadAllText(MainWindowSourcePath());

            Assert.Contains("Manage DTMF Presets", app);
            Assert.Contains("OpenDtmfPresetManager", app);
            Assert.Contains("OpenDtmfPresetManager", window);
            Assert.Contains("new DtmfPresetManagerViewModel", window);
            Assert.Contains("new DtmfPresetTarget", window);
            Assert.Contains("SaveRequested +=", window);
            Assert.Contains("PreviewRequested +=", window);
            Assert.Contains("SendRequested +=", window);
            Assert.Contains("SaveRequested -=", window);
            Assert.Contains("Closed +=", window);
            Assert.Contains("AlertSettingsPersistence", window);
        }

        [Fact]
        public void SaveDtmfPresetSection_MergesOnlyDtmfPresets()
        {
            string path = Path.Combine(
                Path.GetTempPath(),
                "dvmconsole-dtmf-presets-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(
                    path,
                    "{\"KeepMe\":{\"Nested\":123},\"AlertTones\":[{\"Id\":\"alert\",\"FilePath\":\"a.wav\"}],\"TonePresets\":[{\"Id\":\"tone\",\"DisplayName\":\"Tone\"}],\"DtmfPresets\":[{\"Id\":\"old\",\"DisplayName\":\"Old\"}]} ");

                var persistence = new AlertSettingsPersistence(new SettingsSectionStore(path));
                Assert.True(persistence.TryLoad(out UserSettingsAlertSection section));
                section.DtmfPresets = new()
                {
                    new UserSettingsDtmfPresetConfig
                    {
                        Id = "new",
                        DisplayName = "New",
                        Steps = new()
                        {
                            new UserSettingsDtmfPresetStep
                            {
                                Kind = "digit",
                                Digit = "5",
                                DurationSeconds = 0.25,
                            },
                        },
                    },
                };
                persistence.Save(section);

                JObject saved = JObject.Parse(File.ReadAllText(path));
                Assert.Equal("123", saved["KeepMe"]!["Nested"]!.ToString());
                Assert.Equal("alert", saved["AlertTones"]![0]!["Id"]!.ToString());
                Assert.Equal("tone", saved["TonePresets"]![0]!["Id"]!.ToString());
                Assert.Equal("new", saved["DtmfPresets"]![0]!["Id"]!.ToString());
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        private static string WindowXamlPath()
            => Path.Combine(RepositoryRoot(), "DvmConsole.Avalonia", "Views", "DtmfPresetManagerWindow.axaml");

        private static string WindowSourcePath()
            => Path.Combine(RepositoryRoot(), "DvmConsole.Avalonia", "Views", "DtmfPresetManagerWindow.axaml.cs");

        private static string AppSourcePath()
            => Path.Combine(RepositoryRoot(), "DvmConsole.Avalonia", "App.axaml.cs");

        private static string MainWindowSourcePath()
            => Path.Combine(RepositoryRoot(), "DvmConsole.Avalonia", "MainWindow.axaml.cs");

        private static string RepositoryRoot()
            => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    }
}
