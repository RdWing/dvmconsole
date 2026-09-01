using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

public sealed class App : Avalonia.Application
{
#if DEBUG
    private static int developerToolsAttached;
#endif
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
        if (!isHeadless && Interlocked.Exchange(ref developerToolsAttached, 1) == 0)
            this.AttachDeveloperTools();
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
                {
                    throw new InvalidOperationException(
                        "Encryption Key Status did not reveal the channel key-status section. " +
                        window.PendingSectionNavigationDiagnostic);
                }
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

            await SmokeLoadedConfigurationSystemActionsAsync(mainWindow);
            await SmokeConfigurationStudioActionsAsync(mainWindow);

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

    private static async Task SmokeLoadedConfigurationSystemActionsAsync(MainWindow mainWindow)
    {
        if (mainWindow.DataContext is not MainWindowViewModel ownerViewModel ||
            string.IsNullOrWhiteSpace(ownerViewModel.CurrentCodeplugPath))
        {
            throw new InvalidOperationException("A loaded codeplug is required for the Configuration Studio smoke.");
        }

        using DemoSessionState smokeState = DemoSessionState.Create();
        var smokeHost = new MainWindow(
            ownerViewModel.CurrentCodeplugPath,
            new UserSettingsStore(smokeState.UserSettingsPath),
            new OperatorViewStore(smokeState.OperatorViewPath),
            demoMode: true);
        ConfigurationStudioWindow studio = smokeHost.CreateConfigurationStudioForCapture(
            ConfigurationStudioSection.Systems);
        studio.DialogConfirmationOverride = (_, _, _) => Task.FromResult(true);
        smokeHost.Show(mainWindow);
        studio.Show(smokeHost);
        await WaitForRenderAsync();
        try
        {
            var exercised = new HashSet<string>(StringComparer.Ordinal);
            int initialCount = studio.StudioViewModel.Systems.Count;

            await ClickStudioButtonAsync(studio, "Add system", exercised);
            Require(
                studio.StudioViewModel.Systems.Count == initialCount + 1,
                "The Configuration Studio Add system button did not add exactly one FNE.");

            studio.StudioViewModel.SelectedSystem = studio.StudioViewModel.Systems[0];
            await ClickStudioButtonAsync(studio, "Duplicate system", exercised);
            Require(
                studio.StudioViewModel.Systems.Count == initialCount + 2 &&
                studio.StudioViewModel.Configuration.Systems.Count == initialCount + 2 &&
                studio.StudioViewModel.Systems.Distinct().Count() == initialCount + 2,
                "The Configuration Studio Duplicate system button did not add exactly one FNE.");

            await ClickStudioButtonAsync(studio, "Delete system", exercised);
            await WaitUntilAsync(
                () => studio.StudioViewModel.Systems.Count == initialCount + 1,
                "The Configuration Studio Delete system button did not delete exactly one FNE.");
        }
        finally
        {
            studio.CloseForSessionReplacement();
            smokeHost.Close();
        }
    }

