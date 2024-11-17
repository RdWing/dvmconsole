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

using WhackerLinkConsoleV2.Controls;

namespace WhackerLinkConsoleV2
{
    public class SelectedChannelsManager
    {
        private readonly HashSet<ChannelBox> _selectedChannels;

        public event Action SelectedChannelsChanged;

        public SelectedChannelsManager()
        {
            _selectedChannels = new HashSet<ChannelBox>();
        }

        public void AddSelectedChannel(ChannelBox channel)
        {
            if (_selectedChannels.Add(channel))
            {
                channel.IsSelected = true;
                SelectedChannelsChanged.Invoke();
            }
        }

        public void RemoveSelectedChannel(ChannelBox channel)
        {
            if (_selectedChannels.Remove(channel))
            {
                channel.IsSelected = false;
                SelectedChannelsChanged.Invoke();
            }
        }

        public void ClearSelections()
        {
            foreach (var channel in _selectedChannels)
            {
                channel.IsSelected = false;
            }
            _selectedChannels.Clear();
            SelectedChannelsChanged.Invoke();
        }

        public IReadOnlyCollection<ChannelBox> GetSelectedChannels() => _selectedChannels;
    }
}
