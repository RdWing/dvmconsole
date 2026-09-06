// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using DvmConsole.Desktop;
using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.RenderTests;

public sealed class ConfigurationStudioOnboardingTests
{
    [AvaloniaFact]
    public async Task EmptyConsoleCanCreateSaveLoadAndReopenCompleteConfiguration()
    {
        const string mode = "p25";
        using DemoSessionState state = DemoSessionState.Create();
        var main = new MainWindow(null, new UserSettingsStore(state.UserSettingsPath),
            new OperatorViewStore(state.OperatorViewPath), demoMode: true);
        try
        {
            main.Show();
            var empty = Assert.IsType<MainWindowViewModel>(main.DataContext);
            Assert.Null(empty.CurrentCodeplugPath);
            Assert.Empty(empty.Systems);
            await main.OpenConfigurationStudioAsync(ConfigurationStudioSection.Overview, createNew: true);
            var studio = Assert.IsType<ConfigurationStudioWindow>(main.OpenConfigurationStudioWindow);
            studio.Width = 1488;
            studio.Height = 900;
            var vm = studio.StudioViewModel;
            Assert.Empty(vm.Systems);
            Assert.Empty(vm.Zones);
            await Capture(studio, "01-empty");

            studio.SelectSection(ConfigurationStudioSection.Systems);
            await Click(studio, "Add system");
            await Edit(studio, "System name", "Training FNE");
            await Edit(studio, "System address", "127.0.0.1");
            await Edit(studio, "System port", "62031");
            await Edit(studio, "System peer ID", "7001");
            await Edit(studio, "System radio ID", "7001");
            await Edit(studio, "System password", "synthetic-training-password");
            await Capture(studio, "02-system");
            await Click(studio, "Add channel to selected FNE system");
            await Edit(studio, "Zone name", "Dispatch");
            await Edit(studio, "Channel name", "Secure Dispatch");
            await Edit(studio, "Channel destination ID", "1001");
            Find<ComboBox>(studio, "Channel mode").SelectedValue = mode;
            await App.WaitForRenderAsync();
            Find<ComboBox>(studio, "Channel encryption algorithm").SelectedItem =
                Assert.Single(vm.AvailableChannelAlgorithms, option => option.ConfigurationValue == "aes");
            await Edit(studio, "Channel encryption key ID", "1");
            await Capture(studio, "03-channel");

            studio.SelectSection(ConfigurationStudioSection.EncryptionKeys);
            await Click(studio, "Add encryption key");
            Find<ComboBox>(studio, "Encryption key protocol").SelectedValue = mode;
            await App.WaitForRenderAsync();
            Find<ComboBox>(studio, "Encryption key algorithm").SelectedItem =
                Assert.Single(vm.AvailableKeyAlgorithms, option => option.ConfigurationValue == "aes");
            await App.WaitForRenderAsync();
            await Edit(studio, "Encryption key material",
                "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
            Assert.Equal("Training FNE", Assert.Single(vm.KeyEntries).System);
            await Capture(studio, "04-key");

            studio.SelectSection(ConfigurationStudioSection.Files);
            await Click(studio, "Add RID alias");
            await Edit(studio, "RID alias radio ID", "7001");
            await Edit(studio, "RID alias name", "Training Dispatch");
            Assert.Equal("Training Dispatch", Assert.Single(vm.Aliases).Name);
            await Capture(studio, "05-alias");

            studio.SelectSection(ConfigurationStudioSection.Streams);
            await Click(studio, "Add web stream");
            await Edit(studio, "Web stream name", "Training Stream");
            await Edit(studio, "Web stream URL", "https://example.invalid/training.mp3");
            ListBox streams = Find<ListBox>(studio, "Web streams");
            Assert.Contains(streams.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "Training Stream");
            Assert.Contains(streams.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "https://example.invalid/training.mp3");
            await Capture(studio, "06-stream");

            studio.SelectSection(ConfigurationStudioSection.Groups);
            await Click(studio, "Add group");
            await Edit(studio, "Group name", "Training Patch");
            Assert.Contains(Find<ListBox>(studio, "Group definitions").GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "Training Patch");
            await Capture(studio, "07-group");
            vm.CommitPendingEdits();
            Assert.DoesNotContain(vm.ValidationIssues, issue => issue.IsError);
            var messages = new List<string>();
            studio.DialogConfirmationOverride = (_, _, _) => Task.FromResult(true);
            studio.MessageOverride = (title, message) => { messages.Add(title + ": " + message); return Task.CompletedTask; };
            Assert.True(await studio.ReviewAndSaveForCaptureAsync(), string.Join("\n", messages));
            var loaded = Assert.IsType<MainWindowViewModel>(main.DataContext);
            Assert.NotNull(loaded.ConfigurationReference);
            Assert.Single(loaded.Systems);
            Assert.False(studio.IsVisible);

            await main.OpenConfigurationStudioAsync(ConfigurationStudioSection.Overview, createNew: false);
            studio = Assert.IsType<ConfigurationStudioWindow>(main.OpenConfigurationStudioWindow);
            vm = studio.StudioViewModel;
            Assert.Equal("Training FNE", Assert.Single(vm.Systems).Name);
            ZoneConfiguration zone = Assert.Single(vm.Zones);
            Assert.Equal("Dispatch", zone.Name);
            Assert.Equal("Secure Dispatch", Assert.Single(zone.Channels).Name);
            Assert.Equal("aes", zone.Channels[0].Algo);
            Assert.Equal(mode, zone.Channels[0].Mode);
            Assert.Equal(mode, Assert.Single(vm.KeyEntries).Protocol);
            Assert.Equal("Training Stream", Assert.Single(zone.WebStreams).Name);
            Assert.Single(vm.KeyEntries);
            Assert.Equal("Training Dispatch", Assert.Single(vm.Aliases).Name);
            Assert.Equal("Training Patch", Assert.Single(vm.Groups).Name);
            Assert.DoesNotContain(vm.ValidationIssues, issue => issue.IsError);
            await Capture(studio, "08-reopened");
            studio.CloseForSessionReplacement();
            await loaded.FlushUserSettingsAsync();
            main.Close();
            main = new MainWindow(null, new UserSettingsStore(state.UserSettingsPath),
                new OperatorViewStore(state.OperatorViewPath), demoMode: true);
            main.Show();
            for (int attempt = 0; attempt < 100 &&
                 Assert.IsType<MainWindowViewModel>(main.DataContext).ConfigurationReference is null; attempt++)
                await App.WaitForRenderAsync();
            Assert.Equal(loaded.ConfigurationReference,
                Assert.IsType<MainWindowViewModel>(main.DataContext).ConfigurationReference);
        }
        finally
        {
            main.OpenConfigurationStudioWindow?.CloseForSessionReplacement();
            main.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData("p25")]
    [InlineData("dmr")]
    [InlineData("nxdn")]
    public async Task ProtocolAndKeyEditorsPersistMatchingEncryption(string mode)
    {
        using DemoSessionState state = DemoSessionState.Create();
        string path = Path.Combine(Path.GetDirectoryName(state.UserSettingsPath)!, "protocol.yml");
        File.WriteAllText(path, """
            systems:
              - name: Training FNE
                identity: Training
                address: 127.0.0.1
                port: 62031
                peerId: 7001
                rid: '7001'
            zones:
              - name: Dispatch
                channels:
                  - name: Secure Dispatch
                    system: Training FNE
                    tgid: '1001'
                    mode: p25
            """);
        var main = new MainWindow(path, new UserSettingsStore(state.UserSettingsPath),
            new OperatorViewStore(state.OperatorViewPath), demoMode: true);
        try
        {
            main.Show();
            await main.OpenConfigurationStudioAsync(ConfigurationStudioSection.Systems, createNew: false);
            var studio = Assert.IsType<ConfigurationStudioWindow>(main.OpenConfigurationStudioWindow);
            studio.Width = 1488;
            studio.Height = 900;
            await App.WaitForRenderAsync();
            var vm = studio.StudioViewModel;
            vm.SelectedSystem = Assert.Single(vm.Systems);
            vm.SelectedZone = Assert.Single(vm.Zones);
            vm.SelectedChannel = Assert.Single(vm.SelectedZone.Channels);
            studio.SelectSection(ConfigurationStudioSection.Zones);
            await App.WaitForRenderAsync();
            Find<ComboBox>(studio, "Channel mode").SelectedValue = mode;
            await App.WaitForRenderAsync();
            Find<ComboBox>(studio, "Channel encryption algorithm").SelectedItem =
                Assert.Single(vm.AvailableChannelAlgorithms, option => option.ConfigurationValue == "aes");
            await Edit(studio, "Channel encryption key ID", "1");
            studio.SelectSection(ConfigurationStudioSection.EncryptionKeys);
            await Click(studio, "Add encryption key");
            Find<ComboBox>(studio, "Encryption key protocol").SelectedValue = mode;
            await App.WaitForRenderAsync();
            Find<ComboBox>(studio, "Encryption key algorithm").SelectedItem =
                Assert.Single(vm.AvailableKeyAlgorithms, option => option.ConfigurationValue == "aes");
            await Edit(studio, "Encryption key material",
                "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
            vm.CommitPendingEdits();
            Assert.DoesNotContain(vm.ValidationIssues, issue => issue.IsError);
            studio.DialogConfirmationOverride = (_, _, _) => Task.FromResult(true);
            studio.MessageOverride = (_, _) => Task.CompletedTask;
            Assert.True(await studio.ReviewAndSaveForCaptureAsync());
            await main.OpenConfigurationStudioAsync(ConfigurationStudioSection.EncryptionKeys, createNew: false);
            vm = Assert.IsType<ConfigurationStudioWindow>(main.OpenConfigurationStudioWindow).StudioViewModel;
            var channel = Assert.Single(Assert.Single(vm.Zones).Channels);
            Assert.Equal(mode, channel.Mode);
            Assert.Equal("aes", channel.Algo);
            Assert.Equal(mode, Assert.Single(vm.KeyEntries).Protocol);
            Assert.Equal("Training FNE", Assert.Single(vm.KeyEntries).System);
            Assert.DoesNotContain(vm.ValidationIssues, issue => issue.IsError);
        }
        finally
        {
            main.OpenConfigurationStudioWindow?.CloseForSessionReplacement();
            main.Close();
        }
    }

    [AvaloniaFact]
    public async Task FirstWebStreamCanBeAddedWithoutAnFneOrPreexistingZone()
    {
        using DemoSessionState state = DemoSessionState.Create();
        var main = new MainWindow(null, new UserSettingsStore(state.UserSettingsPath),
            new OperatorViewStore(state.OperatorViewPath), demoMode: true);
        try
        {
            main.Show();
            await main.OpenConfigurationStudioAsync(ConfigurationStudioSection.Streams, createNew: true);
            var studio = Assert.IsType<ConfigurationStudioWindow>(main.OpenConfigurationStudioWindow);
            await Click(studio, "Add web stream");
            Assert.Single(studio.StudioViewModel.Streams);
            Assert.Single(studio.StudioViewModel.Zones);
            Assert.Empty(studio.StudioViewModel.Systems);
        }
        finally
        {
            main.OpenConfigurationStudioWindow?.CloseForSessionReplacement();
            main.Close();
        }
    }

    private static T Find<T>(Control root, string name) where T : Control
        => Assert.Single(root.GetVisualDescendants().OfType<T>(),
            control => AutomationProperties.GetName(control) == name);

    private static async Task Click(ConfigurationStudioWindow studio, string name)
    {
        await App.WaitForRenderAsync();
        Button button = Find<Button>(studio, name);
        Assert.True(button.IsEffectivelyVisible);
        Assert.True(button.IsEnabled);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await App.WaitForRenderAsync();
    }

    private static async Task Edit(ConfigurationStudioWindow studio, string name, string value)
    {
        await App.WaitForRenderAsync();
        TextBox editor = Find<TextBox>(studio, name);
        Assert.True(editor.IsEffectivelyVisible);
        Assert.True(editor.IsEnabled);
        editor.Focus();
        editor.Text = value;
        editor.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        await App.WaitForRenderAsync();
    }

    private static async Task Capture(ConfigurationStudioWindow studio, string name)
    {
        await App.WaitForRenderAsync();
        string? directory = Environment.GetEnvironmentVariable("DVMCONSOLE_NEW_USER_AUDIT_DIR");
        if (string.IsNullOrWhiteSpace(directory))
            return;
        Directory.CreateDirectory(directory);
        App.SaveVisual(studio, Path.Combine(directory, name + ".png"));
    }
}
