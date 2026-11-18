using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using HS_Reconnector.Core;
using HS_Reconnector.Native;
using Localization = HS_Reconnector.Core.Localization;

namespace HS_Reconnector.UI
{
    public partial class OverlayWindow : Window
    {
        private const double BaseOverlayWidth = 176;
        private const double BaseOverlayHeight = 46;

        private static readonly Brush ReadyBg = CreateFrozenBrush(0xFF, 0x23, 0x27, 0x2D);
        private static readonly Brush HoverBg = CreateFrozenBrush(0xFF, 0x2E, 0x34, 0x3C);
        private static readonly Brush IdleBg = CreateFrozenBrush(0xFF, 0x1A, 0x1D, 0x22);
        private static readonly Brush ReadyBorder = CreateFrozenBrush(0xFF, 0x4E, 0x55, 0x5E);
        private static readonly Brush HoverBorder = CreateFrozenBrush(0xFF, 0x6B, 0x74, 0x80);
        private static readonly Brush IdleBorder = CreateFrozenBrush(0xFF, 0x3E, 0x43, 0x4A);
        private static readonly Brush DisconnectedBorder = CreateFrozenBrush(0xFF, 0xC9, 0x92, 0x2E);
        private static readonly Brush ReadyText = CreateFrozenBrush(0xFF, 0xFF, 0xFF, 0xFF);
        private static readonly Brush DisconnectedText = CreateFrozenBrush(0xFF, 0xFF, 0xD0, 0x66);
        private static readonly Brush CooldownText = CreateFrozenBrush(0xFF, 0xB2, 0xBB, 0xC7);
        private static readonly Brush MutedText = CreateFrozenBrush(0xFF, 0x8D, 0x96, 0xA3);

        private static Brush CreateFrozenBrush(byte a, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }

        private readonly DispatcherTimer foregroundTimer;
        private bool isGameInForeground;
        private bool isDragging;
        private bool isHovered;
        private Point dragStartScreen;
        private Point dragStartWindow;
        private ControllerSnapshot lastSnapshot;

        public event Action ReconnectRequested;

        public OverlayWindow()
        {
            InitializeComponent();
            foregroundTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            foregroundTimer.Tick += (s, e) =>
            {
                var focused = HearthstoneLocator.IsHearthstoneInForeground();
                if (focused != isGameInForeground)
                {
                    isGameInForeground = focused;
                    UpdateVisibilityAndVisuals();
                }
            };
            Closed += (s, e) => foregroundTimer.Stop();
            SourceInitialized += OverlayWindow_SourceInitialized;
            Loaded += (s, e) => ApplySettingsAndPosition();
            RootBorder.LostMouseCapture += (s, e) => isDragging = false;
        }

        private void OverlayWindow_SourceInitialized(object sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            var exStyle = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
            Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, exStyle | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW);
        }

        public void ApplySettingsAndPosition()
        {
            var settings = AppSettings.Instance;
            Opacity = settings.OverlayOpacityPercent / 100.0;
            BackgroundPlate.Opacity = settings.OverlayBackgroundOpacityPercent / 100.0;
            var scale = settings.OverlayScalePercent / 100.0;
            OverlayScaleTransform.ScaleX = scale;
            OverlayScaleTransform.ScaleY = scale;

            RootBorder.ToolTip = settings.LockOverlayPosition
                ? Localization.Get("OverlayTooltipLocked")
                : Localization.Get("OverlayTooltip");

            EnsureValidPosition();
            UpdateForegroundTracking();
            UpdateVisibilityAndVisuals();
        }

        internal void UpdateState(ControllerSnapshot snapshot)
        {
            lastSnapshot = snapshot;
            UpdateForegroundTracking();
            UpdateVisibilityAndVisuals();
        }

        private void UpdateForegroundTracking()
        {
            var settings = AppSettings.Instance;
            var trackForeground = settings.EnableOverlay && settings.ShowOverlayOnlyWhenGameFocused
                && lastSnapshot?.IsHearthstoneRunning == true;
            foregroundTimer.IsEnabled = trackForeground;
            isGameInForeground = trackForeground && HearthstoneLocator.IsHearthstoneInForeground();
        }

