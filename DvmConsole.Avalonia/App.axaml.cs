// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using dvmconsole;
using DvmConsole.Avalonia.Audio;
using DvmConsole.Avalonia.Dialogs;
using DvmConsole.Avalonia.Persistence;
using DvmConsole.Avalonia.Services;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Avalonia.Views;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Networking;
using DvmConsole.Platform;
using DvmConsole.Platform.Audio;
using DvmConsole.Platform.Audio.Mac;
using DvmConsole.Platform.Audio.Vocoder;
using DvmConsole.Platform.Hotkeys;
using DvmConsole.Platform.Hotkeys.Mac;
using DvmConsole.Platform.Native;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace DvmConsole.Avalonia
{
    public partial class App : Application
    {
        private ApplicationDiagnostics? applicationDiagnostics;

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        /// <summary>
        /// Creates the macOS CoreAudio device catalog, or null when the
        /// host is not macOS or the catalog cannot be constructed. The
        /// catalog constructor throws <see cref="PlatformNotSupportedException"/>
        /// off macOS and may throw <see cref="AudioDeviceException"/> when
        /// CoreAudio listener registration fails; both are expected
        /// runtime conditions and degrade to no audio settings rather
        /// than failing the application.
        /// </summary>
        internal static IAudioDeviceCatalog? CreateAudioDeviceCatalog()
        {
            if (!PlatformInfo.IsMacOS)
            {
                return null;
            }

            try
            {
                return new MacAudioDeviceCatalog();
            }
            catch (AudioDeviceException)
            {
                return null;
            }
            catch (PlatformNotSupportedException)
            {
                return null;
            }
        }

        /// <summary>
        /// Creates the global hotkey service for the application
        /// lifetime. On macOS the CGEventTap-backed adapter is composed
        /// with its TCC permission probe: the probe reports whether the
        /// process holds Accessibility and Input Monitoring permission,
        /// so GetCapability can surface PermissionRequired and
        /// registration is denied (never prompted, never bypasses TCC)
        /// until the user grants access in System Settings. On every
        /// other host the unavailable fallback is composed: every
        /// gesture reports unsupported and no hotkey event ever fires.
        /// The application owns the service for its whole lifetime;
        /// disposal is handled by a later concrete factory/lifecycle
        /// slice.
        /// </summary>
        internal static IGlobalHotkeyService CreateGlobalHotkeyService()
            => PlatformInfo.IsMacOS
                ? new MacGlobalHotkeyService(new CoreGraphicsEventTap(), new MacPermissionProbe())
                : new UnavailableGlobalHotkeyService();

        /// <summary>
        /// Creates the macOS CGEventSourceKeyState-backed key-state
        /// reader used by the PTT hotkey key-up watchdog, or null on
        /// every other host where the watchdog stays dormant. The
        /// application owns the reader for its whole lifetime.
        /// </summary>
        internal static IKeyboardKeyStateReader? CreateKeyStateReader()
            => PlatformInfo.IsMacOS ? new MacKeyStateReader() : null;

        /// <summary>
        /// Runs the startup vocoder-readiness check through the
        /// native-library probe and returns its result. The probe owns
        /// loading, export resolution and handle release; this shell
        /// only maps the outcome. The final readiness line is written to
        /// stdout and flushed so headless SSH/launchd logs observe it: the
        /// stable <c>libvocoder ready</c> line on success, the probe
        /// diagnostic on failure (which also keeps its existing debug
        /// output sink). The check itself never throws.
        /// </summary>
        internal static VocoderReadinessResult CheckVocoderReadiness(
            DiagnosticLogSink? diagnosticSink = null)
        {
            var result = new VocoderReadiness(new NativeLibraryProbe()).Check();

            if (result.IsReady)
            {
                System.Console.WriteLine("libvocoder ready");
                diagnosticSink?.Write("libvocoder ready");
            }
            else if (result.Diagnostic is { } diagnostic)
            {
                System.Diagnostics.Debug.WriteLine(diagnostic);
                System.Console.WriteLine(diagnostic);
                diagnosticSink?.Write(diagnostic);
            }

            System.Console.Out.Flush();

            return result;
        }

        /// <summary>
        /// Loads each codeplug system's alias file and composes the
        /// per-system alias resolver for the call-history slice,
        /// WPF-parity (MainWindow.xaml.cs:1064-1065). Inline aliases are
        /// the seed; an existing external alias file replaces them. A
        /// missing or unreadable external file leaves inline aliases in
        /// place, so alias loading never fails startup. A null codeplug
        /// yields a null resolver (no aliases anywhere).
        /// </summary>
        internal static AliasResolver? BuildAliasResolver(Codeplug? codeplug)
        {
            if (codeplug is null)
            {
                return null;
            }

            var aliasesBySystem = new Dictionary<string, IReadOnlyList<RadioAlias>>();

            foreach (var system in codeplug.Systems ?? new List<Codeplug.System>())
            {
                if (system is null || string.IsNullOrWhiteSpace(system.Name))
                {
                    continue;
                }

                IReadOnlyList<RadioAlias> aliases =
                    system.RidAlias ?? new List<RadioAlias>();

                if (!string.IsNullOrWhiteSpace(system.AliasPath)
                    && File.Exists(system.AliasPath))
                {
                    try
                    {
                        aliases = AliasTools.LoadAliases(system.AliasPath);
                    }
                    catch
                    {
                        // Keep inline aliases when an external file is bad.
                    }
                }

                aliasesBySystem[system.Name] = aliases;
            }

            return new AliasResolver(aliasesBySystem);
        }

        /// <summary>
        /// Creates the call-history store with the WPF console-RID
        /// suppression rule. The configured RID is indexed by system name
        /// once at startup so receive-frame classification stays cheap and
        /// never depends on mutable codeplug objects.
        /// </summary>
        internal static CallHistoryStore CreateCallHistoryStore(Codeplug codeplug)
        {
            var consoleRids = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

            foreach (var system in codeplug.Systems ?? new List<Codeplug.System>())
            {
                if (system is null
                    || string.IsNullOrWhiteSpace(system.Name)
                    || !uint.TryParse(system.Rid, out var consoleRid))
                {
                    continue;
                }

                consoleRids[system.Name] = consoleRid;
            }

            return new CallHistoryStore(
                suppress: (systemName, sourceId) =>
                    consoleRids.TryGetValue(systemName, out var consoleRid)
                    && consoleRid == sourceId);
        }

        /// <summary>
        /// Creates the one application TAR recorder from the normalized
        /// persisted section. The persistence adapter owns load normalization;
        /// this composition layer only supplies the shared fallback root and
        /// the WPF-compatible resource-key, talkgroup-id, then channel-name
        /// lookup order used by recording and viewer operations.
        /// </summary>
        internal static TarRecorder CreateTarRecorder(
            TarSettingsPersistence persistence,
            string defaultRecordingRoot)
        {
            if (persistence is null)
            {
                throw new System.ArgumentNullException(nameof(persistence));
            }

            var section = new UserSettingsTarSection();
            bool loaded;
            try
            {
                loaded = persistence.TryLoad(out section);
            }
            catch
            {
                loaded = false;
                section = new UserSettingsTarSection();
            }

            if (!loaded)
            {
                section.TarRecordingsRootPath = defaultRecordingRoot;
            }

            var configs = section.TarChannelConfigs
                ?? new Dictionary<string, TarChannelConfig>(StringComparer.OrdinalIgnoreCase);
            return new TarRecorder(
                section.TarRecordingsRootPath,
                defaultRecordingRoot,
                (resourceKey, channelName, talkgroupId) =>
                {
                    IReadOnlyDictionary<string, TarChannelConfig> currentConfigs = configs;
                    try
                    {
                        if (persistence.TryLoad(out UserSettingsTarSection currentSection)
                            && currentSection.TarChannelConfigs is not null)
                        {
                            currentConfigs = currentSection.TarChannelConfigs;
                        }
                    }
                    catch
                    {
                        // Keep the last known normalized snapshot when a
                        // settings read races a malformed or locked file.
                    }

                    if (!string.IsNullOrWhiteSpace(resourceKey)
                        && currentConfigs.TryGetValue(resourceKey, out TarChannelConfig? config))
                    {
                        return config;
                    }

                    if (!string.IsNullOrWhiteSpace(talkgroupId)
                        && currentConfigs.TryGetValue(talkgroupId, out config))
                    {
                        return config;
                    }

                    if (!string.IsNullOrWhiteSpace(channelName)
                        && currentConfigs.TryGetValue(channelName, out config))
                    {
                        return config;
                    }

                    return new TarChannelConfig();
                });
        }

        /// <summary>
        /// Adds the native "About" menu item that opens the About
        /// dialog on the main window. The item is inserted into the
        /// first top-level submenu (the App menu) ahead of its
        /// trailing items; when the menu tree does not match that
        /// shape the item is appended to the top-level menu. Failures
        /// degrade to no menu item, never to a startup crash.
        /// </summary>
        internal static void AddAboutMenuItem(MainWindow mainWindow)
        {
            try
            {
                NativeMenu? menu = NativeMenu.GetMenu(mainWindow);
                if (menu is null)
                {
                    return;
                }

                // Wire Help first. The native macOS exporter can publish the
                // menu before later shell-menu composition finishes; Help is
                // the diagnostic escape hatch and must not depend on any
                // unrelated menu mutation succeeding.
                WireHelpMenu(menu, mainWindow);

                var aboutItem = CreateAboutMenuItem(mainWindow);

                NativeMenuItem tarItem = CreateTarConfigurationMenuItem(mainWindow);
                NativeMenuItem tarViewerItem = CreateTarViewerMenuItem(mainWindow);
                NativeMenuItem groupsItem = CreatePatchGroupsMenuItem(mainWindow);
                NativeMenuItem tonesItem = CreateAlertToneManagerMenuItem(mainWindow);
                NativeMenuItem shellControlsItem = CreateShellControlsMenuItem(mainWindow);
                NativeMenuItem subscriberCommandsItem = CreateSubscriberCommandsMenuItem(mainWindow);
                NativeMenuItem quickCallItem = CreateQuickCallMenuItem(mainWindow);

                NativeMenuItem? fileItem = menu.Items
                    .OfType<NativeMenuItem>()
                    .FirstOrDefault(item => item.Header is string header
                        && string.Equals(header, "File", StringComparison.Ordinal));
                if (fileItem?.Menu is { } fileMenu)
                {
                    NativeMenuItem? openItem = fileMenu.Items
                        .OfType<NativeMenuItem>()
                        .FirstOrDefault(item => item.Header is string header
                            && string.Equals(header, "Open Codeplug", StringComparison.Ordinal));
                    if (openItem is not null)
                    {
                        openItem.Click += (_, _) => _ = mainWindow.OpenCodeplugAsync();
                    }
                }

                NativeMenuItem? appItem = menu.Items
                    .OfType<NativeMenuItem>()
                    .FirstOrDefault(item => item.Header is string header
                        && string.Equals(header, "App", StringComparison.Ordinal));
                NativeMenuItem? settingsItem = menu.Items
                    .OfType<NativeMenuItem>()
                    .FirstOrDefault(item => item.Header is string header
                        && string.Equals(header, "Settings", StringComparison.Ordinal));
                if (settingsItem?.Menu is { } settingsMenu)
                {
                    settingsMenu.Items.Insert(0, groupsItem);
                    settingsMenu.Items.Insert(1, tonesItem);
                    settingsMenu.Items.Insert(2, CreateSettingsTransferMenuItem(mainWindow));
                    settingsMenu.Items.Insert(3, shellControlsItem);
                }

                NativeMenuItem? commandsItem = menu.Items
                    .OfType<NativeMenuItem>()
                    .FirstOrDefault(item => item.Header is string header
                        && string.Equals(header, "Commands", StringComparison.Ordinal));
                if (commandsItem is not null)
                {
                    commandsItem.Menu = subscriberCommandsItem.Menu;
                    commandsItem.IsEnabled = subscriberCommandsItem.IsEnabled;
                    BindSubscriberCommandEnablement(commandsItem, mainWindow);
                }
                else
                {
                    menu.Items.Add(subscriberCommandsItem);
                    BindSubscriberCommandEnablement(subscriberCommandsItem, mainWindow);
                }

                NativeMenuItem commandsHost = commandsItem ?? subscriberCommandsItem;
                if (commandsHost.Menu is { } commandsMenu
                    && !commandsMenu.Items.OfType<NativeMenuItem>().Any(item =>
                        item.Header is string header
                        && string.Equals(header, "Quick Call II", StringComparison.Ordinal)))
                {
                    commandsMenu.Items.Add(quickCallItem);
                }

                if (appItem?.Menu is { } appMenu && appMenu.Items.Count > 0)
                {
                    // Insert ahead of the trailing item (Quit): About,
                    // TAR Configuration, TAR Viewer, then Quit.
                    appMenu.Items.Insert(appMenu.Items.Count - 1, aboutItem);
                    appMenu.Items.Insert(appMenu.Items.Count - 1, tarItem);
                    appMenu.Items.Insert(appMenu.Items.Count - 1, tarViewerItem);

                    if (appMenu.Items
                        .OfType<NativeMenuItem>()
                        .FirstOrDefault(item => item.Header is string header
                            && string.Equals(header, "Quit", StringComparison.Ordinal)) is { } quitItem)
                    {
                        quitItem.Click += (_, _) =>
                            (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
                    }
                }
                else
                {
                    menu.Items.Add(aboutItem);
                    menu.Items.Add(tarItem);
                    menu.Items.Add(tarViewerItem);
                }
            }
            catch
            {
                // A menu-structure surprise must never fail startup.
            }
        }

        /// <summary>
        /// Creates the native "TAR Configuration" menu item that opens the
        /// TAR configuration dialog on the main window. The click handler
        /// is null-safe: with a null main window it is an inert no-op and
        /// never throws. When a main window is supplied, the item is
        /// enabled only while the window's data context is a
        /// <see cref="MainWindowViewModel"/> whose TAR configuration slice
        /// is composed (a loaded codeplug and TAR settings persistence); a
        /// missing codeplug or TAR composition yields a disabled item whose
        /// click stays inert.
        /// </summary>
        internal static NativeMenuItem CreateTarConfigurationMenuItem(MainWindow? mainWindow)
        {
            var item = new NativeMenuItem("TAR Configuration");

            if (mainWindow is not null)
            {
                item.IsEnabled = mainWindow.DataContext is MainWindowViewModel viewModel
                    && viewModel.TarConfiguration is not null;
            }

            item.Click += (_, _) => mainWindow?.OpenTarConfiguration();
            return item;
        }

        /// <summary>
        /// Creates the native TAR Viewer menu item. It remains available on
        /// hosts where the required audio capability is absent so the main
        /// dashboard can show the precise missing dependency instead of
        /// silently hiding a planned capability.
        /// </summary>
        internal static NativeMenuItem CreateTarViewerMenuItem(MainWindow? mainWindow)
        {
            var item = new NativeMenuItem("TAR Viewer");
            item.Click += (_, _) => mainWindow?.OpenTarViewer();
            return item;
        }

        /// <summary>
        /// Creates the native Groups menu item. It is inserted into the
        /// Settings submenu after the main window exists because Avalonia
        /// native menu events are not XAML-bindable on this target.
        /// </summary>
        internal static NativeMenuItem CreatePatchGroupsMenuItem(MainWindow? mainWindow)
        {
            var item = new NativeMenuItem("Patch Groups");
            item.Click += (_, _) => mainWindow?.OpenPatchGroups();
            return item;
        }

        /// <summary>
        /// Creates the native Tones submenu. Native menu click events are
        /// composed here because they are not XAML-bindable on this target.
        /// </summary>
        internal static NativeMenuItem CreateAlertToneManagerMenuItem(MainWindow? mainWindow)
        {
            var tones = new NativeMenuItem("Tones")
            {
                Menu = new NativeMenu(),
            };
            var manage = new NativeMenuItem("Manage Custom Alert Tones")
            {
                IsEnabled = mainWindow is not null,
            };
            manage.Click += (_, _) => mainWindow?.OpenAlertToneManager();
            tones.Menu.Items.Add(manage);
            var presets = new NativeMenuItem("Manage Tone Presets")
            {
                IsEnabled = mainWindow is not null,
            };
            presets.Click += (_, _) => mainWindow?.OpenTonePresetManager();
            tones.Menu.Items.Add(presets);
            var dtmfPresets = new NativeMenuItem("Manage DTMF Presets")
            {
                IsEnabled = mainWindow is not null,
            };
            dtmfPresets.Click += (_, _) => mainWindow?.OpenDtmfPresetManager();
            tones.Menu.Items.Add(dtmfPresets);
            return tones;
        }

        /// <summary>
        /// Creates the native settings-transfer menu item. The settings
        /// dialog is owned by the main window and remains inert when no
        /// dashboard is available.
        /// </summary>
        internal static NativeMenuItem CreateSettingsTransferMenuItem(MainWindow? mainWindow)
        {
            var item = new NativeMenuItem("Import / Export Settings")
            {
                IsEnabled = mainWindow is not null,
            };
            item.Click += (_, _) => mainWindow?.OpenSettingsTransfer();
            return item;
        }

        /// <summary>
        /// Creates the native subscriber-command submenu. The four entries
        /// mirror the WPF Page, Radio Check, Inhibit, and Uninhibit actions;
        /// they stay disabled until a live fnecore-backed command service and
        /// at least one configured codeplug system are composed.
        /// </summary>
        internal static NativeMenuItem CreateSubscriberCommandsMenuItem(MainWindow? mainWindow)
        {
            bool enabled = mainWindow?.CanOpenSubscriberCommands == true;
            var item = new NativeMenuItem("Commands")
            {
                IsEnabled = enabled,
                Menu = new NativeMenu(),
            };

            AddSubscriberCommandAction(
                item.Menu,
                "Page Subscriber",
                mainWindow,
                SubscriberCommandKind.Page,
                enabled);
            AddSubscriberCommandAction(
                item.Menu,
                "Radio Check Subscriber",
                mainWindow,
                SubscriberCommandKind.RadioCheck,
                enabled);
            AddSubscriberCommandAction(
                item.Menu,
                "Inhibit Subscriber",
                mainWindow,
                SubscriberCommandKind.Inhibit,
                enabled);
            AddSubscriberCommandAction(
                item.Menu,
                "Uninhibit Subscriber",
                mainWindow,
                SubscriberCommandKind.Uninhibit,
                enabled);

            return item;
        }

        /// <summary>
        /// Creates the manual QuickCall II action. Target selection happens
        /// at send time from the page-state snapshot, not when the menu opens.
        /// </summary>
        internal static NativeMenuItem CreateQuickCallMenuItem(MainWindow? mainWindow)
        {
            var item = new NativeMenuItem("Quick Call II")
            {
                IsEnabled = mainWindow?.CanOpenQuickCall == true,
            };
            item.Click += (_, _) => mainWindow?.OpenManualQuickCall();
            return item;
        }

        private static void BindSubscriberCommandEnablement(
            NativeMenuItem item,
            MainWindow? mainWindow)
        {
            if (mainWindow?.DataContext is not MainWindowViewModel viewModel)
                return;

            viewModel.FneConnections.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is not nameof(FneConnectionManagerViewModel.AnyConnected))
                    return;

                bool currentEnabled = mainWindow.CanOpenSubscriberCommands;
                item.IsEnabled = currentEnabled;
                if (item.Menu is not { } menu)
                    return;

                foreach (NativeMenuItem child in menu.Items.OfType<NativeMenuItem>())
                    child.IsEnabled = currentEnabled;
            };
        }

        private static void AddSubscriberCommandAction(
            NativeMenu menu,
            string header,
            MainWindow? mainWindow,
            SubscriberCommandKind commandKind,
            bool enabled)
        {
            var item = new NativeMenuItem(header)
            {
                IsEnabled = enabled,
            };
            if (enabled)
                item.Click += (_, _) => mainWindow!.OpenSubscriberCommand(commandKind);
            menu.Items.Add(item);
        }

        /// <summary>
        /// Creates the native submenu for the remaining WPF shell actions.
        /// FNE and call history already live in the dashboard; their entries
        /// focus those existing surfaces instead of creating duplicate
        /// view-models or windows.
        /// </summary>
        internal static NativeMenuItem CreateShellControlsMenuItem(MainWindow? mainWindow)
        {
            var item = new NativeMenuItem("Shell Controls")
            {
                IsEnabled = mainWindow is not null,
                Menu = new NativeMenu(),
            };

            AddShellAction(item.Menu, "Select/Clear All Current Zone", mainWindow is null ? null : (Action)mainWindow.ToggleSelectAllCurrentZone);
            AddShellAction(item.Menu, "Call History", mainWindow is null ? null : (Action)mainWindow.OpenCallHistory);
            AddShellAction(item.Menu, "Select Widgets to Display", mainWindow is null ? null : (Action)mainWindow.OpenWidgetSelection);
            AddShellAction(item.Menu, "Select User Background", mainWindow is null ? null : (Action)mainWindow.OpenUserBackgroundAsync);
            AddShellAction(item.Menu, "Reset Settings", mainWindow is null ? null : (Action)mainWindow.ResetSettings);
            AddShellAction(item.Menu, "Reset Tab Layout", mainWindow is null ? null : (Action)mainWindow.ResetLayout);
            AddShellAction(item.Menu, "Fit Channel Display to Window Size", mainWindow is null ? null : (Action)mainWindow.FitLayoutToWindow);
            AddShellAction(item.Menu, "Lock Widgets", mainWindow is null ? null : (Action)mainWindow.SetWidgetLayoutLocked);
            AddShellAction(item.Menu, "Always on Top", mainWindow is null ? null : (Action)mainWindow.ToggleKeepWindowOnTop);
            AddShellAction(item.Menu, "FNE Connection Manager", mainWindow is null ? null : (Action)mainWindow.OpenFneConnectionManager);
            return item;
        }

        private static void AddShellAction(NativeMenu menu, string header, Action? action)
        {
            var item = new NativeMenuItem(header)
            {
                IsEnabled = action is not null,
            };
            if (action is not null)
            {
                item.Click += (_, _) => action();
            }
            menu.Items.Add(item);
        }

        /// <summary>
        /// Creates the native Help → Debug Logs entry. Native menu events are
        /// composed here because they are not XAML-bindable on this target.
        /// </summary>
        internal static NativeMenuItem CreateDebugLogMenuItem(MainWindow? mainWindow)
        {
            var item = new NativeMenuItem("Debug Logs")
            {
                IsEnabled = mainWindow is not null,
            };
            item.Click += (_, _) => mainWindow?.OpenDebugLog();
            return item;
        }

        private static void WireHelpMenu(NativeMenu menu, MainWindow mainWindow)
        {
            NativeMenuItem? helpItem = menu.Items
                .OfType<NativeMenuItem>()
                .FirstOrDefault(item => item.Header is string header
                    && string.Equals(header, "Help", StringComparison.Ordinal));
            if (helpItem is null)
            {
                helpItem = new NativeMenuItem("Help")
                {
                    Menu = new NativeMenu(),
                };
                menu.Items.Add(helpItem);
            }

            if (helpItem.Menu is not { } helpMenu)
                return;

            NativeMenuItem? existingDebugLog = helpMenu.Items
                .OfType<NativeMenuItem>()
                .FirstOrDefault(item => item.Header is string header
                    && string.Equals(header, "Debug Logs", StringComparison.Ordinal));
            if (existingDebugLog is null)
            {
                helpMenu.Items.Add(CreateDebugLogMenuItem(mainWindow));
            }
            else
            {
                existingDebugLog.IsEnabled = true;
                existingDebugLog.Click += (_, _) => mainWindow.OpenDebugLog();
            }

            NativeMenuItem? existingDocumentation = helpMenu.Items
                .OfType<NativeMenuItem>()
                .FirstOrDefault(item => item.Header is string header
                    && string.Equals(header, "Documentation", StringComparison.Ordinal));
            if (existingDocumentation is null)
            {
                helpMenu.Items.Add(CreateDocumentationMenuItem());
            }
            else
            {
                existingDocumentation.Click += (_, _) =>
                    OpenExternalUrl(AboutWindowViewModel.DocumentationLink);
            }

            NativeMenuItem? existingAbout = helpMenu.Items
                .OfType<NativeMenuItem>()
                .FirstOrDefault(item => item.Header is string header
                    && string.Equals(header, "About", StringComparison.Ordinal));
            if (existingAbout is null)
            {
                helpMenu.Items.Add(CreateAboutMenuItem(mainWindow));
            }
            else
            {
                WireAboutMenuItem(existingAbout, mainWindow);
            }
        }

        private static NativeMenuItem CreateAboutMenuItem(MainWindow mainWindow)
        {
            var item = new NativeMenuItem("About");
            WireAboutMenuItem(item, mainWindow);
            return item;
        }

        private static void WireAboutMenuItem(NativeMenuItem item, MainWindow mainWindow)
        {
            item.Click += (_, _) =>
            {
                string? nativeReadiness = (mainWindow.DataContext as MainWindowViewModel)?.VocoderStatus;
                var about = new AboutWindow(nativeReadiness);
                about.ShowDialog(mainWindow);
            };
        }

        /// <summary>
        /// Creates the native Help → Documentation entry. Packaged builds use
        /// the host browser rather than assuming the repository checkout is
        /// present beside the application bundle.
        /// </summary>
        internal static NativeMenuItem CreateDocumentationMenuItem()
        {
            var item = new NativeMenuItem("Documentation");
            item.Click += (_, _) => OpenExternalUrl(AboutWindowViewModel.DocumentationLink);
            return item;
        }

        private static void OpenExternalUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true,
                });
            }
            catch
            {
                // A missing host browser must not crash the application.
            }
        }

        private static IEnumerable<string> GetDiagnosticSensitiveValues(Codeplug? codeplug)
        {
            foreach (Codeplug.System system in codeplug?.Systems ?? new List<Codeplug.System>())
            {
                yield return system.Password;
                yield return system.PresharedKey;
            }

            foreach (Codeplug.Zone zone in codeplug?.Zones ?? new List<Codeplug.Zone>())
            {
                foreach (Codeplug.WebStream stream in zone.WebStreams ?? new List<Codeplug.WebStream>())
                {
                    yield return stream.AuthPassword;
                }
            }
        }

        private static DiagnosticLogSink CreateDiagnosticLogSink(
            Codeplug? codeplug,
            LogBuffer? buffer = null,
            string? filePath = null)
        {
            return new DiagnosticLogSink(
                buffer ?? new LogBuffer(),
                GetDiagnosticSensitiveValues(codeplug),
                filePath);
        }

        private static MainWindow CreateComposedMainWindow(
            IClassicDesktopStyleApplicationLifetime desktop,
            Codeplug? codeplug,
            string codeplugPath,
            bool deferRuntimeActivation = false,
            DiagnosticLogSink? diagnosticSink = null)
        {
            diagnosticSink ??= CreateDiagnosticLogSink(codeplug);
            diagnosticSink.ReplaceSensitiveValues(GetDiagnosticSensitiveValues(codeplug));
            var catalog = CreateAudioDeviceCatalog();
            var streams = catalog is MacAudioDeviceCatalog macCatalog
                ? new MacAudioStreamFactory(macCatalog)
                : null;
            var webStreamSourceFactory = new WebStreamSourceFactory();
            var hotkeys = CreateGlobalHotkeyService();
            // One shared settings store backs all section adapters. Each
            // adapter merges only its own DTO section, preserving unrelated
            // settings during both startup and codeplug replacement.
            var fileSystemPaths = new DefaultFileSystemPaths();
            var settingsStore = new SettingsSectionStore(fileSystemPaths.SettingsFilePath);
            var persistence = new AudioSettingsPersistence(settingsStore);
            var tarPersistence = new TarSettingsPersistence(settingsStore);
            var pttPersistence = new PttSettingsPersistence(settingsStore);
            var preferencesPersistence = new PreferencesSettingsPersistence(settingsStore);
            var restorePersistence = new RestoreSettingsPersistence(settingsStore);
            var layoutPersistence = new LayoutSettingsPersistence(settingsStore);
            var groupsPersistence = new GroupSettingsPersistence(settingsStore);
            var alertPersistence = new AlertSettingsPersistence(settingsStore);
            var settingsTransferService =
                new SettingsTransferService(fileSystemPaths.SettingsFilePath);
            var tarRecorder = CreateTarRecorder(tarPersistence, fileSystemPaths.DefaultTarRecordingsPath);
            var tarViewerColumnPersistence =
                new TarViewerColumnSettingsPersistence(settingsStore);
            var alertWaveFileInspector = new WaveFileInspector();
            IAudioWaveFilePlayer? tarWaveFilePlayer = null;
            IAudioWaveFilePlayer? alertWaveFilePlayer = null;
            try
            {
                tarWaveFilePlayer = streams?.CreateWaveFilePlayer();
                alertWaveFilePlayer = streams?.CreateWaveFilePlayer();
            }
            catch (AudioDeviceException)
            {
                // The dashboard reports unavailable audio capabilities.
            }
            catch (PlatformNotSupportedException)
            {
                // The dashboard reports unavailable audio capabilities.
            }

            MacBundleLibraryResolver.Register(typeof(NativeLibraryProbe).Assembly);
            var vocoderStatus = CheckVocoderReadiness(diagnosticSink);
            var callHistory = codeplug is null
                ? null
                : CreateCallHistoryStore(codeplug);
            var aliasResolver = BuildAliasResolver(codeplug);
            LibVocoderVoiceCodec? voiceCodec = vocoderStatus.IsReady
                ? new LibVocoderVoiceCodec(new LibVocoderNative())
                : null;
            var fnecoreTransportFactory = new FnecoreTransportFactory();
            var fneLoggingOptions = FneLoggingOptions.FromEnvironment();
            fnecoreTransportFactory.FneLogLevel = fneLoggingOptions.LogLevel;
            fnecoreTransportFactory.FneRawPacketTrace = fneLoggingOptions.RawPacketTrace;
            fnecoreTransportFactory.FneTrafficLogging = fneLoggingOptions.TrafficLogging;
            fnecoreTransportFactory.DiagnosticWriter = diagnosticSink.Write;
            diagnosticSink.WriteApplication(
                fnecore.LogLevel.INFO,
                $"FNE diagnostics configured: level={fneLoggingOptions.LogLevel}, "
                    + $"rawPacketTrace={fneLoggingOptions.RawPacketTrace}, "
                    + $"trafficLogging={fneLoggingOptions.TrafficLogging}");

            var mainWindow = new MainWindow(
                catalog,
                hotkeys,
                CreateKeyStateReader(),
                persistence,
                vocoderStatus,
                streams,
                codeplug?.Systems,
                (IVoiceFrameDecoder?)voiceCodec ?? new NullVoiceFrameDecoder(),
                (IVoiceFrameEncoder?)voiceCodec ?? new NullVoiceFrameEncoder(),
                new FnecoreVoiceTrafficSender(fnecoreTransportFactory.ResolveAdapter),
                fnecoreTransportFactory,
                codeplug,
                callHistory,
                aliasResolver,
                tarPersistence,
                pttPersistence,
                tarRecorder,
                tarWaveFilePlayer,
                tarViewerColumnPersistence,
                deferRuntimeActivation,
                ownsRuntimeServices: true);
            mainWindow.AttachWebStreamSourceFactory(webStreamSourceFactory);
            mainWindow.AttachPreferencesPersistence(preferencesPersistence);
            mainWindow.AttachGroupsPersistence(groupsPersistence);
            mainWindow.AttachRestorePersistence(restorePersistence);
            mainWindow.AttachLayoutPersistence(layoutPersistence);
            mainWindow.AttachWebStreamPersistence(restorePersistence, layoutPersistence);
            mainWindow.AttachAlertSettingsPersistence(alertPersistence);
            mainWindow.AttachAlertTonePreview(alertWaveFileInspector, alertWaveFilePlayer);
            mainWindow.AttachSettingsTransfer(settingsTransferService);
            mainWindow.AttachDiagnosticLogSink(diagnosticSink);
            mainWindow.FileDialogService =
                new AvaloniaFileDialogService(mainWindow.StorageProvider);
            mainWindow.TarFileRevealService = new DesktopFileRevealService();
            mainWindow.TarConfirmationService = new AvaloniaConfirmationService();
            mainWindow.ConfigureCodeplugReload(
                (nextCodeplug, nextPath) =>
                    CreateComposedMainWindow(
                        desktop,
                        nextCodeplug,
                        nextPath,
                        deferRuntimeActivation: true,
                        diagnosticSink),
                async (current, candidate) =>
                {
                    await current.DisposeRuntimeAsync().ConfigureAwait(false);
                    candidate.ActivateRuntime();
                    candidate.Show();
                    desktop.MainWindow = candidate;
                    current.Close();
                },
                codeplugPath);
            AddAboutMenuItem(mainWindow);
            return mainWindow;
        }

        private static string ResolveDefaultCodeplugPath(DefaultFileSystemPaths paths)
        {
            string platformPath = Path.Combine(
                paths.ApplicationDataRootPath,
                "codeplug.yml");
            string checkoutPath = Path.Combine(
                Environment.CurrentDirectory,
                "configs",
                "codeplug.yml");
            return File.Exists(platformPath) || !File.Exists(checkoutPath)
                ? platformPath
                : checkoutPath;
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var defaultCodeplugPath = ResolveDefaultCodeplugPath(new DefaultFileSystemPaths());
                var diagnosticBuffer = new LogBuffer();
                var fileSystemPaths = new DefaultFileSystemPaths();
                var diagnosticLogPath = Path.Combine(
                    fileSystemPaths.TraceLogDirectoryPath,
                    "DvmConsole.log");
                var diagnosticSink = CreateDiagnosticLogSink(
                    null,
                    diagnosticBuffer,
                    diagnosticLogPath);
                applicationDiagnostics = new ApplicationDiagnostics(diagnosticSink);
                applicationDiagnostics.Install();
                diagnosticSink.WriteApplication(
                    fnecore.LogLevel.INFO,
                    $"DvmConsole startup; diagnostics file={diagnosticLogPath}");
                var loadResult = CodeplugLoader.LoadFromFile(defaultCodeplugPath);
                diagnosticSink.AddSensitiveValues(GetDiagnosticSensitiveValues(loadResult.Codeplug));
                if (!loadResult.Succeeded)
                {
                    string diagnostic = "codeplug unavailable: "
                        + (loadResult.ErrorMessage ?? "load failed");
                    diagnosticSink.WriteApplication(fnecore.LogLevel.ERROR, diagnostic);
                    System.Console.WriteLine(diagnostic);
                    System.Console.Out.Flush();
                }

                var mainWindow = CreateComposedMainWindow(
                    desktop,
                    loadResult.Codeplug,
                    defaultCodeplugPath,
                    diagnosticSink: diagnosticSink);
                desktop.MainWindow = mainWindow;
                mainWindow.Show();
                desktop.Exit += (_, _) =>
                {
                    applicationDiagnostics?.Dispose();
                    applicationDiagnostics = null;
                    diagnosticSink.Dispose();
                };

                // Native "About" menu item: opens the About dialog on
                // the main window. Kept in the shell so the About slice
                // stays self-contained; an unexpected menu structure
                // degrades to no item rather than failing startup.
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
