using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using WhackerLinkLib.Models.Radio;

namespace WhackerLinkConsoleV2
{
    /// <summary>
    /// Interaction logic for DigitalPageWindow.xaml
    /// </summary>
    public partial class DigitalPageWindow : Window
    {
        public List<Codeplug.System> systems = new List<Codeplug.System>();

        public string DstId = string.Empty;
        public Codeplug.System RadioSystem = null;

        public DigitalPageWindow(List<Codeplug.System> systems)
        {
            InitializeComponent();
            this.systems = systems;

            SystemCombo.DisplayMemberPath = "Name";
            SystemCombo.ItemsSource = systems;
            SystemCombo.SelectedIndex = 0;
        }

        private void SendPageButton_Click(object sender, RoutedEventArgs e)
        {
            RadioSystem = SystemCombo.SelectedItem as Codeplug.System;
            DstId = DstIdText.Text;
            DialogResult = true;
            Close();
        }
    }
}
