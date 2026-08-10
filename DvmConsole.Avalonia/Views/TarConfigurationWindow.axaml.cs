// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Platform.Dialogs;

namespace DvmConsole.Avalonia.Views
{
    /// <summary>
    /// TAR configuration dialog (Avalonia). Presentation-only shell over the
    /// headless <see cref="TarConfigurationViewModel"/>: the view model owns
    /// the zone/channel projection, ignored-RID parsing, recording-root
    /// validation, and the validated save payload. This window owns only the
    /// folder-picker invocation and the Save/Close gestures and never touches
    /// <c>SettingsSectionStore</c>, <c>TarSettingsPersistence</c>, the
    /// dispatcher, or any WPF state.
    /// </summary>
    /// <remarks>
    /// Ownership and deferred shell wiring: the shell that constructs this
    /// window owns the <see cref="IFileDialogService"/> lifetime and must
    /// subscribe to <see cref="TarConfigurationViewModel.SaveRequested"/> (the
    /// headless VM raises it exactly once per successful save) to persist the
    /// payload through the TAR settings adapter. MainWindow menu composition,
    /// persistence subscription, and the TAR viewer remain later shell gates.
    /// The class is internal (matching <c>x:ClassModifier</c>) because the
    /// window is only ever created by the shell through the injected
    /// constructor; Avalonia's runtime XAML loader cannot instantiate it
    /// without a public parameterless constructor, and the public
    /// <c>TarConfigurationViewModel</c>-driven constructor is the pinned
    /// contract.
    /// </remarks>
    internal partial class TarConfigurationWindow : Window
    {
        private readonly IFileDialogService fileDialogService;

        /// <summary>
        /// Creates the TAR configuration dialog over the injected headless
        /// view model and file-dialog service.
        /// </summary>
        /// <param name="viewModel">Headless TAR configuration surface; becomes the window DataContext.</param>
        /// <param name="fileDialogService">Host file/folder picker used by the Browse action.</param>
        /// <exception cref="ArgumentNullException">Either argument is null.</exception>
        public TarConfigurationWindow(TarConfigurationViewModel viewModel, IFileDialogService fileDialogService)
        {
            if (viewModel == null)
                throw new ArgumentNullException(nameof(viewModel));

            this.fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));

            InitializeComponent();
            DataContext = viewModel;
        }

        /// <summary>
        /// Picks the TAR recording folder through the injected dialog service,
        /// seeding the picker with the current value. A dismissed dialog,
        /// blank selection, or cancellation leaves the folder unchanged; the
        /// view model's change-only setter clears prior status/error text.
        /// </summary>
        private async void Browse_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not TarConfigurationViewModel viewModel)
                return;

            try
            {
                FolderDialogResult result = await fileDialogService.PickFolderAsync(
                    new FolderPickerRequest("Select TAR recording folder", viewModel.RecordingFolderPath),
                    CancellationToken.None);

                if (!result.Cancelled && !string.IsNullOrWhiteSpace(result.Selected))
                    viewModel.RecordingFolderPath = result.Selected;
            }
            catch (OperationCanceledException)
            {
                // User or token cancellation of the picker; nothing to show or rethrow.
            }
        }

        /// <summary>
        /// Runs the view model's validated save. Validation, payload building,
        /// and the <see cref="TarConfigurationViewModel.SaveRequested"/> event
        /// are owned entirely by the headless VM; the window neither duplicates
        /// them nor closes itself on save.
        /// </summary>
        private void Save_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is TarConfigurationViewModel viewModel)
                viewModel.Save();
        }

        /// <summary>
        /// Closes the dialog without saving; unsaved edits are discarded.
        /// </summary>
        private void Close_Click(object? sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
