using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using HS_Reconnector.Core;
using HS_Reconnector.Native;
using Localization = HS_Reconnector.Core.Localization;

namespace HS_Reconnector.UI
{
    internal sealed class TrayIconController : IDisposable
    {
        private readonly NotifyIcon notifyIcon;
        private readonly ToolStripMenuItem openItem;
        private readonly ToolStripMenuItem reconnectItem;
        private readonly ToolStripMenuItem overlayItem;
        private readonly ToolStripMenuItem exitItem;

        private readonly Icon waitingIcon;
        private readonly Icon readyIcon;
        private readonly Icon disconnectedIcon;
        private bool disposed;

        public event Action ShowRequested;
        public event Action ReconnectRequested;
        public event Action<bool> ToggleOverlayRequested;
        public event Action ExitRequested;

        public TrayIconController()
        {
            waitingIcon = CreateStateIcon(Color.FromArgb(140, 152, 168));
            readyIcon = CreateStateIcon(Color.FromArgb(64, 214, 138));
            disconnectedIcon = CreateStateIcon(Color.FromArgb(255, 184, 56));

            var menu = new ContextMenuStrip
            {
                Renderer = new DarkMenuRenderer(),
                ShowImageMargin = false
            };

            openItem = new ToolStripMenuItem();
            openItem.Font = new Font(openItem.Font, System.Drawing.FontStyle.Bold);
            openItem.Click += (s, e) => ShowRequested?.Invoke();

            reconnectItem = new ToolStripMenuItem();
            reconnectItem.Click += (s, e) => ReconnectRequested?.Invoke();

            overlayItem = new ToolStripMenuItem { CheckOnClick = true };
            overlayItem.Click += (s, e) => ToggleOverlayRequested?.Invoke(overlayItem.Checked);

            exitItem = new ToolStripMenuItem();
            exitItem.Click += (s, e) => ExitRequested?.Invoke();

            menu.Items.Add(openItem);
            menu.Items.Add(reconnectItem);
            menu.Items.Add(overlayItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            notifyIcon = new NotifyIcon
            {
                Icon = waitingIcon,
                Text = "HS-Reconnector",
                ContextMenuStrip = menu,
                Visible = true
            };
            notifyIcon.MouseDoubleClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                    ShowRequested?.Invoke();
            };

            RefreshLocalization();
        }

        public void RefreshLocalization()
        {
            if (disposed)
                return;

            openItem.Text = Localization.Get("TrayShow");
            reconnectItem.Text = Localization.Get("TrayReconnect");
            overlayItem.Text = Localization.Get("TrayOverlay");
            exitItem.Text = Localization.Get("TrayExit");
        }

        public void UpdateState(ControllerSnapshot snapshot)
        {
            if (disposed || snapshot == null)
                return;

            overlayItem.Checked = AppSettings.Instance.EnableOverlay;
            reconnectItem.Enabled = snapshot.CanReconnect;

            string tooltip;
            Icon targetIcon;
            switch (snapshot.VisualState)
            {
                case ReconnectVisualState.Ready:
                    targetIcon = readyIcon;
                    tooltip = Localization.Format("TrayReady", snapshot.RemoteAddress + ":" + snapshot.RemotePort);
                    break;
                case ReconnectVisualState.Disconnected:
                    targetIcon = disconnectedIcon;
                    tooltip = Localization.Get("TrayDisconnected");
                    break;
                case ReconnectVisualState.Cooldown:
                    targetIcon = waitingIcon;
                    tooltip = "HS-Reconnector — " + Localization.Format("BtnWait", snapshot.CooldownRemainingSeconds);
                    break;
                case ReconnectVisualState.Unsupported:
                    targetIcon = waitingIcon;
                    tooltip = "HS-Reconnector — " + Localization.Get("OverlayUnsupported");
                    break;
                default:
                    targetIcon = waitingIcon;
                    tooltip = Localization.Get("TrayWaiting");
                    break;
            }

            if (!ReferenceEquals(notifyIcon.Icon, targetIcon))
                notifyIcon.Icon = targetIcon;

            // NotifyIcon.Text limit is 63 chars on Windows.
            notifyIcon.Text = tooltip.Length > 63 ? tooltip.Substring(0, 63) : tooltip;
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            notifyIcon.Visible = false;
            notifyIcon.ContextMenuStrip?.Dispose();
            notifyIcon.Dispose();
            waitingIcon.Dispose();
            readyIcon.Dispose();
            disconnectedIcon.Dispose();
        }

        private static Icon CreateStateIcon(Color badgeColor)
        {
            using (var baseIcon = LoadBaseIcon())
            using (var bmp = baseIcon.ToBitmap())
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var dotSize = Math.Max(8, bmp.Width / 3);
                var x = bmp.Width - dotSize - 1;
                var y = bmp.Height - dotSize - 1;
                using (var outline = new SolidBrush(Color.FromArgb(245, 16, 20, 26)))
                    g.FillEllipse(outline, x - 2, y - 2, dotSize + 4, dotSize + 4);
                using (var fill = new SolidBrush(badgeColor))
                    g.FillEllipse(fill, x, y, dotSize, dotSize);

                var hIcon = bmp.GetHicon();
                try
                {
                    return (Icon)Icon.FromHandle(hIcon).Clone();
                }
                finally
                {
                    Win32.DestroyIcon(hIcon);
                }
            }
        }

        private static Icon LoadBaseIcon()
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            var streamInfo = System.Windows.Application.GetResourceStream(uri);
            if (streamInfo != null)
            {
                using (streamInfo.Stream)
                    return new Icon(streamInfo.Stream, 32, 32);
            }
            return (Icon)SystemIcons.Application.Clone();
        }

        private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
        {
            public DarkMenuRenderer() : base(new DarkColorTable()) { }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled
                    ? Color.FromArgb(236, 241, 248)
                    : Color.FromArgb(120, 130, 145);
                base.OnRenderItemText(e);
            }
        }

        private sealed class DarkColorTable : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Color.FromArgb(28, 34, 42);
            public override Color MenuBorder => Color.FromArgb(56, 66, 80);
            public override Color MenuItemBorder => Color.FromArgb(76, 194, 255);
            public override Color MenuItemSelected => Color.FromArgb(44, 54, 68);
            public override Color SeparatorDark => Color.FromArgb(54, 64, 76);
            public override Color SeparatorLight => Color.FromArgb(54, 64, 76);
            public override Color CheckBackground => Color.FromArgb(44, 54, 68);
            public override Color CheckSelectedBackground => Color.FromArgb(58, 72, 90);
        }
    }
}
