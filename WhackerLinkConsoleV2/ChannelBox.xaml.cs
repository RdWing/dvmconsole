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

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WhackerLinkConsoleV2.Controls
{
    public partial class ChannelBox : UserControl, INotifyPropertyChanged
    {
        private readonly SelectedChannelsManager _selectedChannelsManager;
        private bool _pttState;
        private string _lastSrcId = "0";

        public event EventHandler<ChannelBox> PTTButtonClicked;
        public event PropertyChangedEventHandler PropertyChanged;

        public string ChannelName { get; set; }
        public string SystemName { get; set; }

        public string LastSrcId
        {
            get => _lastSrcId;
            set
            {
                if (_lastSrcId != value)
                {
                    _lastSrcId = value;
                    OnPropertyChanged(nameof(LastSrcId));
                }
            }
        }

        public bool PttState
        {
            get => _pttState;
            set
            {
                _pttState = value;
                UpdatePTTColor();
            }
        }

        public string VoiceChannel { get; set; }

        public bool IsEditMode { get; set; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                UpdateBackground();
            }
        }

        public ChannelBox(SelectedChannelsManager selectedChannelsManager, string channelName, string systemName)
        {
            InitializeComponent();
            DataContext = this;
            _selectedChannelsManager = selectedChannelsManager;
            ChannelName = channelName;
            SystemName = $"System: {systemName}";
            LastSrcId = $"Last SRC: {LastSrcId}";
            UpdateBackground();
            MouseLeftButtonDown += ChannelBox_MouseLeftButtonDown;
        }

        private void ChannelBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (IsEditMode) return;

            IsSelected = !IsSelected;
            Background = IsSelected ? Brushes.Blue : Brushes.Gray;

            if (IsSelected)
            {
                _selectedChannelsManager.AddSelectedChannel(this);
            }
            else
            {
                _selectedChannelsManager.RemoveSelectedChannel(this);
            }
        }

        private void UpdatePTTColor()
        {
            if (IsEditMode) return;

            if (PttState)
                PttButton.Background = new SolidColorBrush(Colors.Red);
            else
                PttButton.Background = new SolidColorBrush(Colors.Green);
        }

        private void UpdateBackground()
        {
            Background = IsSelected ? Brushes.DodgerBlue : Brushes.DarkGray;
        }

        private void PTTButton_Click(object sender, RoutedEventArgs e)
        {
            PttState = !PttState;
            PTTButtonClicked.Invoke(sender, this);
        }

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }   
    }
}