    private static async Task SmokeConfigurationStudioActionsAsync(MainWindow owner)
    {
        using DemoSessionState smokeState = DemoSessionState.Create();
        string root = Path.GetDirectoryName(smokeState.UserSettingsPath)!;
        var smokeHost = new MainWindow(
            ResolveDemoConfigurationPath(AppContext.BaseDirectory),
            new UserSettingsStore(smokeState.UserSettingsPath),
            new OperatorViewStore(smokeState.OperatorViewPath),
            demoMode: true);
        smokeHost.Show(owner);
        await WaitForRenderAsync();

        var exercised = new HashSet<string>(StringComparer.Ordinal);
        var messages = new List<(string Title, string Message)>();
        string pickerRoot = Path.Combine(root, "StudioSmokeFiles");
        Directory.CreateDirectory(pickerRoot);
        string keyPath = Path.Combine(pickerRoot, "smoke-keys.clear");
        string aliasPath = Path.Combine(pickerRoot, "smoke-aliases.yml");
        File.WriteAllText(keyPath, "keys:\n  - protocol: p25\n    keyId: 1\n    algId: 132\n    key: 000102030405060708090A0B0C0D0E0F\n");
        File.WriteAllText(aliasPath, "- rid: 7001\n  alias: Smoke Unit\n");

        ConfigurationStudioWindow studio = smokeHost.CreateConfigurationStudioForCapture(
            ConfigurationStudioSection.Overview);
        studio.DialogConfirmationOverride = (title, _, _) =>
            Task.FromResult(!title.Equals("Reload active configuration?", StringComparison.Ordinal));
        studio.EditMenuConfirmationOverride = (_, _, _) => Task.FromResult(true);
        studio.MessageOverride = (title, message) =>
        {
            messages.Add((title, message));
            return Task.CompletedTask;
        };
        studio.CompanionFilePickerOverride = async (title, _) =>
        {
            string path = title.Contains("encryption", StringComparison.OrdinalIgnoreCase)
                ? keyPath
                : aliasPath;
            return await studio.StorageProvider.TryGetFileFromPathAsync(path);
        };
        var exportPaths = new List<string>();
        studio.CodeplugSaveFilePickerOverride = async (_, suggestedName) =>
        {
            string directory = Path.Combine(pickerRoot, $"export-{exportPaths.Count + 1}");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, suggestedName);
            File.WriteAllText(path, string.Empty);
            exportPaths.Add(path);
            return await studio.StorageProvider.TryGetFileFromPathAsync(path);
        };

        studio.Show(smokeHost);
        await WaitForRenderAsync();
        try
        {
            await SmokeStudioNavigationAsync(studio, exercised);
            await SmokeStudioSystemActionsAsync(studio, exercised);
            await SmokeStudioZoneActionsAsync(studio, exercised);
            await SmokeStudioStreamActionsAsync(studio, exercised);
            await SmokeStudioGroupActionsAsync(studio, exercised);
            await SmokeStudioKeyActionsAsync(studio, exercised);
            await SmokeStudioFileActionsAsync(studio, messages, exportPaths, exercised);
            await SmokeStudioFooterActionsAsync(studio, messages, exportPaths, exercised);
        }
        finally
        {
            if (studio.IsVisible)
                studio.CloseForSessionReplacement();
        }

        ConfigurationStudioWindow closeStudio = smokeHost.CreateConfigurationStudioForCapture(
            ConfigurationStudioSection.Groups);
        closeStudio.DialogConfirmationOverride = (_, _, _) => Task.FromResult(true);
        closeStudio.MessageOverride = (_, _) => Task.CompletedTask;
        closeStudio.Show(smokeHost);
        await WaitForRenderAsync();
        await ClickStudioButtonAsync(
            closeStudio,
            "Apply operator group changes and close",
            exercised);
        await WaitUntilAsync(
            () => !closeStudio.IsVisible,
            "Apply & close did not close Configuration Studio.");

