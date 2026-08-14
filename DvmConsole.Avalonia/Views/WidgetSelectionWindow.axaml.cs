// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DvmConsole.Avalonia.Views
{
    internal partial class WidgetSelectionWindow : Window
    {
        public WidgetSelectionWindow(bool showSystemStatus, bool showChannels, bool showAlertTones)
        {
            InitializeComponent();
            ShowSystemStatusCheckBox.IsChecked = showSystemStatus;
            ShowChannelsCheckBox.IsChecked = showChannels;
            ShowAlertTonesCheckBox.IsChecked = showAlertTones;
        }

        public event Action<bool, bool, bool>? SaveRequested;

        private void Apply_Click(object? sender, RoutedEventArgs e)
        {
            SaveRequested?.Invoke(
                ShowSystemStatusCheckBox.IsChecked == true,
                ShowChannelsCheckBox.IsChecked == true,
                ShowAlertTonesCheckBox.IsChecked == true);
            Close();
        }

        private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
    }
}
