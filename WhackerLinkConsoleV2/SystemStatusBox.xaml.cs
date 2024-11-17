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
using System.Runtime.CompilerServices;
using System.Windows.Controls;

namespace WhackerLinkConsoleV2.Controls
{
    public partial class SystemStatusBox : UserControl, INotifyPropertyChanged
    {
        private string _connectionState = "Disconnected";

        public string SystemName { get; set; }
        public string AddressPort { get; set; }

        public string ConnectionState
        {
            get => _connectionState;
            set
            {
                if (_connectionState != value)
                {
                    _connectionState = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public SystemStatusBox()
        {
            InitializeComponent();
            DataContext = this;
        }

        public SystemStatusBox(string systemName, string address, int port) : this()
        {
            SystemName = systemName;
            AddressPort = $"Address: {address}:{port}";
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void NotifyPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
