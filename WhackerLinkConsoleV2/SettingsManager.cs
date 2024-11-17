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

using System.IO;
using Newtonsoft.Json;

namespace WhackerLinkConsoleV2
{
    public class SettingsManager
    {
        private const string SettingsFilePath = "UserSettings.json";

        public bool ShowSystemStatus { get; set; } = true;
        public bool ShowChannels { get; set; } = true;
        public bool ShowAlertTones { get; set; } = true;

        public string LastCodeplugPath { get; set; } = null;

        public Dictionary<string, ChannelPosition> ChannelPositions { get; set; } = new Dictionary<string, ChannelPosition>();
        public Dictionary<string, ChannelPosition> SystemStatusPositions { get; set; } = new Dictionary<string, ChannelPosition>();
        public List<string> AlertToneFilePaths { get; set; } = new List<string>();
        public Dictionary<string, ChannelPosition> AlertTonePositions { get; set; } = new Dictionary<string, ChannelPosition>();

        public void LoadSettings()
        {
            if (!File.Exists(SettingsFilePath)) return;

            try
            {
                var json = File.ReadAllText(SettingsFilePath);
                var loadedSettings = JsonConvert.DeserializeObject<SettingsManager>(json);

                if (loadedSettings != null)
                {
                    ShowSystemStatus = loadedSettings.ShowSystemStatus;
                    ShowChannels = loadedSettings.ShowChannels;
                    ShowAlertTones = loadedSettings.ShowAlertTones;
                    LastCodeplugPath = loadedSettings.LastCodeplugPath;
                    ChannelPositions = loadedSettings.ChannelPositions ?? new Dictionary<string, ChannelPosition>();
                    SystemStatusPositions = loadedSettings.SystemStatusPositions ?? new Dictionary<string, ChannelPosition>();
                    AlertToneFilePaths = loadedSettings.AlertToneFilePaths ?? new List<string>();
                    AlertTonePositions = loadedSettings.AlertTonePositions ?? new Dictionary<string, ChannelPosition>();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading settings: {ex.Message}");
            }
        }

        public void UpdateAlertTonePaths(string newFilePath)
        {
            if (!AlertToneFilePaths.Contains(newFilePath))
            {
                AlertToneFilePaths.Add(newFilePath);
                SaveSettings();
            }
        }

        public void UpdateAlertTonePosition(string alertFileName, double x, double y)
        {
            AlertTonePositions[alertFileName] = new ChannelPosition { X = x, Y = y };
            SaveSettings();
        }

        public void UpdateChannelPosition(string channelName, double x, double y)
        {
            ChannelPositions[channelName] = new ChannelPosition { X = x, Y = y };
            SaveSettings();
        }

        public void UpdateSystemStatusPosition(string systemName, double x, double y)
        {
            SystemStatusPositions[systemName] = new ChannelPosition { X = x, Y = y };
            SaveSettings();
        }

        public void SaveSettings()
        {
            try
            {
                var json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(SettingsFilePath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving settings: {ex.Message}");
            }
        }
    }
}
