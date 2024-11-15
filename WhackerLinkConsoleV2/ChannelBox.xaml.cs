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
using System.Windows.Controls;
using System.Windows.Input;

namespace WhackerLinkConsoleV2.Controls
{
    public partial class ChannelBox : UserControl
    {
        public string ChannelName { get; set; }
        public string SystemName { get; set; }
        public string TGID { get; set; }

        public ChannelBox()
        {
            InitializeComponent();
            DataContext = this;

            MouseMove += ChannelBox_MouseMove;
            MouseDown += ChannelBox_MouseDown;
            MouseUp += ChannelBox_MouseUp;
        }

        public ChannelBox(string channelName, string systemName, string tgid) : this()
        {
            ChannelName = $"{channelName}";
            SystemName = $"System: {systemName}";
            TGID = $"TGID: {tgid}";
        }

        private Point _dragStartPoint;
        private bool _isDragging;

        private void ChannelBox_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
            _isDragging = false;
        }

        private void ChannelBox_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                Point currentPosition = e.GetPosition(null);

                if (!_isDragging && (Math.Abs(currentPosition.X - _dragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
                                     Math.Abs(currentPosition.Y - _dragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance))
                {
                    _isDragging = true;
                    DataObject data = new DataObject("ChannelBox", this);
                    DragDrop.DoDragDrop(this, data, DragDropEffects.Move);
                }
            }
        }

        private void ChannelBox_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _isDragging = false;
        }

        private void PTTButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show($"Imagine you were talking on {ChannelName} rn");
        }
    }
}
