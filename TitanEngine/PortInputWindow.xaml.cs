using System;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;

namespace TitanEngine
{
    public partial class PortInputWindow : Window
    {
        public string SelectedPort { get; set; } = "5555";
        public string LocalIp { get; private set; } = "localhost";

        public PortInputWindow()
        {
            InitializeComponent();
            UpdatePreview();
        }

        private void TxtPort_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            if (lblPreview != null && txtPort != null)
            {
                lblPreview.Text = $"Preview: http://{LocalIp}:{txtPort.Text}/";
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtPort.Text))
            {
                MessageBox.Show("Please enter a valid port.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (!int.TryParse(txtPort.Text, out int port) || port < 1 || port > 65535)
            {
                MessageBox.Show("Port must be a number between 1 and 65535.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Store the PARSED value, not the raw text: int.TryParse accepts surrounding
            // whitespace and a sign (" 5555 "), which would pass validation but produce an
            // invalid HttpListener prefix ("http://localhost: 5555/") downstream.
            SelectedPort = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            DialogResult = true;
            Close();
        }
    }
}