        private void UpdateVisibilityAndVisuals()
        {
            var settings = AppSettings.Instance;
            if (!settings.EnableOverlay || lastSnapshot == null || !lastSnapshot.IsHearthstoneRunning
                || (settings.ShowOverlayOnlyWhenGameFocused && !isGameInForeground))
            {
                if (IsVisible)
                    Hide();
                return;
            }

            var hasActiveMatch = lastSnapshot.EndpointUnsupported
                || (!string.IsNullOrWhiteSpace(lastSnapshot.RemoteAddress) && lastSnapshot.RemotePort != 0);
            if (settings.ShowOverlayOnlyInMatch && !hasActiveMatch)
            {
                if (IsVisible)
                    Hide();
                return;
            }

            switch (lastSnapshot.VisualState)
            {
                case ReconnectVisualState.Ready:
                    StatusText.Text = Localization.Get("BtnReconnect");
                    InnerBorder.Background = isHovered ? HoverBg : ReadyBg;
                    InnerBorder.BorderBrush = isHovered ? HoverBorder : ReadyBorder;
                    StatusText.Fill = ReadyText;
                    RootBorder.Cursor = Cursors.Hand;
                    break;
                case ReconnectVisualState.Disconnected:
                    StatusText.Text = Localization.Get("BtnDisconnected");
                    InnerBorder.Background = IdleBg;
                    InnerBorder.BorderBrush = DisconnectedBorder;
                    StatusText.Fill = DisconnectedText;
                    RootBorder.Cursor = Cursors.Arrow;
                    break;
                case ReconnectVisualState.Cooldown:
                    StatusText.Text = Localization.Format("BtnWait", lastSnapshot.CooldownRemainingSeconds);
                    InnerBorder.Background = IdleBg;
                    InnerBorder.BorderBrush = IdleBorder;
                    StatusText.Fill = CooldownText;
                    RootBorder.Cursor = Cursors.Arrow;
                    break;
                case ReconnectVisualState.Unsupported:
                    StatusText.Text = Localization.Get("OverlayUnsupported");
                    InnerBorder.Background = IdleBg;
                    InnerBorder.BorderBrush = IdleBorder;
                    StatusText.Fill = MutedText;
                    RootBorder.Cursor = Cursors.Arrow;
                    break;
                default:
                    StatusText.Text = Localization.Get("BtnReconnect");
                    InnerBorder.Background = IdleBg;
                    InnerBorder.BorderBrush = IdleBorder;
                    StatusText.Fill = MutedText;
                    RootBorder.Cursor = Cursors.Arrow;
                    break;
            }

            if (!IsVisible)
            {
                EnsureValidPosition();
                Show();
            }
        }

        public void AdjustPositionForScaleChange(int oldScalePercent, int newScalePercent)
        {
            if (oldScalePercent == newScalePercent)
                return;

            var settings = AppSettings.Instance;
            if (double.IsNaN(settings.OverlayLeft) || double.IsNaN(settings.OverlayTop))
                return;

            var oldScale = oldScalePercent / 100.0;
            var newScale = newScalePercent / 100.0;
            var oldWidth = BaseOverlayWidth * oldScale;
            var oldHeight = BaseOverlayHeight * oldScale;
            var newWidth = BaseOverlayWidth * newScale;
            var newHeight = BaseOverlayHeight * newScale;

            var minLeft = SystemParameters.VirtualScreenLeft;
            var minTop = SystemParameters.VirtualScreenTop;
            var oldMaxLeft = Math.Max(minLeft, minLeft + SystemParameters.VirtualScreenWidth - oldWidth);
            var oldMaxTop = Math.Max(minTop, minTop + SystemParameters.VirtualScreenHeight - oldHeight);
            var clampedOldLeft = Math.Max(minLeft, Math.Min(oldMaxLeft, settings.OverlayLeft));
            var clampedOldTop = Math.Max(minTop, Math.Min(oldMaxTop, settings.OverlayTop));

            var centerX = clampedOldLeft + oldWidth / 2.0;
            var centerY = clampedOldTop + oldHeight / 2.0;

            var newMaxLeft = Math.Max(minLeft, minLeft + SystemParameters.VirtualScreenWidth - newWidth);
            var newMaxTop = Math.Max(minTop, minTop + SystemParameters.VirtualScreenHeight - newHeight);

            settings.OverlayLeft = Math.Max(minLeft, Math.Min(newMaxLeft, centerX - newWidth / 2.0));
            settings.OverlayTop = Math.Max(minTop, Math.Min(newMaxTop, centerY - newHeight / 2.0));
        }

