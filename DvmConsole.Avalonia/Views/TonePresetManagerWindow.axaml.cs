// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DvmConsole.Avalonia.ViewModels;

namespace DvmConsole.Avalonia.Views
{
    /// <summary>
    /// Thin owner-bound shell for generated tone preset editing. The view model
    /// owns managed rows and detached request payloads; persistence and runtime
    /// preview/send handling remain composed by MainWindow.
    /// </summary>
    internal partial class TonePresetManagerWindow : Window
    {
        private readonly TonePresetManagerViewModel viewModel;

        public TonePresetManagerWindow(TonePresetManagerViewModel viewModel)
        {
            this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            InitializeComponent();
            DataContext = viewModel;
            Closed += OnWindowClosed;
        }

        private void AddPreset_Click(object? sender, RoutedEventArgs e)
        {
            viewModel.AddPreset();
            SetStatus("New tone preset added.");
        }

        private void DeletePreset_Click(object? sender, RoutedEventArgs e)
        {
            viewModel.DeleteSelected();
            SetStatus("Tone preset deleted.");
        }

        private void AddTone_Click(object? sender, RoutedEventArgs e)
        {
            viewModel.AddTone();
            SetStatus("Tone step added.");
        }

        private void AddHold_Click(object? sender, RoutedEventArgs e)
        {
            viewModel.AddHold();
            SetStatus("Hold step added.");
        }

        private void DeleteStep_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button button
                && button.DataContext is TonePresetManagerViewModel.TonePresetStepItem step)
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
                && button.DataContext is TonePresetManagerViewModel.TonePresetStepItem step)
            {
                viewModel.MoveStep(step, direction);
                SetStatus("Step order updated.");
            }
        }

        private void Preview_Click(object? sender, RoutedEventArgs e)
        {
            viewModel.Preview();
            SetStatus("Tone preset preview requested.");
        }

        private void Send_Click(object? sender, RoutedEventArgs e)
        {
            viewModel.Send();
            SetStatus("Tone preset send requested.");
        }

        private void Save_Click(object? sender, RoutedEventArgs e)
        {
            viewModel.Commit();
            SetStatus("Tone preset changes saved.");
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
