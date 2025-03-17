using System;
using System.Collections.ObjectModel;
using System.Windows;
using WhackerLinkConsoleV2.Controls;
using WhackerLinkLib.Models.Radio;

namespace WhackerLinkConsoleV2
{
    public partial class KeyStatusWindow : Window
    {
        public ObservableCollection<KeyStatusItem> KeyStatusItems { get; private set; } = new ObservableCollection<KeyStatusItem>();

        private Codeplug Codeplug;
        private MainWindow mainWindow;

        public KeyStatusWindow(Codeplug codeplug, MainWindow mainWindow)
        {
            InitializeComponent();
            this.Codeplug = codeplug;
            this.mainWindow = mainWindow;
            DataContext = this;

            LoadKeyStatus();
        }

        private void LoadKeyStatus()
        {
            Dispatcher.Invoke(() =>
            {
                KeyStatusItems.Clear();

                foreach (var child in mainWindow.ChannelsCanvas.Children)
                {
                    if (child == null)
                    {
                        Console.WriteLine("A child in ChannelsCanvas.Children is null.");
                        continue;
                    }

                    if (!(child is ChannelBox channelBox))
                    {
                        continue;
                    }

                    Codeplug.System system = Codeplug.GetSystemForChannel(channelBox.ChannelName);
                    if (system == null)
                    {
                        Console.WriteLine($"System not found for {channelBox.ChannelName}");
                        continue;
                    }

                    Codeplug.Channel cpgChannel = Codeplug.GetChannelByName(channelBox.ChannelName);
                    if (cpgChannel == null)
                    {
                        Console.WriteLine($"Channel not found for {channelBox.ChannelName}");
                        continue;
                    }

                    if (!system.IsDvm)
                        continue;

                    if (cpgChannel.GetKeyId() == 0 || cpgChannel.GetAlgoId() == 0)
                        continue;

                    if (channelBox.crypter == null)
                    {
                        Console.WriteLine($"Crypter is null for channel {channelBox.ChannelName}");
                        continue;
                    }

                    bool hasKey = channelBox.crypter.HasKey(cpgChannel.GetKeyId());

                    KeyStatusItems.Add(new KeyStatusItem
                    {
                        ChannelName = channelBox.ChannelName,
                        AlgId = $"0x{cpgChannel.GetAlgoId():X2}",
                        KeyId = $"0x{cpgChannel.GetKeyId():X4}",
                        KeyStatus = hasKey ? "Key Available" : "No Key"
                    });
                }
            });
        }
    }

    public class KeyStatusItem
    {
        public string ChannelName { get; set; }
        public string AlgId { get; set; }
        public string KeyId { get; set; }
        public string KeyStatus { get; set; }
    }
}
