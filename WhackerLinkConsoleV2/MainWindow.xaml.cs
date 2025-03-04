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
* Copyright (C) 2024-2025 Caleb, K4PHP
* Copyright (C) 2025 J. Dean
* 
*/

using Microsoft.Win32;
using System;
using System.Timers;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WhackerLinkLib.Models.Radio;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using WhackerLinkConsoleV2.Controls;
using WebSocketManager = WhackerLinkLib.Managers.WebSocketManager;
using System.Windows.Media;
using WhackerLinkLib.Utils;
using WhackerLinkLib.Models;
using System.Net;
using NAudio.Wave;
using WhackerLinkLib.Interfaces;
using WhackerLinkLib.Models.IOSP;

namespace WhackerLinkConsoleV2
{
    public partial class MainWindow : Window
    {
        public Codeplug Codeplug { get; set; }
        private bool isEditMode = false;

        private bool globalPttState = false;

        private UIElement _draggedElement;
        private Point _startPoint;
        private double _offsetX;
        private double _offsetY;
        private bool _isDragging;

        private SettingsManager _settingsManager = new SettingsManager();
        private SelectedChannelsManager _selectedChannelsManager;
        private FlashingBackgroundManager _flashingManager;
        private WaveFilePlaybackManager _emergencyAlertPlayback;
        private WebSocketManager _webSocketManager = new WebSocketManager();

        private ChannelBox playbackChannelBox;

        public static string PLAYBACKTG = "LOCPLAYBACK";
        public static string PLAYBACKSYS = "LOCPLAYBACKSYS";
        public static string PLAYBACKCHNAME = "PLAYBACK";

        private readonly WaveInEvent _waveIn;
        private readonly AudioManager _audioManager;

        private static System.Timers.Timer _channelHoldTimer;

        public MainWindow()
        {
#if DEBUG
            ConsoleNative.ShowConsole();
#endif
            InitializeComponent();
            _settingsManager.LoadSettings();
            _selectedChannelsManager = new SelectedChannelsManager();
            _flashingManager = new FlashingBackgroundManager(null, ChannelsCanvas, null, this);
            _emergencyAlertPlayback = new WaveFilePlaybackManager(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "emergency.wav"));

            _channelHoldTimer = new System.Timers.Timer(10000);
            _channelHoldTimer.Elapsed += OnHoldTimerElapsed;
            _channelHoldTimer.AutoReset = true;
            _channelHoldTimer.Enabled = true;

            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(8000, 16, 1)
            };
            _waveIn.DataAvailable += WaveIn_DataAvailable;
            _waveIn.RecordingStopped += WaveIn_RecordingStopped;

            _waveIn.StartRecording();

            _audioManager = new AudioManager(_settingsManager);

