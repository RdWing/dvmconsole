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
using System.Collections.Generic;
using NAudio.Wave;

namespace WhackerLinkConsoleV2
{
    public partial class AudioSettingsWindow : Window
    {
        public int? SelectedInputDeviceIndex { get; private set; }
        public int? SelectedOutputDeviceIndex { get; private set; }

        public AudioSettingsWindow()
        {
            InitializeComponent();
            LoadAudioDevices();
        }

        private void LoadAudioDevices()
        {
            InputDeviceComboBox.ItemsSource = GetAudioInputDevices();
            OutputDeviceComboBox.ItemsSource = GetAudioOutputDevices();
        }

        private List<string> GetAudioInputDevices()
        {
            List<string> inputDevices = new List<string>();

            for (int i = 0; i < WaveIn.DeviceCount; i++)
            {
                var deviceInfo = WaveIn.GetCapabilities(i);
                inputDevices.Add(deviceInfo.ProductName);
            }

            return inputDevices;
        }

        private List<string> GetAudioOutputDevices()
        {
            List<string> outputDevices = new List<string>();

            for (int i = 0; i < WaveOut.DeviceCount; i++)
            {
                var deviceInfo = WaveOut.GetCapabilities(i);
                outputDevices.Add(deviceInfo.ProductName);
            }

            return outputDevices;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedInputDeviceIndex = InputDeviceComboBox.SelectedIndex;
            SelectedOutputDeviceIndex = OutputDeviceComboBox.SelectedIndex;

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
