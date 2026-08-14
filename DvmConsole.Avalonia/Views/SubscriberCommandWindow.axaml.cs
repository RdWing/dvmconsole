// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DvmConsole.Avalonia.ViewModels;

namespace DvmConsole.Avalonia.Views
{
    /// <summary>
    /// Presentation-only shell for one P25 subscriber command. The view-model
    /// validates and executes through the injected Core command service; this
    /// window only owns close/send interaction and status display.
    /// </summary>
    internal partial class SubscriberCommandWindow : Window
    {
        private readonly SubscriberCommandViewModel viewModel;

        public SubscriberCommandWindow(
            SubscriberCommandViewModel viewModel,
            string title)
        {
            this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            Title = string.IsNullOrWhiteSpace(title)
                ? "Subscriber Command"
                : title;
            DataContext = viewModel;
            InitializeComponent();
        }

        private async void Submit_Click(object? sender, RoutedEventArgs e)
        {
            var result = await viewModel.SubmitAsync();
            if (result.Succeeded)
                Close();
        }

        private void Cancel_Click(object? sender, RoutedEventArgs e)
            => Close();
    }
}