            _selectedChannelsManager.SelectedChannelsChanged += SelectedChannelsChanged;
            Loaded += MainWindow_Loaded;
        }

        private void OpenCodeplug_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "Codeplug Files (*.yml)|*.yml|All Files (*.*)|*.*",
                Title = "Open Codeplug"
            };
            if (openFileDialog.ShowDialog() == true)
            {
                LoadCodeplug(openFileDialog.FileName);

                _settingsManager.LastCodeplugPath = openFileDialog.FileName;
                _settingsManager.SaveSettings();
            }
        }

        private void ResetSettings_Click(object sender, RoutedEventArgs e)
        {
            if (File.Exists("UserSettings.json"))
                File.Delete("UserSettings.json");
        }

        private void LoadCodeplug(string filePath)
        {
            try
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();

                var yaml = File.ReadAllText(filePath);
                Codeplug = deserializer.Deserialize<Codeplug>(yaml);

                GenerateChannelWidgets();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading codeplug: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void GenerateChannelWidgets()
        {
            ChannelsCanvas.Children.Clear();
            double offsetX = 20;
            double offsetY = 20;

            if (Codeplug != null)
            {
                foreach (var system in Codeplug.Systems)
                {
                    var systemStatusBox = new SystemStatusBox(system.Name, system.Address, system.Port);

                    if (_settingsManager.SystemStatusPositions.TryGetValue(system.Name, out var position))
                    {
                        Canvas.SetLeft(systemStatusBox, position.X);
                        Canvas.SetTop(systemStatusBox, position.Y);
                    }
                    else
                    {
                        Canvas.SetLeft(systemStatusBox, offsetX);
                        Canvas.SetTop(systemStatusBox, offsetY);
                    }

                    systemStatusBox.MouseLeftButtonDown += SystemStatusBox_MouseLeftButtonDown;
                    systemStatusBox.MouseMove += SystemStatusBox_MouseMove;
                    systemStatusBox.MouseRightButtonDown += SystemStatusBox_MouseRightButtonDown;

                    ChannelsCanvas.Children.Add(systemStatusBox);

                    offsetX += 225;
                    if (offsetX + 220 > ChannelsCanvas.ActualWidth)
                    {
                        offsetX = 20;
                        offsetY += 106;
                    }

                    _webSocketManager.AddWebSocketHandler(system.Name);

                    IPeer handler = _webSocketManager.GetWebSocketHandler(system.Name);
                    handler.OnVoiceChannelResponse += HandleVoiceResponse;
                    handler.OnVoiceChannelRelease += HandleVoiceRelease;
                    handler.OnEmergencyAlarmResponse += HandleEmergencyAlarmResponse;
                    handler.OnAudioData += HandleReceivedAudio;
                    handler.OnAffiliationUpdate += HandleAffiliationUpdate;

                    if (!_settingsManager.ShowSystemStatus)
                        systemStatusBox.Visibility = Visibility.Collapsed;

                    handler.OnUnitRegistrationResponse += (response) =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (response.Status == (int)ResponseType.GRANT)
                            {
                                systemStatusBox.Background = (Brush)new BrushConverter().ConvertFrom("#FF00BC48"); 
                                systemStatusBox.ConnectionState = "Connected";
                            }
                            else
                            {
                                systemStatusBox.Background = new SolidColorBrush(Colors.Red);
                                systemStatusBox.ConnectionState = "Disconnected";
                            }
                        });
                    };

                    handler.OnClose += () =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            systemStatusBox.Background = new SolidColorBrush(Colors.Red);
                            systemStatusBox.ConnectionState = "Disconnected";
                        });

                        Thread.Sleep(1000);

                        Task.Factory.StartNew(() =>
                        {
                            handler.Connect(system.Address, system.Port);

                            U_REG_REQ release = new U_REG_REQ
                            {
                                SrcId = system.Rid,
                                Site = system.Site
                            };

                            handler.SendMessage(release.GetData());
                        });
                    };

                    Task.Factory.StartNew(() =>
                    {
                        handler.Connect(system.Address, system.Port);

                        handler.OnGroupAffiliationResponse += (response) => { /* TODO */ };

                        if (handler.IsConnected)
                        {
                            U_REG_REQ release = new U_REG_REQ
                            {
                                SrcId = system.Rid,
                                Site = system.Site
                            };

                            handler.SendMessage(release.GetData());
                        }
                        else
                        {
                            systemStatusBox.Background = new SolidColorBrush(Colors.Red);
                            systemStatusBox.ConnectionState = "Disconnected";
                        }
                    });
                }
            }

            if (_settingsManager.ShowChannels && Codeplug != null)
            {
                foreach (var zone in Codeplug.Zones)
                {
                    foreach (var channel in zone.Channels)
                    {
                        var channelBox = new ChannelBox(_selectedChannelsManager, _audioManager, channel.Name, channel.System, channel.Tgid);

                        if (_settingsManager.ChannelPositions.TryGetValue(channel.Name, out var position))
                        {
                            Canvas.SetLeft(channelBox, position.X);
                            Canvas.SetTop(channelBox, position.Y);
                        }
                        else
                        {
                            Canvas.SetLeft(channelBox, offsetX);
                            Canvas.SetTop(channelBox, offsetY);
                        }

                        channelBox.PTTButtonClicked += ChannelBox_PTTButtonClicked;
                        channelBox.PageButtonClicked += ChannelBox_PageButtonClicked;
                        channelBox.HoldChannelButtonClicked += ChannelBox_HoldChannelButtonClicked;

                        channelBox.MouseLeftButtonDown += ChannelBox_MouseLeftButtonDown;
                        channelBox.MouseMove += ChannelBox_MouseMove;
                        channelBox.MouseRightButtonDown += ChannelBox_MouseRightButtonDown;
                        ChannelsCanvas.Children.Add(channelBox);

                        offsetX += 225;

                        if (offsetX + 220 > ChannelsCanvas.ActualWidth)
                        {
                            offsetX = 20;
                            offsetY += 106;
                        }
                    }
                }
            }

            if (_settingsManager.ShowAlertTones && Codeplug != null)
            {
                foreach (var alertPath in _settingsManager.AlertToneFilePaths)
                {
                    var alertTone = new AlertTone(alertPath)
                    {
                        IsEditMode = isEditMode
                    };

                    alertTone.OnAlertTone += SendAlertTone;

                    if (_settingsManager.AlertTonePositions.TryGetValue(alertPath, out var position))
                    {
                        Canvas.SetLeft(alertTone, position.X);
                        Canvas.SetTop(alertTone, position.Y);
                    }
                    else
                    {
                        Canvas.SetLeft(alertTone, 20);
                        Canvas.SetTop(alertTone, 20);
                    }

                    alertTone.MouseRightButtonUp += AlertTone_MouseRightButtonUp;

                    ChannelsCanvas.Children.Add(alertTone);
                }
            }

            playbackChannelBox = new ChannelBox(_selectedChannelsManager, _audioManager, PLAYBACKCHNAME, PLAYBACKSYS, PLAYBACKTG);

            if (_settingsManager.ChannelPositions.TryGetValue(PLAYBACKCHNAME, out var pos))
            {
                Canvas.SetLeft(playbackChannelBox, pos.X);
                Canvas.SetTop(playbackChannelBox, pos.Y);
            }
            else
            {
                Canvas.SetLeft(playbackChannelBox, offsetX);
                Canvas.SetTop(playbackChannelBox, offsetY);
            }

            playbackChannelBox.PTTButtonClicked += ChannelBox_PTTButtonClicked;
            playbackChannelBox.PageButtonClicked += ChannelBox_PageButtonClicked;
            playbackChannelBox.HoldChannelButtonClicked += ChannelBox_HoldChannelButtonClicked;

            playbackChannelBox.MouseLeftButtonDown += ChannelBox_MouseLeftButtonDown;
            playbackChannelBox.MouseMove += ChannelBox_MouseMove;
            playbackChannelBox.MouseRightButtonDown += ChannelBox_MouseRightButtonDown;
            ChannelsCanvas.Children.Add(playbackChannelBox);

            //offsetX += 225;

            //if (offsetX + 220 > ChannelsCanvas.ActualWidth)
            //{
            //    offsetX = 20;
            //    offsetY += 106;
            //}

            AdjustCanvasHeight();
        }

        private void WaveIn_RecordingStopped(object sender, EventArgs e)
        {
            /* stub */
        }

        private void WaveIn_DataAvailable(object sender, WaveInEventArgs e)
        {
            bool isAnyTgOn = false;

            foreach (ChannelBox channel in _selectedChannelsManager.GetSelectedChannels())
            {
                if (channel.SystemName == PLAYBACKSYS || channel.ChannelName == PLAYBACKCHNAME || channel.DstId == PLAYBACKTG)
                {
                    playbackChannelBox.IsReceiving = true;
                    continue;
                }

                Codeplug.System system = Codeplug.GetSystemForChannel(channel.ChannelName);
                Codeplug.Channel cpgChannel = Codeplug.GetChannelByName(channel.ChannelName);
                IPeer handler = _webSocketManager.GetWebSocketHandler(system.Name);

                if (channel.IsSelected && channel.VoiceChannel != null && channel.PttState)
                {
                    isAnyTgOn = true;

                    object voicePaket = new
                    {
                        type = PacketType.AUDIO_DATA,
                        data = new {
                            Data = e.Buffer,
                            VoiceChannel = new VoiceChannel
                            {
                                Frequency = channel.VoiceChannel,
                                DstId = cpgChannel.Tgid,
                                SrcId = system.Rid,
                                Site = system.Site
                            },
                            Site = system.Site
                        }
                    };

                    handler.SendMessage(voicePaket);
                }
            }

            if (isAnyTgOn && playbackChannelBox.IsSelected)
                _audioManager.AddTalkgroupStream(PLAYBACKTG, e.Buffer);
        }

        private void SelectedChannelsChanged()
        {
            foreach (ChannelBox channel in _selectedChannelsManager.GetSelectedChannels())
            {
                if (channel.SystemName == PLAYBACKSYS || channel.ChannelName == PLAYBACKCHNAME || channel.DstId == PLAYBACKTG)
                    continue;

                Codeplug.System system = Codeplug.GetSystemForChannel(channel.ChannelName);
                Codeplug.Channel cpgChannel = Codeplug.GetChannelByName(channel.ChannelName);
                IPeer handler = _webSocketManager.GetWebSocketHandler(system.Name);

                if (channel.IsSelected && handler.IsConnected)
                {
                    GRP_AFF_REQ release = new GRP_AFF_REQ
                    {
                        SrcId = system.Rid,
                        DstId = cpgChannel.Tgid,
                        Site = system.Site
                    };

                    handler.SendMessage(release.GetData());
                }
            }
        }

        private void AudioSettings_Click(object sender, RoutedEventArgs e)
        {
            List<Codeplug.Channel> channels = Codeplug?.Zones.SelectMany(z => z.Channels).ToList() ?? new List<Codeplug.Channel>();

            AudioSettingsWindow audioSettingsWindow = new AudioSettingsWindow(_settingsManager, _audioManager, channels);
            audioSettingsWindow.ShowDialog();
        }

        private void P25Page_Click(object sender, RoutedEventArgs e)
        {
            DigitalPageWindow pageWindow = new DigitalPageWindow(Codeplug.Systems);
            pageWindow.Owner = this;
            if (pageWindow.ShowDialog() == true)
            {
                IPeer handler = _webSocketManager.GetWebSocketHandler(pageWindow.RadioSystem.Name);

                CALL_ALRT_REQ callAlert = new CALL_ALRT_REQ
                {
                    SrcId = pageWindow.RadioSystem.Rid,
                    DstId = pageWindow.DstId
                };

                handler.SendMessage(callAlert.GetData());
            }
        }

        private async void ManualPage_Click(object sender, RoutedEventArgs e)
        {
            QuickCallPage pageWindow = new QuickCallPage();
            pageWindow.Owner = this;
            if (pageWindow.ShowDialog() == true)
            {
                foreach (ChannelBox channel in _selectedChannelsManager.GetSelectedChannels())
                {
                    Codeplug.System system = Codeplug.GetSystemForChannel(channel.ChannelName);
                    Codeplug.Channel cpgChannel = Codeplug.GetChannelByName(channel.ChannelName);
                    IPeer handler = _webSocketManager.GetWebSocketHandler(system.Name);

                    if (channel.PageState)
                    {
                        ToneGenerator generator = new ToneGenerator();

                        double toneADuration = 1.0;
                        double toneBDuration = 3.0;

                        byte[] toneA = generator.GenerateTone(Double.Parse(pageWindow.ToneA), toneADuration);
                        byte[] toneB = generator.GenerateTone(Double.Parse(pageWindow.ToneB), toneBDuration);

                        byte[] combinedAudio = new byte[toneA.Length + toneB.Length];
                        Buffer.BlockCopy(toneA, 0, combinedAudio, 0, toneA.Length);
                        Buffer.BlockCopy(toneB, 0, combinedAudio, toneA.Length, toneB.Length);

                        int chunkSize = 1600;
                        int totalChunks = (combinedAudio.Length + chunkSize - 1) / chunkSize;

                        Task.Factory.StartNew(() =>
                        {
                            //_waveProvider.ClearBuffer();
                            _audioManager.AddTalkgroupStream(cpgChannel.Tgid, combinedAudio);
                        });

                        await Task.Run(() =>
                        {
                            for (int i = 0; i < totalChunks; i++)
                            {
                                int offset = i * chunkSize;
                                int size = Math.Min(chunkSize, combinedAudio.Length - offset);

                                byte[] chunk = new byte[chunkSize];
                                Buffer.BlockCopy(combinedAudio, offset, chunk, 0, size);

                                AudioPacket voicePacket = new AudioPacket
                                {
                                    Data = chunk,
                                    VoiceChannel = new VoiceChannel
                                    {
                                        Frequency = channel.VoiceChannel,
                                        DstId = cpgChannel.Tgid,
                                        SrcId = system.Rid,
                                        Site = system.Site
                                    },
                                    Site = system.Site,
                                    LopServerVocode = true
                                };

                                handler.SendMessage(voicePacket.GetData());
                            }
                        });

                        double totalDurationMs = (toneADuration + toneBDuration) * 1000 + 250;
                        await Task.Delay((int)totalDurationMs);

                        GRP_VCH_RLS release = new GRP_VCH_RLS
                        {
                            SrcId = system.Rid,
                            DstId = cpgChannel.Tgid,
                            Channel = channel.VoiceChannel,
                            Site = system.Site
                        };

                        handler.SendMessage(release.GetData());

                        Dispatcher.Invoke(() =>
                        {
                            //channel.PageState = false; // TODO: Investigate
                            channel.PageSelectButton.Background = channel.grayGradient;
                        });
                    }
                }
            }
        }

        private void SendAlertTone(AlertTone e)
        {
            Task.Factory.StartNew(() => SendAlertTone(e.AlertFilePath));
        }

        private void SendAlertTone(string filePath, bool forHold = false)
        {
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                try
                {
                    foreach (ChannelBox channel in _selectedChannelsManager.GetSelectedChannels())
                    {
                        if (channel.SystemName == PLAYBACKSYS || channel.ChannelName == PLAYBACKCHNAME || channel.DstId == PLAYBACKTG)
                            continue;

                        Codeplug.System system = Codeplug.GetSystemForChannel(channel.ChannelName);
                        Codeplug.Channel cpgChannel = Codeplug.GetChannelByName(channel.ChannelName);
                        IPeer handler = _webSocketManager.GetWebSocketHandler(system.Name);

                        if (channel.PageState || (forHold && channel.HoldState))
                        {
                            byte[] pcmData;

                            Task.Factory.StartNew(async () => {
                                using (var waveReader = new WaveFileReader(filePath))
                                {
                                    if (waveReader.WaveFormat.Encoding != WaveFormatEncoding.Pcm ||
                                        waveReader.WaveFormat.SampleRate != 8000 ||
                                        waveReader.WaveFormat.BitsPerSample != 16 ||
                                        waveReader.WaveFormat.Channels != 1)
                                    {
                                        MessageBox.Show("The alert tone must be PCM 16-bit, Mono, 8000Hz format.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                                        return;
                                    }

                                    using (MemoryStream ms = new MemoryStream())
                                    {
                                        waveReader.CopyTo(ms);
                                        pcmData = ms.ToArray();
                                    }
                                }

                                int chunkSize = 1600;
                                int totalChunks = (pcmData.Length + chunkSize - 1) / chunkSize;

                                if (pcmData.Length % chunkSize != 0)
                                {
                                    byte[] paddedData = new byte[totalChunks * chunkSize];
                                    Buffer.BlockCopy(pcmData, 0, paddedData, 0, pcmData.Length);
                                    pcmData = paddedData;
                                }

                                Task.Factory.StartNew(() =>
                                {
                                    _audioManager.AddTalkgroupStream(cpgChannel.Tgid, pcmData);
                                });

                                DateTime startTime = DateTime.UtcNow;

                                for (int i = 0; i < totalChunks; i++)
                                {
                                    int offset = i * chunkSize;
                                    byte[] chunk = new byte[chunkSize];
                                    Buffer.BlockCopy(pcmData, offset, chunk, 0, chunkSize);

                                    AudioPacket voicePacket = new AudioPacket
                                    {
                                        Data = chunk,
                                        VoiceChannel = new VoiceChannel
                                        {
                                            Frequency = channel.VoiceChannel,
                                            DstId = cpgChannel.Tgid,
                                            SrcId = system.Rid,
                                            Site = system.Site
                                        },
                                        Site = system.Site,
                                        LopServerVocode = true
                                    };

                                    handler.SendMessage(voicePacket.GetData());

                                    DateTime nextPacketTime = startTime.AddMilliseconds((i + 1) * 100);
                                    TimeSpan waitTime = nextPacketTime - DateTime.UtcNow;

                                    if (waitTime.TotalMilliseconds > 0)
                                    {
                                        await Task.Delay(waitTime);
                                    }
                                }

                                double totalDurationMs = ((double)pcmData.Length / 16000);
                                await Task.Delay((int)totalDurationMs);
                                      
                                GRP_VCH_RLS release = new GRP_VCH_RLS
                                {
                                    SrcId = system.Rid,
                                    DstId = cpgChannel.Tgid,
                                    Channel = channel.VoiceChannel,
                                    Site = system.Site
                                };

                                Dispatcher.Invoke(() =>
                                {
                                    handler.SendMessage(release.GetData());

                                    if (forHold)
                                        channel.PttButton.Background = channel.grayGradient;
                                    else
                                        channel.PageState = false;
                                });

                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to process alert tone: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                MessageBox.Show("Alert file not set or file not found.", "Alert", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SelectWidgets_Click(object sender, RoutedEventArgs e)
        {
            WidgetSelectionWindow widgetSelectionWindow = new WidgetSelectionWindow();
            widgetSelectionWindow.Owner = this;
            if (widgetSelectionWindow.ShowDialog() == true)
            {
                _settingsManager.ShowSystemStatus = widgetSelectionWindow.ShowSystemStatus;
                _settingsManager.ShowChannels = widgetSelectionWindow.ShowChannels;
                _settingsManager.ShowAlertTones = widgetSelectionWindow.ShowAlertTones;

                GenerateChannelWidgets();
                _settingsManager.SaveSettings();
            }
        }

        private void HandleEmergencyAlarmResponse(EMRG_ALRM_RSP response)
        {
            bool forUs = false;

            foreach (ChannelBox channel in _selectedChannelsManager.GetSelectedChannels())
            {
                Codeplug.System system = Codeplug.GetSystemForChannel(channel.ChannelName);
                Codeplug.Channel cpgChannel = Codeplug.GetChannelByName(channel.ChannelName);

                if (response.DstId == cpgChannel.Tgid)
                {
                    forUs = true;
                    channel.Emergency = true;
                    channel.LastSrcId = response.SrcId;
                }
            }

            if (forUs)
            {
                Dispatcher.Invoke(() =>
                {
                    _flashingManager.Start();
                    _emergencyAlertPlayback.Start();
                });
            }
        }

        private void HandleReceivedAudio(AudioPacket audioPacket)
        {
            bool shouldReceive = false;
            string talkgroupId = audioPacket.VoiceChannel.DstId;

            foreach (ChannelBox channel in _selectedChannelsManager.GetSelectedChannels())
            {
                if (channel.SystemName == PLAYBACKSYS || channel.ChannelName == PLAYBACKCHNAME || channel.DstId == PLAYBACKTG)
                    continue;

                Codeplug.System system = Codeplug.GetSystemForChannel(channel.ChannelName);
                Codeplug.Channel cpgChannel = Codeplug.GetChannelByName(channel.ChannelName);
                IPeer handler = _webSocketManager.GetWebSocketHandler(system.Name);

                if (audioPacket.VoiceChannel.SrcId != system.Rid && audioPacket.VoiceChannel.Frequency == channel.VoiceChannel && audioPacket.VoiceChannel.DstId == cpgChannel.Tgid)
                    shouldReceive = true;
            }

            if (shouldReceive)
                _audioManager.AddTalkgroupStream(talkgroupId, audioPacket.Data);
        }

        private void HandleAffiliationUpdate(AFF_UPDATE affUpdate)
        {
            foreach (ChannelBox channel in _selectedChannelsManager.GetSelectedChannels())
            {
                if (channel.SystemName == PLAYBACKSYS || channel.ChannelName == PLAYBACKCHNAME || channel.DstId == PLAYBACKTG)
                    continue;

                Codeplug.System system = Codeplug.GetSystemForChannel(channel.ChannelName);
                Codeplug.Channel cpgChannel = Codeplug.GetChannelByName(channel.ChannelName);
                IPeer handler = _webSocketManager.GetWebSocketHandler(system.Name);

                bool ridExists = affUpdate.Affiliations.Any(aff => aff.SrcId == system.Rid);
                bool tgidExists = affUpdate.Affiliations.Any(aff => aff.DstId == cpgChannel.Tgid);

                if (ridExists && tgidExists)
                {
                    Console.WriteLine("rid aff'ed");
                }
                else
                {
                    Console.WriteLine("rid not aff'ed");

                    GRP_AFF_REQ affReq = new GRP_AFF_REQ
                    {
                        SrcId = system.Rid,
                        DstId = cpgChannel.Tgid,
                        Site = system.Site
                    };

                    handler.SendMessage(affReq.GetData());
                }
            }
        }

        private void HandleVoiceRelease(GRP_VCH_RLS response)
        {
            foreach (ChannelBox channel in _selectedChannelsManager.GetSelectedChannels())
            {
                if (channel.SystemName == PLAYBACKSYS || channel.ChannelName == PLAYBACKCHNAME || channel.DstId == PLAYBACKTG)
                    continue;

                Codeplug.System system = Codeplug.GetSystemForChannel(channel.ChannelName);
                Codeplug.Channel cpgChannel = Codeplug.GetChannelByName(channel.ChannelName);
                IPeer handler = _webSocketManager.GetWebSocketHandler(system.Name);

                if (response.DstId == cpgChannel.Tgid && response.SrcId != system.Rid)
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (channel.IsSelected)
                        {
                            channel.Background = (Brush)new BrushConverter().ConvertFrom("#FF0B004B");
                            channel.IsReceiving = false;
                        }
                        else
                        {
                            channel.Background = new SolidColorBrush(Colors.DarkGray);
                            channel.IsReceiving = false;
                        }
                    });

                    channel.VoiceChannel = null;
                }
            }
        }

        private void HandleVoiceResponse(GRP_VCH_RSP response)
        {
            foreach (ChannelBox channel in _selectedChannelsManager.GetSelectedChannels())
            {
                if (channel.SystemName == PLAYBACKSYS || channel.ChannelName == PLAYBACKCHNAME || channel.DstId == PLAYBACKTG)
                    continue;

                Codeplug.System system = Codeplug.GetSystemForChannel(channel.ChannelName);
                Codeplug.Channel cpgChannel = Codeplug.GetChannelByName(channel.ChannelName);
                IPeer handler = _webSocketManager.GetWebSocketHandler(system.Name);

                if (channel.PttState && response.Status == (int)ResponseType.GRANT && response.Channel != null && response.SrcId == system.Rid && response.DstId == cpgChannel.Tgid)
                {
                    channel.VoiceChannel = response.Channel;
                } else if (response.Status == (int)ResponseType.GRANT && response.SrcId != system.Rid && response.DstId == cpgChannel.Tgid)
                {
                    channel.VoiceChannel = response.Channel;
                    channel.LastSrcId = "Last SRC: " + response.SrcId;
                    Dispatcher.Invoke(() =>
                    {
                        channel.Background = (Brush)new BrushConverter().ConvertFrom("#FF00BC48");
                        channel.IsReceiving = true;
                    });
                } else if ((channel.HoldState || channel.PageState) && response.Status == (int)ResponseType.GRANT && response.Channel != null && response.SrcId == system.Rid && response.DstId == cpgChannel.Tgid)
                {
                    channel.VoiceChannel = response.Channel;
                }
                else
                {
                    //Dispatcher.Invoke(() =>
                    //{
                    //    if (channel.IsSelected)
                    //        channel.Background = new SolidColorBrush(Colors.DodgerBlue);
                    //    else
                    //        channel.Background = new SolidColorBrush(Colors.Gray);
                    //});

                    //channel.VoiceChannel = null;
                    //_stopSending = true;
                }
            }
        }

        private void ChannelBox_HoldChannelButtonClicked(object sender, ChannelBox e)
        {
            if (e.SystemName == PLAYBACKSYS || e.ChannelName == PLAYBACKCHNAME || e.DstId == PLAYBACKTG)
                return;

            Codeplug.System system = Codeplug.GetSystemForChannel(e.ChannelName);
            Codeplug.Channel cpgChannel = Codeplug.GetChannelByName(e.ChannelName);
            IPeer handler = _webSocketManager.GetWebSocketHandler(system.Name);
        }

        private void ChannelBox_PageButtonClicked(object sender, ChannelBox e)
        {
            if (e.SystemName == PLAYBACKSYS || e.ChannelName == PLAYBACKCHNAME || e.DstId == PLAYBACKTG)
                return;

            Codeplug.System system = Codeplug.GetSystemForChannel(e.ChannelName);
            Codeplug.Channel cpgChannel = Codeplug.GetChannelByName(e.ChannelName);
            IPeer handler = _webSocketManager.GetWebSocketHandler(system.Name);

            if (e.PageState)
            {
                GRP_VCH_REQ request = new GRP_VCH_REQ
                {
                    SrcId = system.Rid,
                    DstId = cpgChannel.Tgid,
                    Site = system.Site
                };

                handler.SendMessage(request.GetData());
            }
            else
            {
                GRP_VCH_RLS release = new GRP_VCH_RLS
                {
                    SrcId = system.Rid,
                    DstId = cpgChannel.Tgid,
                    Channel = e.VoiceChannel,
                    Site = system.Site
                };

                handler.SendMessage(release.GetData());
                e.VoiceChannel = null;
            }
        }

        private void ChannelBox_PTTButtonClicked(object sender, ChannelBox e)
        {
            if (e.SystemName == PLAYBACKSYS || e.ChannelName == PLAYBACKCHNAME || e.DstId == PLAYBACKTG)
                return;

            Codeplug.System system = Codeplug.GetSystemForChannel(e.ChannelName);
            Codeplug.Channel cpgChannel = Codeplug.GetChannelByName(e.ChannelName);
            IPeer handler = _webSocketManager.GetWebSocketHandler(system.Name);

            if (!e.IsSelected)
                return;

            if (e.PttState)
            {
                GRP_VCH_REQ request = new GRP_VCH_REQ
                {
                    SrcId = system.Rid,
                    DstId = cpgChannel.Tgid,
                    Site = system.Site
                };

                handler.SendMessage(request.GetData());
            }
            else
            {
                GRP_VCH_RLS release = new GRP_VCH_RLS
                {
                    SrcId = system.Rid,
                    DstId = cpgChannel.Tgid,
                    Channel = e.VoiceChannel,
                    Site = system.Site
                };

                handler.SendMessage(release.GetData());
                e.VoiceChannel = null;
            }
        }

        private void ChannelBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!isEditMode || !(sender is UIElement element)) return;

            _draggedElement = element;
            _startPoint = e.GetPosition(ChannelsCanvas);
            _offsetX = _startPoint.X - Canvas.GetLeft(_draggedElement);
            _offsetY = _startPoint.Y - Canvas.GetTop(_draggedElement);
            _isDragging = true;

            element.CaptureMouse();
        }

        private const int GridSize = 5;

        private void ChannelBox_MouseMove(object sender, MouseEventArgs e)
        {
            if (!isEditMode || !_isDragging || _draggedElement == null) return;

            Point currentPosition = e.GetPosition(ChannelsCanvas);

            // Calculate the new position with snapping to the grid
            double newLeft = Math.Round((currentPosition.X - _offsetX) / GridSize) * GridSize;
            double newTop = Math.Round((currentPosition.Y - _offsetY) / GridSize) * GridSize;

            // Ensure the box stays within canvas bounds
            newLeft = Math.Max(0, Math.Min(newLeft, ChannelsCanvas.ActualWidth - _draggedElement.RenderSize.Width));
            newTop = Math.Max(0, Math.Min(newTop, ChannelsCanvas.ActualHeight - _draggedElement.RenderSize.Height));

            // Apply snapped position
            Canvas.SetLeft(_draggedElement, newLeft);
            Canvas.SetTop(_draggedElement, newTop);

            // Save the new position if it's a ChannelBox
            if (_draggedElement is ChannelBox channelBox)
            {
                _settingsManager.UpdateChannelPosition(channelBox.ChannelName, newLeft, newTop);
            }

            AdjustCanvasHeight();
        }

        private void ChannelBox_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!isEditMode || !_isDragging || _draggedElement == null) return;

            _isDragging = false;
            _draggedElement.ReleaseMouseCapture();
            _draggedElement = null;
        }

        private void SystemStatusBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => ChannelBox_MouseLeftButtonDown(sender, e);
        private void SystemStatusBox_MouseMove(object sender, MouseEventArgs e) => ChannelBox_MouseMove(sender, e);

        private void SystemStatusBox_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!isEditMode) return;

            if (sender is SystemStatusBox systemStatusBox)
            {
                double x = Canvas.GetLeft(systemStatusBox);
                double y = Canvas.GetTop(systemStatusBox);
                _settingsManager.SystemStatusPositions[systemStatusBox.SystemName] = new ChannelPosition { X = x, Y = y };

                ChannelBox_MouseRightButtonDown(sender, e);

                AdjustCanvasHeight();
            }
        }

        private void ToggleEditMode_Click(object sender, RoutedEventArgs e)
        {
            isEditMode = !isEditMode;
            var menuItem = (MenuItem)sender;
            menuItem.Header = isEditMode ? "Disable Edit Mode" : "Enable Edit Mode";
            UpdateEditModeForWidgets();
        }

        private void UpdateEditModeForWidgets()
        {
            foreach (var child in ChannelsCanvas.Children)
            {
                if (child is AlertTone alertTone)
                {
                    alertTone.IsEditMode = isEditMode;
                }

                if (child is ChannelBox channelBox)
                {
                    channelBox.IsEditMode = isEditMode;
                }
            }
        }

        private void AddAlertTone_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "WAV Files (*.wav)|*.wav|All Files (*.*)|*.*",
                Title = "Select Alert Tone"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                string alertFilePath = openFileDialog.FileName;
                var alertTone = new AlertTone(alertFilePath)
                {
                    IsEditMode = isEditMode
                };

                alertTone.OnAlertTone += SendAlertTone;

                if (_settingsManager.AlertTonePositions.TryGetValue(alertFilePath, out var position))
                {
                    Canvas.SetLeft(alertTone, position.X);
                    Canvas.SetTop(alertTone, position.Y);
                }
                else
                {
                    Canvas.SetLeft(alertTone, 20);
                    Canvas.SetTop(alertTone, 20);
                }

                alertTone.MouseRightButtonUp += AlertTone_MouseRightButtonUp;

                ChannelsCanvas.Children.Add(alertTone);
                _settingsManager.UpdateAlertTonePaths(alertFilePath);

                AdjustCanvasHeight();
            }
        }

        private void AlertTone_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!isEditMode) return;

            if (sender is AlertTone alertTone)
            {
                double x = Canvas.GetLeft(alertTone);
                double y = Canvas.GetTop(alertTone);
                _settingsManager.UpdateAlertTonePosition(alertTone.AlertFilePath, x, y);

                AdjustCanvasHeight();
            }
        }

        private void AdjustCanvasHeight()
        {
            double maxBottom = 0;

            foreach (UIElement child in ChannelsCanvas.Children)
            {
                double childBottom = Canvas.GetTop(child) + child.RenderSize.Height;
                if (childBottom > maxBottom)
                {
                    maxBottom = childBottom;
                }
            }

            ChannelsCanvas.Height = maxBottom + 150;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_settingsManager.LastCodeplugPath) && File.Exists(_settingsManager.LastCodeplugPath))
            {
                LoadCodeplug(_settingsManager.LastCodeplugPath);
            }
            else
            {
                GenerateChannelWidgets();
            }
        }

        private async void OnHoldTimerElapsed(object sender, ElapsedEventArgs e)
        {
            foreach (ChannelBox channel in _selectedChannelsManager.GetSelectedChannels())
            {
                if (channel.SystemName == PLAYBACKSYS || channel.ChannelName == PLAYBACKCHNAME || channel.DstId == PLAYBACKTG)
                    continue;

                Codeplug.System system = Codeplug.GetSystemForChannel(channel.ChannelName);
                Codeplug.Channel cpgChannel = Codeplug.GetChannelByName(channel.ChannelName);
                IPeer handler = _webSocketManager.GetWebSocketHandler(system.Name);

                if (channel.HoldState && !channel.IsReceiving && !channel.PttState && !channel.PageState)
                {
                    //Task.Factory.StartNew(async () =>
                    //{
                        Console.WriteLine("Sending channel hold beep");

                        Dispatcher.Invoke(() => { channel.PttButton.Background = channel.redGradient; });
                        
                        GRP_VCH_REQ req = new GRP_VCH_REQ
                        {
                            SrcId = system.Rid,
                            DstId = cpgChannel.Tgid,
                            Site = system.Site
                        };

                        handler.SendMessage(req.GetData());

                        await Task.Delay(1000);

                        SendAlertTone("hold.wav", true);
                   // });
                }
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            _settingsManager.SaveSettings();
            base.OnClosing(e);
        }

        private void ClearEmergency_Click(object sender, RoutedEventArgs e)
        {
            _emergencyAlertPlayback.Stop();
            _flashingManager.Stop();

            foreach (ChannelBox channel in _selectedChannelsManager.GetSelectedChannels())
            {
                channel.Emergency = false;
            }
        }

        private void btnAlert1_Click(object sender, RoutedEventArgs e)
        {
            Dispatcher.Invoke(() => {
                SendAlertTone("alert1.wav");
            });
        }

        private void btnAlert2_Click(object sender, RoutedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                SendAlertTone("alert2.wav");
            });
        }

        private void btnAlert3_Click(object sender, RoutedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                SendAlertTone("alert3.wav");
            });
        }

        private void btnGlobalPtt_Click(object sender, RoutedEventArgs e)
        {
            globalPttState = !globalPttState;

            foreach (ChannelBox channel in _selectedChannelsManager.GetSelectedChannels())
            {
                if (channel.SystemName == PLAYBACKSYS || channel.ChannelName == PLAYBACKCHNAME || channel.DstId == PLAYBACKTG)
                    continue;

                Codeplug.System system = Codeplug.GetSystemForChannel(channel.ChannelName);
                Codeplug.Channel cpgChannel = Codeplug.GetChannelByName(channel.ChannelName);
                IPeer handler = _webSocketManager.GetWebSocketHandler(system.Name);

                if (!channel.IsSelected)
                    return;

                if (globalPttState)
                {
                    Dispatcher.Invoke(() =>
                    {
                        btnGlobalPtt.Background = channel.redGradient;
                    });


                    GRP_VCH_REQ request = new GRP_VCH_REQ
                    {
                        SrcId = system.Rid,
                        DstId = cpgChannel.Tgid,
                        Site = system.Site
                    };

                    channel.PttState = true;

                    handler.SendMessage(request.GetData());
                } else
                {
                    Dispatcher.Invoke(() =>
                    {
                        btnGlobalPtt.Background = channel.grayGradient;
                    });

                    GRP_VCH_RLS release = new GRP_VCH_RLS
                    {
                        SrcId = system.Rid,
                        DstId = cpgChannel.Tgid,
                        Site = system.Site
                    };

                    channel.PttState = false;

                    handler.SendMessage(release.GetData());
                }
            }
        }

        private void Button_Click(object sender, RoutedEventArgs e) { /* sub */ }
    }
}
