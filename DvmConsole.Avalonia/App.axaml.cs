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
using DvmConsole.Platform;
using DvmConsole.Platform.Audio;
using DvmConsole.Platform.Audio.Mac;
using DvmConsole.Platform.Audio.Vocoder;
using DvmConsole.Platform.Hotkeys;
using DvmConsole.Platform.Hotkeys.Mac;
using DvmConsole.Platform.Native;
using System.Collections.Generic;
using System.IO;

namespace DvmConsole.Avalonia
{
    public partial class App : Application
    {
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
        private static IAudioDeviceCatalog? CreateAudioDeviceCatalog()
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
        private static IGlobalHotkeyService CreateGlobalHotkeyService()
            => PlatformInfo.IsMacOS
                ? new MacGlobalHotkeyService(new CoreGraphicsEventTap(), new MacPermissionProbe())
                : new UnavailableGlobalHotkeyService();

        /// <summary>
        /// Creates the macOS CGEventSourceKeyState-backed key-state
        /// reader used by the PTT hotkey key-up watchdog, or null on
        /// every other host where the watchdog stays dormant. The
        /// application owns the reader for its whole lifetime.
        /// </summary>
        private static IKeyboardKeyStateReader? CreateKeyStateReader()
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
        private static VocoderReadinessResult CheckVocoderReadiness()
        {
            var result = new VocoderReadiness(new NativeLibraryProbe()).Check();

            if (result.IsReady)
            {
                System.Console.WriteLine("libvocoder ready");
            }
            else if (result.Diagnostic is { } diagnostic)
            {
                System.Diagnostics.Debug.WriteLine(diagnostic);
                System.Console.WriteLine(diagnostic);
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
        private static void AddAboutMenuItem(MainWindow mainWindow)
        {
            try
            {
                NativeMenu? menu = NativeMenu.GetMenu(mainWindow);
                if (menu is null)
                {
                    return;
                }

                var aboutItem = new NativeMenuItem("About");
                aboutItem.Click += (_, _) =>
                {
                    var about = new AboutWindow();
                    about.ShowDialog(mainWindow);
                };

                NativeMenuItem tarItem = CreateTarConfigurationMenuItem(mainWindow);
                NativeMenuItem tarViewerItem = CreateTarViewerMenuItem(mainWindow);
                NativeMenuItem groupsItem = CreatePatchGroupsMenuItem(mainWindow);
                NativeMenuItem tonesItem = CreateAlertToneManagerMenuItem(mainWindow);

                NativeMenuItem? appItem = menu.Items
                    .OfType<NativeMenuItem>()
                    .FirstOrDefault(item => item.Menu is { });
                NativeMenuItem? settingsItem = menu.Items
                    .OfType<NativeMenuItem>()
                    .FirstOrDefault(item => item.Header is string header
                        && string.Equals(header, "Settings", StringComparison.Ordinal));
                if (settingsItem?.Menu is { } settingsMenu)
                {
                    settingsMenu.Items.Insert(0, groupsItem);
                    settingsMenu.Items.Insert(1, tonesItem);
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
            return tones;
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var catalog = CreateAudioDeviceCatalog();
                // The catalog is not owned by the factory (MacAudioStreamFactory
                // catalog-constructor does not dispose it); the MainWindow owns
                // the factory and disposes the audio pipelines on close.
                var streams = catalog is MacAudioDeviceCatalog macCatalog
                    ? new MacAudioStreamFactory(macCatalog)
                    : null;
                var hotkeys = CreateGlobalHotkeyService();
                // One shared settings store backs both persistence
                // adapters: the Core store saves only the section it is
                // asked to update and preserves every unrelated property
                // value-for-value, so the audio and TAR settings coexist
                // in the same settings file without clobbering each other.
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
                    // The viewer reports the missing capability in the
                    // dashboard; startup remains usable on this host.
                }
                catch (PlatformNotSupportedException)
                {
                    // Same visible degradation for an unavailable host API.
                }
                // Packaged macOS .app: register the bundle resolver for
                // future DllImport-based libvocoder loads in the Platform
                // assembly. The startup readiness probe maps and loads the
                // bundle candidate explicitly, since its assembly-aware
                // TryLoad path does not invoke the DllImportResolver.
                // No-op on every other host and in un-packaged runs.
                MacBundleLibraryResolver.Register(typeof(NativeLibraryProbe).Assembly);
                var vocoderStatus = CheckVocoderReadiness();

                // Load the codeplug from the repo-convention path and
                // seed the FNE slice with its real systems. A missing or
                // unreadable codeplug degrades to no systems (the FNE
                // manager and service stay empty) rather than failing
                // startup; the diagnostic mirrors the vocoder-readiness
                // stdout pattern so headless logs observe it. The load
                // itself never throws.
                var codeplug = CodeplugLoader.LoadFromFile(
                    Path.Combine(System.Environment.CurrentDirectory, "configs", "codeplug.yml"));
                if (!codeplug.Succeeded)
                {
                    System.Console.WriteLine(
                        "codeplug unavailable: " + (codeplug.ErrorMessage ?? "load failed"));
                    System.Console.Out.Flush();
                }

                // Compose the call-history store only when the codeplug
                // loaded: the store records received calls, resolving
                // their targets to codeplug channel names. A null
                // codeplug keeps the slice dormant (a null store, and
                // the panel shows its muted "not attached" state).
                var callHistory = codeplug.Codeplug is null
                    ? null
                    : CreateCallHistoryStore(codeplug.Codeplug);

                // Load per-system alias files (WPF-parity) and compose
                // the alias resolver over them so call-history entries
                // carry the subscriber alias for their (system, source
                // id). A null codeplug yields a null resolver — no
                // aliases anywhere; inline aliases survive a missing or
                // unreadable external alias file. Never throws.
                var aliasResolver = BuildAliasResolver(codeplug.Codeplug);

                // Compose the real dual-mode libvocoder adapter when the
                // startup readiness probe found the library; otherwise
                // keep the null codec pair so the router stays fully
                // wired while decoding/encoding is inert. The fnecore
                // traffic sender resolves each transmit target's live
                // adapter through the transport factory shared with the
                // FNE slice, so PTT traffic flows once the system's
                // connection is started.
                LibVocoderVoiceCodec? voiceCodec = vocoderStatus.IsReady
                    ? new LibVocoderVoiceCodec(new LibVocoderNative())
                    : null;

                // The transport factory is shared by the FNE slice and
                // the voice traffic sender: the connection service
                // creates adapters through it (registered per system
                // name) and the sender resolves them at transmit time.
                var fnecoreTransportFactory = new FnecoreTransportFactory();

                var mainWindow = new MainWindow(
                    catalog,
                    hotkeys,
                    CreateKeyStateReader(),
                    persistence,
                    vocoderStatus,
                    streams,
                    codeplug.Codeplug?.Systems,
                    (IVoiceFrameDecoder?)voiceCodec ?? new NullVoiceFrameDecoder(),
                    (IVoiceFrameEncoder?)voiceCodec ?? new NullVoiceFrameEncoder(),
                    new FnecoreVoiceTrafficSender(fnecoreTransportFactory.ResolveAdapter),
                    fnecoreTransportFactory,
                    codeplug.Codeplug,
                    callHistory,
                    aliasResolver,
                    tarPersistence,
                    pttPersistence,
                    tarRecorder,
                    tarWaveFilePlayer,
                    tarViewerColumnPersistence);
                mainWindow.AttachPreferencesPersistence(preferencesPersistence);
                mainWindow.AttachGroupsPersistence(groupsPersistence);
                mainWindow.AttachRestorePersistence(restorePersistence);
                mainWindow.AttachLayoutPersistence(layoutPersistence);
                mainWindow.AttachAlertSettingsPersistence(alertPersistence);
                mainWindow.AttachAlertTonePreview(alertWaveFileInspector, alertWaveFilePlayer);
                mainWindow.FileDialogService =
                    new AvaloniaFileDialogService(mainWindow.StorageProvider);
                mainWindow.TarFileRevealService = new DesktopFileRevealService();
                mainWindow.TarConfirmationService = new AvaloniaConfirmationService();
                desktop.MainWindow = mainWindow;

                // Native "About" menu item: opens the About dialog on
                // the main window. Kept in the shell so the About slice
                // stays self-contained; an unexpected menu structure
                // degrades to no item rather than failing startup.
                AddAboutMenuItem(mainWindow);

                if (catalog is MacAudioDeviceCatalog mac)
                {
                    // The catalog raises DevicesChanged from a CoreAudio
                    // callback thread; marshal the refresh to the UI
                    // thread before touching the view-model.
                    mac.DevicesChanged += (_, _) => Dispatcher.UIThread.Post(() =>
                        (mainWindow.DataContext as MainWindowViewModel)?.AudioSettings?.Refresh());
                }
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
