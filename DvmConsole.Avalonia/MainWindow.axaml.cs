// SPDX-License-Identifier: AGPL-3.0-only
using Avalonia.Controls;
using DvmConsole.Avalonia.Dialogs;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Platform.Dialogs;

namespace DvmConsole.Avalonia
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainWindowViewModel();
        }

        /// <summary>
        /// File and folder picker service used by shell features. Injected by
        /// <see cref="App"/> with the window's storage provider; the no-op
        /// fallback keeps the shell behaviorally unchanged until then.
        /// </summary>
        internal IFileDialogService FileDialogService { get; set; } = NoopFileDialogService.Instance;
    }
}
