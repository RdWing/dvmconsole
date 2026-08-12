// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DvmConsole.Avalonia.ViewModels;

namespace DvmConsole.Avalonia.Views
{
    /// <summary>
    /// Thin owner-bound shell for DTMF preset editing. The view model owns
    /// managed rows and detached request payloads; persistence and runtime
    /// preview/send handling remain composed by MainWindow.
    /// </summary>
    internal partial class DtmfPresetManagerWindow : Window
    {
        private readonly DtmfPresetManagerViewModel viewModel;

        public DtmfPresetManagerWindow(DtmfPresetManagerViewModel viewModel)
        {
            this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            InitializeComponent();
            DataContext = viewModel;
            Closed += OnWindowClosed;
        }

        private void AddPreset_Click(object? sender, RoutedEventArgs e)
        {
            viewModel.AddPreset();
            SetStatus("New DTMF preset added.");
        }

        private void DeletePreset_Click(object? sender, RoutedEventArgs e)
        {
            viewModel.DeleteSelected();
            SetStatus("DTMF preset deleted.");
        }

        private void AddDigit_Click(object? sender, RoutedEventArgs e)
        {
            viewModel.AddDigit();
            SetStatus("Digit step added.");
        }

        private void AddHold_Click(object? sender, RoutedEventArgs e)
        {
            viewModel.AddHold();
            SetStatus("Hold step added.");
        }

        private void DeleteStep_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button button
                && button.DataContext is DtmfPresetManagerViewModel.DtmfPresetStepItem step)
            {
                viewModel.DeleteStep(step);
                SetStatus("Step deleted.");
            }
        }

        private void MoveUp_Click(object? sender, RoutedEventArgs e)
            => MoveStep(sender, -1);

        private void MoveDown_Click(object? sender, RoutedEventArgs e)
            => MoveStep(sender, 1);

        private void MoveStep(object? sender, int direction)
        {
            if (sender is Button button
                && button.DataContext is DtmfPresetManagerViewModel.DtmfPresetStepItem step)
            {
                viewModel.MoveStep(step, direction);
                SetStatus("Step order updated.");
            }
        }

        private void Preview_Click(object? sender, RoutedEventArgs e)
        {
            viewModel.Preview();
            SetStatus("DTMF preset preview requested.");
        }

        private void Send_Click(object? sender, RoutedEventArgs e)
        {
            viewModel.Send();
            SetStatus("DTMF preset send requested.");
        }

        private void Save_Click(object? sender, RoutedEventArgs e)
        {
            viewModel.Commit();
            SetStatus("DTMF preset changes saved.");
        }

        private void Close_Click(object? sender, RoutedEventArgs e)
            => Close();

        private void OnWindowClosed(object? sender, EventArgs e)
        {
            Closed -= OnWindowClosed;
        }

        private void SetStatus(string message)
        {
            if (StatusTextBlock is not null)
                StatusTextBlock.Text = message;
        }
    }
}
