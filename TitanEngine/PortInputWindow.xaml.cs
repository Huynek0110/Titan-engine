using System;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;

namespace TitanEngine
{
    public partial class PortInputWindow : Window
    {
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern void DwmSetWindowAttribute(System.IntPtr hwnd, int attribute, ref int value, int size);

        public string SelectedPort { get; set; } = "5555";
        public string LocalIp { get; private set; } = "localhost";

        /// <summary>
        /// Same dark chrome as the main window (immersive dark title bar + rounded corners + brand
        /// icon). Purely visual, additive, silently skipped where DWM or the logo file is unavailable.
        /// </summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                string logoPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Content", "Branding", "video-logo.png");
                if (System.IO.File.Exists(logoPath))
                    Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(logoPath));
            }
            catch
            {
                // Cosmetic only - never block startup over a missing icon.
            }

            try
            {
                System.IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                int on = 1;
                DwmSetWindowAttribute(hwnd, 20, ref on, 4);   // DWMWA_USE_IMMERSIVE_DARK_MODE
                int round = 2;
                DwmSetWindowAttribute(hwnd, 33, ref round, 4); // DWMWA_WINDOW_CORNER_PREFERENCE = ROUND
            }
            catch
            {
                // Older DWM: keep the standard chrome, nothing else depends on it.
            }
        }

        public PortInputWindow()
        {
            InitializeComponent();
            if (!string.IsNullOrWhiteSpace(AppSettings.LastApiPort))
                txtPort.Text = AppSettings.LastApiPort;
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
            AppSettings.RememberApiPort(SelectedPort);
            DialogResult = true;
            Close();
        }
    }
}
