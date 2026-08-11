// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.IO;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED source contract for the compiled NativeMenu preference bindings.
    /// Runtime check-state behavior remains a macOS host concern; the XAML
    /// build and these exact binding pins prove the shell surface exists.
    /// </summary>
    public sealed class PreferencesMenuWiringTests
    {
        [Fact]
        public void MainWindowXaml_DeclaresSettingsAndViewPreferenceMenus()
        {
            var source = File.ReadAllText(XamlPath());

            Assert.Contains("<NativeMenuItem Header=\"Settings\">", source);
            Assert.Contains("<NativeMenuItem Header=\"View\">", source);
            Assert.Contains("<NativeMenuItem Header=\"Quit\" />", source);
            Assert.Contains("Text=\"{Binding PreferencesSaveFeedback}\"", source);
            Assert.Contains(
                "StringConverters.IsNotNullOrEmpty",
                source);
        }

        [Fact]
        public void MainWindowXaml_BindsAllPreferencesAsTwoWayCheckboxMenuItems()
        {
            var source = File.ReadAllText(XamlPath());
            var bindings = new[]
            {
                "Talk Permit Tone",
                "Mute RX Audio While Transmitting",
                "Retain Patch State On Startup",
                "Restore Selected Channels On Startup",
                "Dark Mode",
                "Always on Top",
            };
            var properties = new[]
            {
                "TalkPermitTone",
                "MuteRxAudioWhileTransmitting",
                "RetainPatchStateOnStartup",
                "RestoreSelectedChannelsOnStartup",
                "DarkMode",
                "KeepWindowOnTop",
            };

            for (var i = 0; i < bindings.Length; i++)
            {
                Assert.Contains($"Header=\"{bindings[i]}\"", source);
                Assert.Contains("ToggleType=\"CheckBox\"", source);
                Assert.Contains(
                    $"IsChecked=\"{{Binding Preferences.{properties[i]}, Mode=TwoWay}}\"",
                    source);
            }
        }

        [Fact]
        public void MainWindowXaml_KeepsQuitAfterTheAppMenuEntries()
        {
            var source = File.ReadAllText(XamlPath());
            var appMenu = source.IndexOf("<NativeMenuItem Header=\"App\">", StringComparison.Ordinal);
            var quit = source.IndexOf("<NativeMenuItem Header=\"Quit\" />", StringComparison.Ordinal);

            Assert.True(appMenu >= 0);
            Assert.True(quit > appMenu);
        }

        private static string XamlPath()
            => Path.Combine(
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../")),
                "DvmConsole.Avalonia",
                "MainWindow.axaml");
    }
}
