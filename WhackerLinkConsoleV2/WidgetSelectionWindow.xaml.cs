/*
* WhackerLink - WhackerLinkConsoleV2
*
* This program is free software: you can redistribute it and/or modify
* it under the terms of the GNU General Public License as published by
* the Free Software Foundation, either version 3 of the License, or
* (at your option) any later version.
*
* This program is distributed in the hope that it will be useful,
* but WITHOUT ANY WARRANTY; without even the implied warranty of
* MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
* GNU General Public License for more details.
*
* You should have received a copy of the GNU General Public License
* along with this program.  If not, see <http://www.gnu.org/licenses/>.
* 
* Copyright (C) 2024 Caleb, K4PHP
* 
*/

using System.Windows;

namespace WhackerLinkConsoleV2
{
    public partial class WidgetSelectionWindow : Window
    {
        public bool ShowSystemStatus { get; private set; } = true;
        public bool ShowChannels { get; private set; } = true;

        public WidgetSelectionWindow()
        {
            InitializeComponent();
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            ShowSystemStatus = SystemStatusCheckBox.IsChecked ?? false;
            ShowChannels = ChannelCheckBox.IsChecked ?? false;
            DialogResult = true;
            Close();
        }
    }
}
