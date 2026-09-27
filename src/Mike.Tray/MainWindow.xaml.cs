using System;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Forms;
using Mike.Common;

namespace Mike.Tray
{
    public partial class MainWindow : Window
    {
        private NotifyIcon? _notifyIcon;

        public MainWindow()
        {
            InitializeComponent();
            InitializeTray();
        }

        private void InitializeTray()
        {
            _notifyIcon = new NotifyIcon
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
                Text = MikeConstants.AppName,
                Visible = true
            };

            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("Open Mike Local", null, (s, e) => OpenApp());
            contextMenu.Items.Add("Settings", null, (s, e) => { });
            contextMenu.Items.Add("-");
            contextMenu.Items.Add("Exit", null, (s, e) => ExitApp());

            _notifyIcon.ContextMenuStrip = contextMenu;
            _notifyIcon.DoubleClick += (s, e) => OpenApp();
        }

        private void OpenApp()
        {
            string desktopExe = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "desktop", "Mike.Desktop.exe"));
            if (File.Exists(desktopExe))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(desktopExe) { UseShellExecute = true });
        }

        private void ExitApp()
        {
            _notifyIcon?.Dispose();
            System.Windows.Application.Current.Shutdown();
        }

        protected override void OnClosed(EventArgs e)
        {
            _notifyIcon?.Dispose();
            base.OnClosed(e);
        }
    }
}
