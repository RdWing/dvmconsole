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
        private readonly AudioManager _audioManager;

        private bool _pttState;
        private bool _pageState;
        private bool _holdState;
        private bool _emergency;
        private string _lastSrcId = "0";
        private double _volume = 1.0;

        internal LinearGradientBrush grayGradient;
        internal LinearGradientBrush redGradient;
        internal LinearGradientBrush orangeGradient;

        public FlashingBackgroundManager _flashingBackgroundManager;

        public event EventHandler<ChannelBox> PTTButtonClicked;
        public event EventHandler<ChannelBox> PageButtonClicked;
        public event EventHandler<ChannelBox> HoldChannelButtonClicked;

        public event PropertyChangedEventHandler PropertyChanged;

        public string ChannelName { get; set; }
        public string SystemName { get; set; }
        public string DstId { get; set; }

        public bool IsReceiving { get; set; } = false;

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

        public bool PageState
        {
            get => _pageState;
            set
            {
                _pageState = value;
                UpdatePageColor();
            }
        }

        public bool HoldState
        {
            get => _holdState;
            set
            {
                _holdState = value;
                UpdateHoldColor();
            }
        }

        public bool Emergency
        {
            get => _emergency;
            set
            {
                _emergency = value;

                Dispatcher.Invoke(() =>
                {
                    if (value)
                        _flashingBackgroundManager.Start();
                    else
                        _flashingBackgroundManager.Stop();
                });
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

        public double Volume
        {
            get => _volume;
            set
            {
                if (_volume != value)
                {
                    _volume = value;
                    OnPropertyChanged(nameof(Volume));
                    _audioManager.SetTalkgroupVolume(DstId, (float)value);
                }
            }
        }

        public ChannelBox(SelectedChannelsManager selectedChannelsManager, AudioManager audioManager, string channelName, string systemName, string dstId)
        {
            InitializeComponent();
            DataContext = this;
            _selectedChannelsManager = selectedChannelsManager;
            _audioManager = audioManager;
            _flashingBackgroundManager = new FlashingBackgroundManager(this);
            ChannelName = channelName;
            DstId = dstId;
            SystemName = $"System: {systemName}";
            LastSrcId = $"Last SRC: {LastSrcId}";
            UpdateBackground();
            MouseLeftButtonDown += ChannelBox_MouseLeftButtonDown;

            grayGradient = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0),
                EndPoint = new Point(0.5, 1)
            };

            grayGradient.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FFF0F0F0"), 0.485));
            grayGradient.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FFDCDCDC"), 0.517));

            redGradient = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0),
                EndPoint = new Point(0.5, 1)
            };

            redGradient.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FFFF0000"), 0.485));
            redGradient.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FFD50000"), 0.517));

            orangeGradient = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0),
                EndPoint = new Point(0.5, 1)
            };

            orangeGradient.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FFFFAF00"), 0.485));
            orangeGradient.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FFEEA400"), 0.517));

            PttButton.Background = grayGradient;
            PageSelectButton.Background = grayGradient;
            ChannelMarkerBtn.Background = grayGradient;

            if (SystemName == MainWindow.PLAYBACKSYS || ChannelName == MainWindow.PLAYBACKCHNAME || DstId == MainWindow.PLAYBACKTG)
            {
                PttButton.IsEnabled = false;
                PageSelectButton.IsEnabled = false;
                ChannelMarkerBtn.IsEnabled = false;
            }
        }

        private void ChannelBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (IsEditMode) return;

            IsSelected = !IsSelected;
            Background = IsSelected ? (Brush)new BrushConverter().ConvertFrom("#FF0B004B") : Brushes.Gray;

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
                PttButton.Background = redGradient;
            else
                PttButton.Background = grayGradient;
        }

        private void UpdatePageColor()
        {
            if (IsEditMode) return;

            if (PageState)
                PageSelectButton.Background = orangeGradient;
            else
                PageSelectButton.Background = grayGradient;
        }

        private void UpdateHoldColor()
        {
            if (IsEditMode) return;

            if (HoldState)
                ChannelMarkerBtn.Background = orangeGradient;
            else
                ChannelMarkerBtn.Background = grayGradient;
        }

        private void UpdateBackground()
        {
            if (SystemName == MainWindow.PLAYBACKSYS || ChannelName == MainWindow.PLAYBACKCHNAME || DstId == MainWindow.PLAYBACKTG)
            {
                Background = IsSelected ? (Brush)new BrushConverter().ConvertFrom("#FFC90000") : Brushes.DarkGray;
                return;
            }

            Background = IsSelected ? (Brush)new BrushConverter().ConvertFrom("#FF0B004B") : Brushes.DarkGray;
        }

        private void PTTButton_Click(object sender, RoutedEventArgs e)
        {
            if (!IsSelected) return;

            PttState = !PttState;
            PTTButtonClicked.Invoke(sender, this);
        }

        private void PageSelectButton_Click(object sender, RoutedEventArgs e)
        {
            if (!IsSelected) return;

            PageState = !PageState;
            PageButtonClicked.Invoke(sender, this);
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            Volume = e.NewValue;
        }

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void ChannelMarkerBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!IsSelected) return;

            HoldState = !HoldState;
            HoldChannelButtonClicked.Invoke(sender, this);
        }

        private void PttButton_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!IsSelected || PttState) return;

            ((Button)sender).Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF3FA0FF"));
        }

        private void PttButton_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!IsSelected || PttState) return;

            ((Button)sender).Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFDDDDDD"));
        }
    }
}
