// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DvmConsole.Avalonia.ViewModels;

namespace DvmConsole.Avalonia.Views
{
    internal partial class QuickCallWindow : Window
    {
        private readonly QuickCallViewModel viewModel;

        internal QuickCallWindow(QuickCallViewModel viewModel)
        {
            this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            DataContext = viewModel;
            InitializeComponent();
        }

        internal event Action<QuickCallRequest>? SendRequested;

        private void Send_Click(object? sender, RoutedEventArgs e)
        {
            if (viewModel.TryBuildRequest(out QuickCallRequest? request)
                && request is not null)
            {
                SendRequested?.Invoke(request);
                Close();
            }
        }

        private void Cancel_Click(object? sender, RoutedEventArgs e)
            => Close();
    }
}