        private void EnsureValidPosition()
        {
            var settings = AppSettings.Instance;
            var scale = settings.OverlayScalePercent / 100.0;
            var width = BaseOverlayWidth * scale;
            var height = BaseOverlayHeight * scale;

            if (double.IsNaN(settings.OverlayLeft) || double.IsNaN(settings.OverlayTop))
                settings.ResetOverlayPosition(width, height);

            var minLeft = SystemParameters.VirtualScreenLeft;
            var minTop = SystemParameters.VirtualScreenTop;
            var maxLeft = Math.Max(minLeft, minLeft + SystemParameters.VirtualScreenWidth - width);
            var maxTop = Math.Max(minTop, minTop + SystemParameters.VirtualScreenHeight - height);

            Left = Math.Max(minLeft, Math.Min(maxLeft, settings.OverlayLeft));
            Top = Math.Max(minTop, Math.Min(maxTop, settings.OverlayTop));
        }

        private void RootBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (lastSnapshot != null && lastSnapshot.CanReconnect)
                ReconnectRequested?.Invoke();
        }

        private void RootBorder_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (AppSettings.Instance.LockOverlayPosition)
                return;

            isDragging = true;
            dragStartScreen = PointToScreen(e.GetPosition(this));
            dragStartWindow = new Point(Left, Top);
            RootBorder.CaptureMouse();
            e.Handled = true;
        }

        private void RootBorder_MouseMove(object sender, MouseEventArgs e)
        {
            if (!isDragging)
                return;

            var currentScreen = PointToScreen(e.GetPosition(this));
            var source = PresentationSource.FromVisual(this);
            var dpiX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
            var dpiY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;

            var dx = (currentScreen.X - dragStartScreen.X) * dpiX;
            var dy = (currentScreen.Y - dragStartScreen.Y) * dpiY;

            var scale = AppSettings.Instance.OverlayScalePercent / 100.0;
            var width = BaseOverlayWidth * scale;
            var height = BaseOverlayHeight * scale;
            var minLeft = SystemParameters.VirtualScreenLeft;
            var minTop = SystemParameters.VirtualScreenTop;
            var maxLeft = Math.Max(minLeft, minLeft + SystemParameters.VirtualScreenWidth - width);
            var maxTop = Math.Max(minTop, minTop + SystemParameters.VirtualScreenHeight - height);

            Left = Math.Max(minLeft, Math.Min(maxLeft, dragStartWindow.X + dx));
            Top = Math.Max(minTop, Math.Min(maxTop, dragStartWindow.Y + dy));
        }

        private void RootBorder_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!isDragging)
                return;

            isDragging = false;
            RootBorder.ReleaseMouseCapture();
            AppSettings.Instance.OverlayLeft = Left;
            AppSettings.Instance.OverlayTop = Top;
            AppSettings.Save();
            e.Handled = true;
        }

        private void RootBorder_MouseEnter(object sender, MouseEventArgs e)
        {
            isHovered = true;
            UpdateVisibilityAndVisuals();
        }

        private void RootBorder_MouseLeave(object sender, MouseEventArgs e)
        {
            isHovered = false;
            UpdateVisibilityAndVisuals();
        }
    }
}
