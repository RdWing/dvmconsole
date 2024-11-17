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
