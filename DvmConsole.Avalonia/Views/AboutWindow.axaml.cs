// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DvmConsole.Avalonia.ViewModels;

namespace DvmConsole.Avalonia.Views
{
    /// <summary>
    /// About dialog. The code-behind builds the view model from the
    /// executing assembly's version metadata, mirroring the WPF
    /// AboutWindow.LoadVersionInfo: the assembly version feeds the
    /// RxxAyy release and the AssemblyInformationalVersion attribute
    /// feeds the short commit hash.
    /// </summary>
    public partial class AboutWindow : Window
    {
        public AboutWindow()
            : this(null)
        {
        }

        public AboutWindow(string? nativeReadiness)
        {
            InitializeComponent();
            DataContext = CreateViewModel(nativeReadiness);
        }

        /// <summary>
        /// Builds the about view model from the executing assembly
        /// (WPF AboutWindow.LoadVersionInfo parity).
        /// </summary>
        private static AboutWindowViewModel CreateViewModel(string? nativeReadiness)
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            return new AboutWindowViewModel(
                "Digital Voice Modem",
                "Desktop Dispatch Console",
                assembly.GetName().Version,
                assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                nativeReadiness);
        }

        private void License_OnClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is AboutWindowViewModel viewModel)
            {
                OpenUrl(viewModel.LicenseUrl);
            }
        }

        private void Repository_OnClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is AboutWindowViewModel viewModel)
            {
                OpenUrl(viewModel.RepositoryUrl);
            }
        }

        private void Documentation_OnClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is AboutWindowViewModel viewModel)
            {
                OpenUrl(viewModel.DocumentationUrl);
            }
        }

        private void Close_OnClick(object? sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// Opens the URL in the platform browser (WPF parity); best
        /// effort only, an unavailable browser must not break the
        /// dialog.
        /// </summary>
        private static void OpenUrl(string url)
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
                // Best effort: no browser available is not a dialog failure.
            }
        }
    }
}