        RequireStudioActionCoverage(exercised);
        smokeHost.Close();
    }

    private static async Task SmokeStudioNavigationAsync(
        ConfigurationStudioWindow studio,
        HashSet<string> exercised)
    {
        foreach ((string name, ConfigurationStudioSection section) in new[]
                 {
                     ("Configuration overview", ConfigurationStudioSection.Overview),
                     ("FNE systems", ConfigurationStudioSection.Systems),
                     ("Web stream configurations", ConfigurationStudioSection.Streams),
                     ("Group configurations", ConfigurationStudioSection.Groups),
                     ("Encryption keys", ConfigurationStudioSection.EncryptionKeys),
                     ("Configuration files", ConfigurationStudioSection.Files)
                 })
        {
            await ClickStudioButtonAsync(studio, name, exercised);
            Require(
                studio.StudioViewModel.SelectedNavigation.Section == section,
                $"The '{name}' navigation button did not open {section}.");
        }

        ConfigurationHierarchyNode zone = studio.StudioViewModel.ConfigurationHierarchy
            .SelectMany(system => system.Children)
            .First(node => node.IsZone);
        studio.StudioViewModel.SelectedHierarchyNode = zone;
        exercised.Add("Zone hierarchy");
        Require(
            studio.StudioViewModel.SelectedNavigation.Section == ConfigurationStudioSection.Zones,
            "Selecting a zone in the FNE hierarchy did not open Zones and Channels.");
    }

    private static async Task SmokeStudioSystemActionsAsync(
        ConfigurationStudioWindow studio,
        HashSet<string> exercised)
    {
        studio.SelectSection(ConfigurationStudioSection.Systems);
        await WaitForRenderAsync();
        int initialCount = studio.StudioViewModel.Systems.Count;
        await ClickStudioButtonAsync(studio, "Add system", exercised);
        Require(studio.StudioViewModel.Systems.Count == initialCount + 1, "Add system did not add one system.");
        await ClickStudioButtonAsync(studio, "Duplicate system", exercised);
        Require(studio.StudioViewModel.Systems.Count == initialCount + 2, "Duplicate system did not add one system.");
        await ClickStudioButtonAsync(studio, "Delete system", exercised);
        await WaitUntilAsync(
            () => studio.StudioViewModel.Systems.Count == initialCount + 1,
            "Delete system did not remove one system.");

        int initialChannelCount = studio.StudioViewModel.Configuration.Zones
            .SelectMany(zone => zone.Channels)
            .Count();
        await ClickStudioButtonAsync(studio, "Add channel to selected FNE system", exercised);
        Require(
            studio.StudioViewModel.Configuration.Zones.SelectMany(zone => zone.Channels).Count() == initialChannelCount + 1 &&
            studio.StudioViewModel.IsZones,
            "Add channel from the selected FNE system did not open the new channel.");
        studio.SelectSection(ConfigurationStudioSection.Systems);
        await WaitForRenderAsync();

    }

    private static async Task SmokeStudioZoneActionsAsync(
        ConfigurationStudioWindow studio,
        HashSet<string> exercised)
    {
        studio.SelectSection(ConfigurationStudioSection.Zones);
        await WaitForRenderAsync();
        ConfigurationStudioViewModel viewModel = studio.StudioViewModel;
        ZoneConfiguration zone = viewModel.Zones[0];
        viewModel.SelectedZone = zone;
        viewModel.SelectedChannel = zone.Channels[^1];
        await WaitForRenderAsync();

        int initialChannelCount = zone.Channels.Count;
        await ClickStudioEditMenuAsync(studio, ConfigurationStudioEditCommand.AddChannel, exercised);
        Require(zone.Channels.Count == initialChannelCount + 1, "Add channel did not add one channel.");
        await ClickStudioEditMenuAsync(studio, ConfigurationStudioEditCommand.DuplicateChannel, exercised);
        Require(zone.Channels.Count == initialChannelCount + 2, "Duplicate channel did not add one channel.");

        ChannelConfiguration channel = viewModel.SelectedChannel
            ?? throw new InvalidOperationException("Channel actions left no selected channel.");
        int originalIndex = zone.Channels.IndexOf(channel);
        await ClickStudioEditMenuAsync(studio, ConfigurationStudioEditCommand.MoveChannelUp, exercised);
        Require(zone.Channels.IndexOf(channel) == originalIndex - 1, "Move channel up did not move the selected channel.");
        await ClickStudioEditMenuAsync(studio, ConfigurationStudioEditCommand.MoveChannelDown, exercised);
        Require(zone.Channels.IndexOf(channel) == originalIndex, "Move channel down did not move the selected channel.");

        foreach ((string name, string value) in new[]
                 {
                     ("Small channel card", "small"),
                     ("Normal channel card", "normal"),
                     ("Large channel card", "large")
                 })
        {
            await ClickStudioButtonAsync(studio, name, exercised);
            Require(channel.CardSize == value, $"The {name} button did not update the selected channel.");
        }
        foreach ((string name, string value) in new[]
                 {
                     ("Blue resource color", "#087CF1"),
                     ("Cyan resource color", "#22D3EE"),
                     ("Green resource color", "#65B95A"),
                     ("Orange resource color", "#F47A1F"),
                     ("Purple resource color", "#7E36D4"),
                     ("Red resource color", "#DC2F60")
                 })
        {
            await ClickStudioButtonAsync(studio, name, exercised);
            Require(channel.ResourceColor == value, $"The {name} button did not update the selected channel.");
        }

        Expander liveLayout = studio.GetVisualDescendants().OfType<Expander>().Single(expander =>
            AutomationProperties.GetName(expander) == "Live zone layout");
        liveLayout.IsExpanded = true;
        await WaitForRenderAsync();
        Require(viewModel.IsZonePreviewExpanded, "The live zone layout control did not expand.");
        liveLayout.IsExpanded = false;
        exercised.Add("Live zone layout");

        await ClickStudioEditMenuAsync(studio, ConfigurationStudioEditCommand.ApplySelectedCardSize, exercised);
        Require(channel.CardSize == "large", "Apply selected card size changed the selected source size unexpectedly.");
        await ClickStudioEditMenuAsync(studio, ConfigurationStudioEditCommand.SetSelectedRowsRxOnly, exercised);
        Require(channel.RxOnly, "Set selected rows RX only did not update the selected channel.");
        await ClickStudioEditMenuAsync(studio, ConfigurationStudioEditCommand.SetSelectedRowsTxCapable, exercised);
        Require(!channel.RxOnly, "Set selected rows TX capable did not update the selected channel.");

        await ClickStudioEditMenuAsync(studio, ConfigurationStudioEditCommand.DeleteChannel, exercised);
        await WaitUntilAsync(
            () => zone.Channels.Count == initialChannelCount + 1,
            "Delete channel did not remove one channel.");

        int initialZoneCount = viewModel.Zones.Count;
        await ClickStudioEditMenuAsync(studio, ConfigurationStudioEditCommand.AddZone, exercised);
        Require(viewModel.Zones.Count == initialZoneCount + 1, "Add zone did not add one zone.");
        await ClickStudioEditMenuAsync(studio, ConfigurationStudioEditCommand.DuplicateZone, exercised);
        Require(viewModel.Zones.Count == initialZoneCount + 2, "Duplicate zone did not add one zone.");
        await ClickStudioEditMenuAsync(studio, ConfigurationStudioEditCommand.DeleteZone, exercised);
        await WaitUntilAsync(
            () => viewModel.Zones.Count == initialZoneCount + 1,
            "Delete zone did not remove one zone.");
    }

    private static async Task SmokeStudioStreamActionsAsync(
        ConfigurationStudioWindow studio,
        HashSet<string> exercised)
    {
        studio.SelectSection(ConfigurationStudioSection.Streams);
        await WaitForRenderAsync();
        int initialCount = studio.StudioViewModel.Streams.Count;
        await ClickStudioButtonAsync(studio, "Add web stream", exercised);
        Require(studio.StudioViewModel.Streams.Count == initialCount + 1, "Add web stream did not add one stream.");
        await ClickStudioButtonAsync(studio, "Delete web stream", exercised);
        await WaitUntilAsync(
            () => studio.StudioViewModel.Streams.Count == initialCount,
            "Delete web stream did not remove one stream.");
    }

    private static async Task SmokeStudioGroupActionsAsync(
        ConfigurationStudioWindow studio,
        HashSet<string> exercised)
    {
        studio.SelectSection(ConfigurationStudioSection.Groups);
        await WaitForRenderAsync();
        ConfigurationStudioViewModel viewModel = studio.StudioViewModel;
        int initialCount = viewModel.Groups.Count;
        await ClickStudioButtonAsync(studio, "Add group", exercised);
        Require(viewModel.Groups.Count == initialCount + 1, "Add group did not add one group.");
        await ClickStudioButtonAsync(studio, "Delete group", exercised);
        await WaitUntilAsync(() => viewModel.Groups.Count == initialCount, "Delete group did not remove one group.");

        await ClickStudioButtonAsync(studio, "Add group", exercised);
        Require(viewModel.Groups.Count == initialCount + 1, "The undo smoke could not add a group.");
        await ClickStudioButtonAsync(studio, "Undo configuration edit", exercised);
        Require(viewModel.Groups.Count == initialCount, "Undo did not remove the added group.");
        await ClickStudioButtonAsync(studio, "Redo configuration edit", exercised);
        Require(viewModel.Groups.Count == initialCount + 1, "Redo did not restore the added group.");
        await ClickStudioButtonAsync(studio, "Delete group", exercised);
        await WaitUntilAsync(() => viewModel.Groups.Count == initialCount, "The history smoke could not remove its group.");

        Require(viewModel.CanUseOperationalGroups, "The isolated Studio smoke is not attached to its active configuration.");
        PatchGroupEditorViewModel group = viewModel.OperationalGroups[0];
        await ClickStudioButtonAsync(
            studio,
            "Apply this operator group",
            exercised,
            button => ReferenceEquals(button.Tag, group));
        await ClickStudioButtonAsync(studio, "Apply all operator group changes", exercised);

        PatchGroupEditorViewModel multiSelect = viewModel.OperationalGroups.Single(candidate => candidate.IsMultiSelect);
        foreach (PatchMemberEditorViewModel member in multiSelect.Members)
            member.IsMember = false;
        await ClickStudioButtonAsync(
            studio,
            "Multi-select PTT",
            exercised,
            button => ReferenceEquals(button.Tag, multiSelect));
        await WaitForRenderAsync();
        Require(!multiSelect.IsPttActive, "The empty multi-select smoke unexpectedly keyed transmit.");
    }

    private static async Task SmokeStudioKeyActionsAsync(
        ConfigurationStudioWindow studio,
        HashSet<string> exercised)
    {
        studio.SelectSection(ConfigurationStudioSection.EncryptionKeys);
        await WaitForRenderAsync();
        int initialCount = studio.StudioViewModel.KeyEntries.Count;
        await ClickStudioButtonAsync(studio, "Add encryption key", exercised);
        Require(studio.StudioViewModel.KeyEntries.Count == initialCount + 1, "Add encryption key did not add one key.");
        await ClickStudioButtonAsync(studio, "Delete encryption key", exercised);
        await WaitUntilAsync(
            () => studio.StudioViewModel.KeyEntries.Count == initialCount,
            "Delete encryption key did not remove one key.");
    }

    private static async Task SmokeStudioFileActionsAsync(
        ConfigurationStudioWindow studio,
        List<(string Title, string Message)> messages,
        List<string> exportPaths,
        HashSet<string> exercised)
    {
        studio.SelectSection(ConfigurationStudioSection.Files);
        await WaitForRenderAsync();
        int messageCount = messages.Count;
        await ClickStudioButtonAsync(studio, "Choose managed key file", exercised);
        await WaitUntilAsync(
            () => messages.Count > messageCount && studio.StudioViewModel.Configuration.KeyFile == "smoke-keys.clear",
            "The key-file browse button did not stage the selected managed key file.");

        SystemConfiguration aliasSystem = studio.StudioViewModel.Systems[0];
        studio.StudioViewModel.SelectedAliasSystem = aliasSystem;
        messageCount = messages.Count;
        await ClickStudioButtonAsync(
            studio,
            "Choose managed RID alias file",
            exercised,
            button => ReferenceEquals(button.Tag, aliasSystem));
        await WaitUntilAsync(
            () => messages.Count > messageCount && aliasSystem.AliasPath == "smoke-aliases.yml",
            "The Files alias browse button did not stage the selected managed alias file.");

        int initialAliasCount = studio.StudioViewModel.Aliases.Count;
        await ClickStudioButtonAsync(studio, "Add RID alias", exercised);
        Require(studio.StudioViewModel.Aliases.Count == initialAliasCount + 1, "Add RID alias did not add one alias.");
        ConfigurationAliasRow alias = studio.StudioViewModel.SelectedAlias
            ?? throw new InvalidOperationException("Add RID alias did not select the new alias.");
        alias.Rid = 4242;
        alias.Name = "Smoke alias";
        studio.StudioViewModel.CommitAliasEdit();
        Require(
            studio.StudioViewModel.Aliases.Count == initialAliasCount + 1 &&
            alias.Rid == 4242 && alias.Name == "Smoke alias",
            "A newly added RID alias was not editable without a second Add action.");
        await ClickStudioButtonAsync(studio, "Delete RID alias", exercised);
        await WaitUntilAsync(
            () => studio.StudioViewModel.Aliases.Count == initialAliasCount,
            "Delete RID alias did not remove one alias.");

        await ClickExportButtonAsync(studio, "Export full configuration copy", messages, exportPaths, exercised);
        await ClickExportButtonAsync(studio, "Export sanitized support copy", messages, exportPaths, exercised);
    }

    private static async Task SmokeStudioFooterActionsAsync(
        ConfigurationStudioWindow studio,
        List<(string Title, string Message)> messages,
        List<string> exportPaths,
        HashSet<string> exercised)
    {
        ConfigurationStudioViewModel viewModel = studio.StudioViewModel;
        studio.SelectSection(ConfigurationStudioSection.Systems);
        await WaitForRenderAsync();
        SystemConfiguration system = viewModel.Systems[0];
        viewModel.SelectedSystem = system;
        string systemName = system.Name;
        string validAddress = system.Address;
        system.Address = string.Empty;
        viewModel.CommitFieldEdit();
        Require(viewModel.HasErrors, "The validation smoke did not create an address error.");

        await ClickStudioButtonAsync(studio, "Configuration validation details", exercised);
        Require(viewModel.IsValidationDrawerOpen, "The validation button did not open the validation drawer.");
        await ClickStudioButtonAsync(studio, "Open configuration validation issue", exercised);
        await ClickStudioButtonAsync(studio, "Close configuration validation details", exercised);
        Require(!viewModel.IsValidationDrawerOpen, "The validation Close button did not close the drawer.");

        SystemConfiguration currentSystem = viewModel.Systems.Single(candidate => candidate.Name == systemName);
        viewModel.SelectedSystem = currentSystem;
        currentSystem.Address = validAddress;
        viewModel.CommitFieldEdit();
        Require(!viewModel.HasErrors, "The validation smoke could not restore a valid configuration.");

        await ClickExportButtonAsync(studio, "Export configuration YAML", messages, exportPaths, exercised);

        int messageCount = messages.Count;
        await ClickStudioButtonAsync(studio, "Save configuration copy", exercised);
        await WaitUntilAsync(
            () => messages.Skip(messageCount).Any(message => message.Title == "Configuration saved") && !viewModel.IsDirty,
            "Save a Copy did not commit the isolated managed copy.");

        viewModel.AddGroup();
        Require(viewModel.IsDirty, "The review-and-save smoke could not create a dirty draft.");
        messageCount = messages.Count;
        await ClickStudioButtonAsync(studio, "Review and save configuration", exercised);
        await WaitUntilAsync(
            () => messages.Skip(messageCount).Any(message => message.Title == "Configuration saved") && !viewModel.IsDirty,
            "Review & Save did not commit the isolated managed revision.");
    }

    private static async Task ClickExportButtonAsync(
        ConfigurationStudioWindow studio,
        string automationName,
        List<(string Title, string Message)> messages,
        List<string> exportPaths,
        HashSet<string> exercised)
    {
        int messageCount = messages.Count;
        int exportCount = exportPaths.Count;
        await ClickStudioButtonAsync(studio, automationName, exercised);
        await WaitUntilAsync(
            () => exportPaths.Count == exportCount + 1 &&
                  messages.Count > messageCount,
            $"The '{automationName}' action did not select a destination and report an export.");
        string path = exportPaths[^1];
        (string title, string message) = messages[^1];
        Require(
            File.Exists(path) && new FileInfo(path).Length > 0 &&
            title.StartsWith("Export complete", StringComparison.Ordinal),
            $"The '{automationName}' action did not complete its export. {title}: {message}");
    }

    private static async Task ClickStudioEditMenuAsync(
        ConfigurationStudioWindow studio,
        ConfigurationStudioEditCommand command,
        HashSet<string> exercised)
    {
        ConfigurationStudioZonesView zones = studio.GetVisualDescendants()
            .OfType<ConfigurationStudioZonesView>()
            .Single();
        Menu menu = zones.FindControl<Menu>("ZoneEditMenu")
            ?? throw new InvalidOperationException("The Configuration Studio Edit menu was not created.");
        MenuItem root = menu.Items.OfType<MenuItem>().Single();
        MenuItem item = root.Items.OfType<MenuItem>().Single(candidate => Equals(candidate.Tag, command.ToString()));
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        exercised.Add($"Edit menu: {command}");
        await WaitForRenderAsync();
    }

    private static async Task ClickStudioButtonAsync(
        Control root,
        string automationName,
        HashSet<string> exercised,
        Func<Button, bool>? predicate = null)
    {
        Button[] matches = root.GetVisualDescendants()
            .OfType<Button>()
            .Where(button => AutomationProperties.GetName(button) == automationName)
            .Where(button => predicate?.Invoke(button) ?? true)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"Expected one '{automationName}' Configuration Studio button, found {matches.Length}.");
        }
        if (!matches[0].IsEnabled)
            throw new InvalidOperationException($"The '{automationName}' Configuration Studio button was disabled.");
        matches[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        exercised.Add(automationName);
        await WaitForRenderAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failureMessage)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            await Task.Delay(40);
        }
        if (!condition())
            throw new InvalidOperationException(failureMessage);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void RequireStudioActionCoverage(HashSet<string> exercised)
    {
        string[] required =
        [
            "Configuration overview", "FNE systems", "Zone hierarchy", "Web stream configurations",
            "Group configurations", "Encryption keys", "Configuration files",
            "Add system", "Duplicate system", "Delete system", "Add channel to selected FNE system",
            "Choose managed RID alias file",
            "Edit menu: AddChannel", "Edit menu: DuplicateChannel", "Edit menu: DeleteChannel",
            "Edit menu: MoveChannelUp", "Edit menu: MoveChannelDown", "Edit menu: ApplySelectedCardSize",
            "Edit menu: SetSelectedRowsRxOnly", "Edit menu: SetSelectedRowsTxCapable",
            "Edit menu: AddZone", "Edit menu: DuplicateZone", "Edit menu: DeleteZone",
            "Small channel card", "Normal channel card", "Large channel card", "Live zone layout",
            "Blue resource color", "Cyan resource color", "Green resource color", "Orange resource color",
            "Purple resource color", "Red resource color",
            "Add web stream", "Delete web stream", "Add group", "Delete group",
            "Apply this operator group", "Apply all operator group changes",
            "Apply operator group changes and close", "Multi-select PTT",
            "Add encryption key", "Delete encryption key", "Choose managed key file",
            "Add RID alias", "Delete RID alias", "Export full configuration copy",
            "Export sanitized support copy", "Configuration validation details",
            "Open configuration validation issue", "Close configuration validation details",
            "Undo configuration edit", "Redo configuration edit", "Export configuration YAML",
            "Save configuration copy", "Review and save configuration"
        ];
        string[] missing = required.Where(action => !exercised.Contains(action)).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"Configuration Studio smoke missed: {string.Join(", ", missing)}.");
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
                         ("configuration-studio-encryption.png", ConfigurationStudioSection.EncryptionKeys),
                         ("configuration-studio-files.png", ConfigurationStudioSection.Files)
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
                ConfigurationSavePlan plan = studio.CreateSavePlanForCapture(
                    studio.StudioViewModel.Document.SourcePath!);
                OperatorDialogParts review = OperatorDialogFactory.CreateConfirmation(
                    "Review & Save",
                    studio.BuildSaveReviewForCapture(plan),
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
        ZoneConfiguration zone = studio.Zones.SingleOrDefault(
            candidate => candidate.Name.Equals("Campus Network", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The Studio capture requires the expanded Campus Network demo zone.");
        if (zone.Channels.Count != 16)
            throw new InvalidOperationException("The expanded Campus Network demo zone must contain 16 channels.");

        studio.SelectedZone = zone;
        studio.SelectedChannel = zone.Channels[0];
        studio.IsZonePreviewExpanded = true;
    }

    internal static async Task WaitForRenderAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(
            static () => { },
            DispatcherPriority.Loaded);
        await Task.Delay(250);
        await Dispatcher.UIThread.InvokeAsync(
            static () => { },
            DispatcherPriority.Render);
    }

    internal static void SaveVisual(Visual visual, string path)
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
