/*
* WhackerLink - WhackerLinkLib
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
* Copyright (C) 2024-2025 Caleb, K4PHP
* 
*/

using WhackerLinkLib.Models.Radio;
using WhackerLinkLib.Network;

namespace WhackerLinkConsoleV2
{
    /// <summary>
    /// WhackerLink peer/client websocket manager for having multiple systems
    /// </summary>
    public class FneSystemManager
    {
        private readonly Dictionary<string, PeerSystem> _webSocketHandlers;

        /// <summary>
        /// Creates an instance of <see cref="PeerSystem"/>
        /// </summary>
        public FneSystemManager()
        {
            _webSocketHandlers = new Dictionary<string, PeerSystem>();
        }

        /// <summary>
        /// Create a new <see cref="PeerSystem"/> for a new system
        /// </summary>
        /// <param name="systemId"></param>
        public void AddFneSystem(string systemId, Codeplug.System system, MainWindow mainWindow)
        {
            if (!_webSocketHandlers.ContainsKey(systemId))
            {
                _webSocketHandlers[systemId] = new PeerSystem(mainWindow, system);
            }
        }

        /// <summary>
        /// Return a <see cref="PeerSystem"/> by looking up a systemid
        /// </summary>
        /// <param name="systemId"></param>
        /// <returns></returns>
        /// <exception cref="KeyNotFoundException"></exception>
        public PeerSystem GetFneSystem(string systemId)
        {
            if (_webSocketHandlers.TryGetValue(systemId, out var handler))
            {
                return handler;
            }
            throw new KeyNotFoundException($"WebSocketHandler for system '{systemId}' not found.");
        }

        /// <summary>
        /// Delete a <see cref="Peer"/> by system id
        /// </summary>
        /// <param name="systemId"></param>
        public void RemoveFneSystem(string systemId)
        {
            if (_webSocketHandlers.TryGetValue(systemId, out var handler))
            {
                handler.peer.Stop();
                _webSocketHandlers.Remove(systemId);
            }
        }

        /// <summary>
        /// Check if the manager has a handler
        /// </summary>
        /// <param name="systemId"></param>
        /// <returns></returns>
        public bool HasFneSystem(string systemId)
        {
            return _webSocketHandlers.ContainsKey(systemId);
        }

        /// <summary>
        /// Cleanup
        /// </summary>
        public void ClearAll()
        {
            foreach (var handler in _webSocketHandlers.Values)
            {
                handler.peer.Stop();
            }
            _webSocketHandlers.Clear();
        }
    }
}
