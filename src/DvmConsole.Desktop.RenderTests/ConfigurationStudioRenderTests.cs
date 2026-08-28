using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using DvmConsole.Desktop;
using System.Text.Json;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(DvmConsole.Desktop.RenderTests.HeadlessTestApp))]

namespace DvmConsole.Desktop.RenderTests;

public static class HeadlessTestApp
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia();
}

public sealed class ConfigurationStudioRenderTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [AvaloniaFact]
    public async Task StudioWindowsRenderWithSanitizedDemoConfiguration()
    {
        string? captureDirectory = Environment.GetEnvironmentVariable("DVMCONSOLE_DOC_CAPTURE_DIR");
        string demoCodeplug = Path.Combine(AppContext.BaseDirectory, "Demo", "codeplug.yml");
        using DemoSessionState demoState = DemoSessionState.Create();
        var settingsStore = new UserSettingsStore(demoState.UserSettingsPath);
        var mainWindow = new MainWindow(
            demoCodeplug,
            settingsStore,
            new OperatorViewStore(demoState.OperatorViewPath),
            demoMode: true);

        try
        {
            mainWindow.Show();
            ConfigurationStudioWindow studio = mainWindow.CreateConfigurationStudioForCapture(
                ConfigurationStudioSection.Overview);
            studio.Show(mainWindow);
            studio.UpdateLayout();
            Assert.IsType<ConfigurationStudioViewModel>(studio.DataContext);
            Assert.False(studio.StudioViewModel.HasErrors);
            studio.CloseForSessionReplacement();

            if (!string.IsNullOrWhiteSpace(captureDirectory))
                await App.CaptureDemoScreenshotsCoreAsync(mainWindow, captureDirectory);

            VerifyReviewPlanMigratesOperatorState(mainWindow, settingsStore, demoCodeplug);
        }
        finally
        {
            mainWindow.Close();
        }
    }

    private static void VerifyReviewPlanMigratesOperatorState(
        MainWindow mainWindow,
        UserSettingsStore settingsStore,
        string demoCodeplug)
    {
        UserSettings settings = settingsStore.Load();
        settings.LastSelectedSystemName = "North Metro";
        settings.RxJitterBuffersBySystem["North Metro"] = new RxJitterBufferSetting();
        settings.ChannelVolumes["North Metro\u001FNorth Dispatch"] = 0.5;
        CodeplugGroupState state = CodeplugGroupStateStore.GetOrMigrate(settings, demoCodeplug);
        state.Memberships["Shared Operations"] =
        [
            new PatchMemberSetting
            {
                SystemName = "North Metro",
                DestinationId = 3101,
                ChannelName = "North Dispatch"
            }
        ];
        settingsStore.Save(settings);

        ConfigurationStudioWindow studio = mainWindow.CreateConfigurationStudioForCapture(
            ConfigurationStudioSection.Overview);
        ConfigurationStudioViewModel viewModel = studio.StudioViewModel;
        viewModel.Configuration.Systems[0].Name = "North Regional";
        viewModel.Configuration.Zones[0].Channels[0].Name = "Regional Dispatch";
        viewModel.Configuration.Groups[0].Name = "Regional Operations";
        viewModel.CommitFieldEdit();

        ConfigurationSavePlan plan = viewModel.CreateSavePlan(demoCodeplug);
        string json = plan.Files.Single(file => file.Category == "Operator settings").Content;
        UserSettings migrated = JsonSerializer.Deserialize<UserSettings>(json, JsonOptions)!;
        CodeplugGroupState migratedGroupState = CodeplugGroupStateStore.GetOrMigrate(migrated, demoCodeplug);

        Assert.Equal("North Regional", migrated.LastSelectedSystemName);
        Assert.True(migrated.RxJitterBuffersBySystem.ContainsKey("North Regional"));
        Assert.Equal(0.5, migrated.ChannelVolumes["North Regional\u001FRegional Dispatch"]);
        Assert.True(migratedGroupState.Memberships.ContainsKey("Regional Operations"));
        Assert.Equal("North Regional", migratedGroupState.Memberships["Regional Operations"][0].SystemName);
        Assert.Equal("Regional Dispatch", migratedGroupState.Memberships["Regional Operations"][0].ChannelName);
        string review = viewModel.BuildReviewText(plan);
        Assert.Contains("System state: North Metro → North Regional", review, StringComparison.Ordinal);
        Assert.Contains("Channel state: North Metro/North Dispatch → North Regional/Regional Dispatch", review, StringComparison.Ordinal);
        Assert.Contains("Group state: Shared Operations → Regional Operations", review, StringComparison.Ordinal);

        string saveAsDirectory = Path.Combine(Path.GetTempPath(), "dvmconsole-studio-saveas-tests", Guid.NewGuid().ToString("N"));
        string saveAsPath = Path.Combine(saveAsDirectory, "codeplug.yml");
        ConfigurationSavePlan saveAsPlan = viewModel.CreateSavePlan(saveAsPath);
        Assert.True(saveAsPlan.CanSave);
        Assert.Contains(saveAsPlan.Files, file =>
            file.Category == "Encryption key file" &&
            file.Path == Path.Combine(saveAsDirectory, "keys.clear"));
        Assert.Contains(saveAsPlan.Files, file =>
            file.Category == "RID alias file" &&
            file.Path == Path.Combine(saveAsDirectory, "aliases.yml"));
        string saveAsJson = saveAsPlan.Files.Single(file => file.Category == "Operator settings").Content;
        UserSettings saveAsSettings = JsonSerializer.Deserialize<UserSettings>(saveAsJson, JsonOptions)!;
        CodeplugGroupState copiedState = CodeplugGroupStateStore.GetOrMigrate(saveAsSettings, saveAsPath);
        Assert.True(copiedState.Memberships.ContainsKey("Regional Operations"));
    }
}
