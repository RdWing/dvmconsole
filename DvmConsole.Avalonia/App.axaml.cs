// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using dvmconsole;
using DvmConsole.Avalonia.Dialogs;
using DvmConsole.Avalonia.Persistence;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Platform;
using DvmConsole.Platform.Audio;
using DvmConsole.Platform.Audio.Mac;
using DvmConsole.Platform.Hotkeys;

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
        /// lifetime. Until an OS-specific event-tap or Win32 hotkey
        /// implementation is selected, the unavailable fallback is
        /// composed on every host — macOS included: every gesture
        /// reports unsupported and no hotkey event ever fires, so the
        /// PTT slice stays unconfigured and the window shows the
        /// capability placeholder. The application owns the service for
        /// its whole lifetime; disposal is handled by a later concrete
        /// factory/lifecycle slice.
        /// </summary>
        private static IGlobalHotkeyService CreateGlobalHotkeyService()
            => new UnavailableGlobalHotkeyService();

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var catalog = CreateAudioDeviceCatalog();
                var hotkeys = CreateGlobalHotkeyService();
                var persistence = new AudioSettingsPersistence(
                    new SettingsSectionStore(new DefaultFileSystemPaths().SettingsFilePath));
                var mainWindow = new MainWindow(catalog, hotkeys, null, persistence);
                mainWindow.FileDialogService =
                    new AvaloniaFileDialogService(mainWindow.StorageProvider);
                desktop.MainWindow = mainWindow;

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
