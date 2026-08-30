using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;

namespace DvmConsole.Desktop;

public sealed class App : Application
{
    public static string? ConfigurationPath { get; set; }
    public static bool DemoMode { get; set; }
    public static string? DemoCaptureDirectory { get; set; }
    public static bool SmokeWindows { get; set; }
    public static string? SmokeResultPath { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        bool isHeadless = AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
            assembly.GetName().Name?.StartsWith("Avalonia.Headless", StringComparison.Ordinal) == true);
        if (!isHeadless)
        {
            this.AttachDeveloperTools();
        }
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainWindow mainWindow;
            if (DemoMode)
            {
                DemoSessionState demoState = DemoSessionState.Create();
                try
                {
                    mainWindow = new MainWindow(
                        ConfigurationPath ?? ResolveDemoConfigurationPath(AppContext.BaseDirectory),
                        new UserSettingsStore(demoState.UserSettingsPath),
                        new OperatorViewStore(demoState.OperatorViewPath),
                        demoMode: true);
                }
                catch
                {
                    demoState.Dispose();
                    throw;
                }
                desktop.Exit += (_, _) => demoState.Dispose();
            }
            else
            {
                mainWindow = new MainWindow(ConfigurationPath);
            }
            desktop.MainWindow = mainWindow;
            if (!string.IsNullOrWhiteSpace(DemoCaptureDirectory))
            {
                Dispatcher.UIThread.Post(() =>
                    TaskObservation.Observe(CaptureDemoScreenshotsAsync(
                        desktop,
                        mainWindow,
                        DemoCaptureDirectory)));
            }
            else if (SmokeWindows)
                Dispatcher.UIThread.Post(() =>
                    TaskObservation.Observe(SmokeWindowsAsync(desktop, mainWindow)));
        }

        base.OnFrameworkInitializationCompleted();
    }

    internal static string ResolveDemoConfigurationPath(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        return Path.GetFullPath(Path.Combine(baseDirectory, "Demo", "codeplug.yml"));
    }

    private static async Task SmokeWindowsAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow mainWindow)
    {
        try
        {
            mainWindow.Show();
            if (mainWindow.DataContext is not MainWindowViewModel viewModel)
                throw new InvalidOperationException("The main view model was not loaded.");

            bool originalDarkMode = viewModel.DarkMode;
            viewModel.DarkMode = false;
            await Task.Delay(75);
            RequireBackground(mainWindow, "#F3F5F7", "light");
            viewModel.DarkMode = true;
            await Task.Delay(75);
            RequireBackground(mainWindow, "#0D1116", "dark");
            viewModel.DarkMode = originalDarkMode;

            foreach (OperatorToolSection section in Enum.GetValues<OperatorToolSection>())
            {
                var window = new OperatorToolsWindow(viewModel, section);
                window.Show(mainWindow);
                await Task.Delay(75);
                if (section == OperatorToolSection.History && !window.IsHistoryViewportHookAttached)
                    throw new InvalidOperationException("The deferred History list did not initialize its viewport handling.");
                if (section == OperatorToolSection.EncryptionKeys && window.IsPendingSectionNavigation)
                    throw new InvalidOperationException("Encryption Key Status did not reveal the channel key-status section.");
                window.Close();
            }

            var logs = new DebugLogWindow(viewModel);
            logs.Show();
            await Task.Delay(75);
            logs.Close();

            var documentation = new DocumentationWindow();
            documentation.Show(mainWindow);
            await Task.Delay(75);
            documentation.Close();

            var about = new AboutWindow();
            about.Show(mainWindow);
            await Task.Delay(75);
            about.Close();

            WriteSmokeResult("PASS");
            Console.WriteLine("Desktop window smoke passed.");
            desktop.Shutdown(0);
        }
        catch (Exception exception)
        {
            WriteSmokeResult($"FAIL{Environment.NewLine}{exception}");
            Console.Error.WriteLine($"Desktop window smoke failed: {exception}");
            desktop.Shutdown(10);
        }
    }

    private static async Task CaptureDemoScreenshotsAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow mainWindow,
        string captureDirectory)
    {
        try
        {
            await CaptureDemoScreenshotsCoreAsync(mainWindow, captureDirectory);
            desktop.Shutdown(0);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Demo screenshot capture failed: {exception}");
            desktop.Shutdown(11);
        }
    }

    internal static async Task CaptureDemoScreenshotsCoreAsync(
        MainWindow mainWindow,
        string captureDirectory)
    {
        Console.WriteLine("Starting deterministic demo screenshot capture.");
        string outputDirectory = Path.GetFullPath(captureDirectory);
        Directory.CreateDirectory(outputDirectory);
        mainWindow.Show();
        Console.WriteLine("Demo main window opened for capture.");
        if (mainWindow.DataContext is not MainWindowViewModel viewModel)
            throw new InvalidOperationException("The demo view model was not loaded.");

        async Task CaptureMainAsync(
                string fileName,
                bool darkMode,
                double width,
                double height,
                bool showEngineeringHealth)
        {
            viewModel.DarkMode = darkMode;
            mainWindow.PrepareDemoCapture(width, height, showEngineeringHealth);
            await WaitForRenderAsync();
            SaveVisual(mainWindow, Path.Combine(outputDirectory, fileName));
        }

        await CaptureMainAsync(
                "console-dark.png",
                darkMode: true,
                1260,
                760,
                showEngineeringHealth: false);
        await CaptureMainAsync(
                "console-light.png",
                darkMode: false,
                1260,
                760,
                showEngineeringHealth: false);
        await CaptureMainAsync(
                "console-narrow.png",
                darkMode: true,
                880,
                560,
                showEngineeringHealth: false);
        await CaptureMainAsync(
                "console-wide.png",
                darkMode: true,
                1800,
                900,
                showEngineeringHealth: false);
        await CaptureMainAsync(
                "console-engineering.png",
                darkMode: true,
                1260,
                760,
                showEngineeringHealth: true);

        mainWindow.PrepareDemoCapture(
                1260,
                760,
                showEngineeringHealth: false);
        foreach ((string FileName, OperatorToolSection Section) capture in new[]
                     {
                         ("history.png", OperatorToolSection.History),
                         ("settings.png", OperatorToolSection.General)
                     })
        {
            var toolsWindow = new OperatorToolsWindow(viewModel, capture.Section)
            {
                Width = 1180,
                Height = 780
            };
            toolsWindow.Show(mainWindow);
            toolsWindow.InvalidateMeasure();
            toolsWindow.UpdateLayout();
            await WaitForRenderAsync();
            SaveVisual(toolsWindow, Path.Combine(outputDirectory, capture.FileName));
            toolsWindow.Close();
            await Task.Delay(50);
        }

        foreach ((string FileName, ConfigurationStudioSection Section) capture in new[]
                     {
                         ("configuration-studio-shell.png", ConfigurationStudioSection.Overview),
                         ("configuration-studio-system.png", ConfigurationStudioSection.Systems),
                         ("configuration-studio-zone.png", ConfigurationStudioSection.Zones),
                         ("configuration-studio-groups.png", ConfigurationStudioSection.Groups),
                         ("configuration-studio-encryption.png", ConfigurationStudioSection.EncryptionKeys)
                     })
        {
            ConfigurationStudioWindow studio = mainWindow.CreateConfigurationStudioForCapture(capture.Section);
            studio.Show(mainWindow);
            if (capture.Section == ConfigurationStudioSection.Zones)
                PrepareConfigurationStudioZoneCapture(studio.StudioViewModel);
            studio.InvalidateMeasure();
            studio.UpdateLayout();
            await WaitForRenderAsync();
            SaveVisual(studio, Path.Combine(outputDirectory, capture.FileName));

            if (capture.Section == ConfigurationStudioSection.Overview)
            {
                ConfigurationSavePlan plan = studio.StudioViewModel.CreateSavePlan(
                    studio.StudioViewModel.Document.SourcePath!);
                OperatorDialogParts review = OperatorDialogFactory.CreateConfirmation(
                    "Review & Save",
                    studio.StudioViewModel.BuildReviewText(plan),
                    "Save");
                review.Window.Show(studio);
                await WaitForRenderAsync();
                SaveVisual(review.Window, Path.Combine(outputDirectory, "configuration-studio-review.png"));
                review.Window.Close();
            }

            studio.CloseForSessionReplacement();
            await Task.Delay(50);
        }

        ConfigurationStudioWindow validationStudio = mainWindow.CreateConfigurationStudioForCapture(
            ConfigurationStudioSection.Overview);
        validationStudio.Show(mainWindow);
        validationStudio.StudioViewModel.SelectedSystem!.Address = string.Empty;
        validationStudio.StudioViewModel.CommitFieldEdit();
        validationStudio.StudioViewModel.OpenValidationDrawer();
        validationStudio.InvalidateMeasure();
        validationStudio.UpdateLayout();
        await WaitForRenderAsync();
        SaveVisual(validationStudio, Path.Combine(outputDirectory, "configuration-studio-validation.png"));
        validationStudio.CloseForSessionReplacement();

        ConfigurationStudioWindow narrowStudio = mainWindow.CreateConfigurationStudioForCapture(
            ConfigurationStudioSection.Zones);
        narrowStudio.Width = 1180;
        narrowStudio.Height = 760;
        narrowStudio.Show(mainWindow);
        PrepareConfigurationStudioZoneCapture(narrowStudio.StudioViewModel);
        narrowStudio.InvalidateMeasure();
        narrowStudio.UpdateLayout();
        await WaitForRenderAsync();
        SaveVisual(narrowStudio, Path.Combine(outputDirectory, "configuration-studio-zone-narrow.png"));
        narrowStudio.CloseForSessionReplacement();

        Console.WriteLine($"Demo screenshots written to {outputDirectory}");
    }

    private static void PrepareConfigurationStudioZoneCapture(ConfigurationStudioViewModel studio)
    {
        ZoneConfiguration zone = studio.SelectedZone
            ?? throw new InvalidOperationException("The Studio capture requires a zone.");
        string[] names =
        [
            "Campus Dispatch", "Campus Ops", "Campus Security", "Facilities",
            "Engineering", "Parking Services", "Campus Event 1", "Campus Event 2",
            "Shuttle Dispatch", "Shuttle Ops", "Shuttle Drivers", "Help Desk",
            "IT Support", "Health Center", "Residence Life", "Athletics"
        ];
        zone.Name = "Campus Network";
        zone.Channels.Clear();
        for (int index = 0; index < names.Length; index++)
        {
            zone.Channels.Add(new ChannelConfiguration
            {
                Name = names[index],
                System = "North Metro",
                Tgid = (3101 + index).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Mode = "dmr",
                Slot = index % 3 == 0 ? 2 : 1,
                Algo = index % 4 == 0 ? "aes" : "none",
                KeyId = index % 4 == 0 ? "0x2" : null,
                SelectableEncryption = index == 0,
                RxOnly = index is 9 or 10,
                CardSize = index is 0 or 1 or 2 or 8 or 9 ? "normal" : "small",
                ResourceColor = index % 3 == 0 ? "#087CF1" : index % 3 == 1 ? "#65B95A" : "#22D3EE"
            });
        }
        studio.CommitFieldEdit();
        studio.SelectedZone = zone;
        studio.SelectedChannel = zone.Channels[0];
        studio.IsZonePreviewExpanded = true;
    }

    private static async Task WaitForRenderAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(
            static () => { },
            DispatcherPriority.Loaded);
        await Task.Delay(250);
        await Dispatcher.UIThread.InvokeAsync(
            static () => { },
            DispatcherPriority.Render);
    }

    private static void SaveVisual(Visual visual, string path)
    {
        if (visual is Control control)
            control.UpdateLayout();
        int width = Math.Max(1, (int)Math.Ceiling(visual.Bounds.Width));
        int height = Math.Max(1, (int)Math.Ceiling(visual.Bounds.Height));
        using var bitmap = new RenderTargetBitmap(
            new PixelSize(width, height),
            new Vector(96, 96));
        bitmap.Render(visual);
        bitmap.Save(path);
    }

    internal static void InitializeSmokeResult()
        => WriteSmokeResult("RUNNING");

    internal static void RecordSmokeFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        WriteSmokeResult($"FAIL{Environment.NewLine}{exception}");
    }

    private static void WriteSmokeResult(string value)
    {
        if (string.IsNullOrWhiteSpace(SmokeResultPath))
            return;

        try
        {
            string path = Path.GetFullPath(SmokeResultPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, value + Environment.NewLine);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"Unable to write desktop smoke result: {exception.Message}");
        }
    }

    private static void RequireBackground(MainWindow window, string expectedColor, string themeName)
    {
        if (window.Background is not ISolidColorBrush brush || brush.Color != Color.Parse(expectedColor))
            throw new InvalidOperationException($"The {themeName} shell background did not update.");
    }
}
