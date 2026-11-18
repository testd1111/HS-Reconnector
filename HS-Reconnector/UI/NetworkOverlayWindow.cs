using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using HS_Reconnector.Core;
using HS_Reconnector.Native;
using Localization = HS_Reconnector.Core.Localization;

namespace HS_Reconnector.UI
{
    internal sealed class NetworkOverlayWindow : Window
    {
        private readonly Border plate = new Border { Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(4) };
        private readonly TextBlock text = new TextBlock();
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        private readonly Queue<bool> history = new Queue<bool>();
        private const int WsExTransparent = 0x20;
        private const double SnapDistance = 16;
        private const double OverlayGap = 4;
        private static readonly FontFamily PingDigitsFont = new FontFamily("Consolas");
        private readonly OverlayWindow reconnectOverlay;
        private ControllerSnapshot snapshot;
        private NetworkOverlaySettings preview;
        private DateTime previewUntil;
        private DateTime? simulationStarted;
        private int simulationPhase;
        private DateTime nextCheck;
        private DateTime nextRegionCheck;
        private string region;
        private long? latency;
        private bool checking;
        private bool closed;
        private int generation;
        private Point? dragOrigin;
        private Point windowOrigin;

        private NetworkOverlaySettings Settings => preview ?? AppSettings.Instance.NetworkOverlay;

        public NetworkOverlayWindow(OverlayWindow reconnectOverlay)
        {
            this.reconnectOverlay = reconnectOverlay;
            reconnectOverlay.PositionUpdated += ReconnectPositionChanged;
            reconnectOverlay.PositionCommitted += SnapAfterReconnectMove;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowActivated = false;
            Focusable = false;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            plate.Child = text;
            Content = plate;
            SourceInitialized += (sender, args) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE)
                    | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW);
                UpdateClickThrough();
            };
            timer.Tick += (sender, args) => Refresh();
            plate.MouseRightButtonDown += StartDrag;
            plate.MouseMove += MoveDrag;
            plate.MouseRightButtonUp += EndDrag;
            plate.LostMouseCapture += (sender, args) => dragOrigin = null;
            Closed += (sender, args) =>
            {
                closed = true;
                generation++;
                timer.Stop();
                reconnectOverlay.PositionUpdated -= ReconnectPositionChanged;
                reconnectOverlay.PositionCommitted -= SnapAfterReconnectMove;
            };
        }

        public void UpdateState(ControllerSnapshot next)
        {
            var changed = snapshot?.RemoteAddress != next.RemoteAddress || snapshot?.RemotePort != next.RemotePort
                || snapshot?.ActiveLogPath != next.ActiveLogPath;
            if (changed)
                ResetMeasurement();
            snapshot = next;
            Refresh(changed);
        }

        public void ApplySettings()
        {
            ClearPreview();
            ResetMeasurement();
            Refresh(true);
        }

        public void TogglePreview(NetworkOverlaySettings draft)
        {
            if (preview == null)
            {
                preview = draft;
                RestartPreviewTimer();
            }
            else if (!simulationStarted.HasValue)
            {
                simulationStarted = DateTime.UtcNow;
                previewUntil = simulationStarted.Value.AddSeconds(6);
            }
            else
            {
                ClearPreview();
            }
            Refresh(true);
        }

        private void ClearPreview()
        {
            preview = null;
            simulationStarted = null;
            simulationPhase = 0;
        }

        public bool IsPreviewActive => preview != null;

        public void UpdatePreview(NetworkOverlaySettings draft)
        {
            preview = draft;
            RestartPreviewTimer();
            Refresh(true);
        }

        private void RestartPreviewTimer()
        {
            if (!simulationStarted.HasValue)
                previewUntil = DateTime.UtcNow.AddSeconds(20);
        }

        public void RefreshLanguage() => Refresh(true);

        public void ResetPosition()
        {
            var saved = AppSettings.Instance.NetworkOverlay;
            saved.Left = saved.Top = double.NaN;
            saved.AttachedToReconnect = true;
            if (preview != null)
            {
                preview.Left = preview.Top = double.NaN;
                preview.AttachedToReconnect = true;
                RestartPreviewTimer();
            }
            AppSettings.Save();
            Refresh(true);
        }

        private void ResetMeasurement()
        {
            generation++;
            history.Clear();
            latency = null;
            region = null;
            nextCheck = DateTime.MinValue;
            nextRegionCheck = DateTime.MinValue;
        }

        private void Refresh(bool redraw = false)
        {
            if (closed)
                return;

            var now = DateTime.UtcNow;
            if (preview != null && now >= previewUntil)
            {
                ClearPreview();
                redraw = true;
            }
            if (simulationStarted.HasValue)
            {
                var phase = 1 + (int)((now - simulationStarted.Value).TotalSeconds / 2);
                redraw |= phase != simulationPhase;
                simulationPhase = phase;
            }

            var active = Settings.Enabled && snapshot?.IsHearthstoneRunning == true
                && !string.IsNullOrEmpty(snapshot.RemoteAddress) && snapshot.RemotePort != 0;
            timer.IsEnabled = active || preview != null;
            if (preview == null && (!active || !HearthstoneLocator.IsHearthstoneInForeground()))
            {
                if (IsVisible)
                    Hide();
                return;
            }

            if (redraw || !IsVisible)
                Draw();
            if (preview == null && !checking && now >= nextCheck
                && snapshot.ConnectionStatus != ConnectionStatus.Disconnected
                && IPAddress.TryParse(snapshot.RemoteAddress, out var address))
                Measure(address, snapshot.RemotePort, snapshot.ActiveLogPath);
        }

        private async void Measure(IPAddress address, ushort port, string logPath)
        {
            checking = true;
            nextCheck = DateTime.UtcNow.AddSeconds(Settings.Interval);
            var request = generation;
            try
            {
                var regionTask = Task.FromResult(region);
                if (Settings.ShowRegion && Settings.Region == "Auto" && region == null
                    && DateTime.UtcNow >= nextRegionCheck)
                {
                    nextRegionCheck = DateTime.UtcNow.AddSeconds(15);
                    regionTask = Task.Run(() => NetworkProbe.ReadRegion(logPath));
                }
                var result = await NetworkProbe.MeasureAsync(address, port);
                var detectedRegion = await regionTask;
                if (request != generation || closed)
                    return;

                region = detectedRegion;
                latency = result;
                history.Enqueue(result.HasValue);
                if (history.Count > 10)
                    history.Dequeue();
            }
            finally
            {
                checking = false;
            }
            Refresh(true);
        }

        private void Draw()
        {
            var settings = Settings;
            var sample = preview != null;
            var ms = sample ? GetSampleLatency(simulationPhase) : latency;
            var failed = sample ? (simulationPhase >= 2 ? 30 : 0) : history.Count == 0 ? -1
                : 100 * history.Count(success => !success) / history.Count;
            var selectedRegion = settings.Region == "Auto" ? (sample ? "EU" : region) : settings.Region;
            var parts = new List<string>();
            if (settings.ShowRegion)
                parts.Add((settings.Compact ? "" : Localization.Get("NetworkRegion") + ": ")
                    + (string.IsNullOrEmpty(selectedRegion) ? "—" : selectedRegion));
            if (settings.ShowFailedChecks && (!settings.HideZeroFailures || failed > 0))
                parts.Add(Localization.Get("NetworkFailures") + " " + (failed < 0 ? "—" : failed + "%"));
            if (settings.ShowAddress)
                parts.Add(sample ? "192.0.2.1:1119" : snapshot.RemoteAddress + ":" + snapshot.RemotePort);

            DrawPing(ms, settings.Compact);
            for (var i = 0; i < parts.Count; i++)
            {
                var isAddress = settings.ShowAddress && i == parts.Count - 1;
                text.Inlines.Add(new Run((settings.Compact && !isAddress ? " · " : Environment.NewLine) + parts[i]));
            }
            text.Foreground = !ms.HasValue ? Brushes.LightGray : ms.Value >= 300 ? Brushes.LightCoral
                : ms.Value >= 100 ? Brushes.Gold : Brushes.LightGreen;
            text.FontSize = 13 * settings.Scale / 100.0;
            text.FontWeight = OverlayWindow.TextWeights[settings.Weight];
            Opacity = settings.Opacity / 100.0;
            plate.Background = new SolidColorBrush(Color.FromArgb((byte)(255 * settings.BackgroundOpacity / 100), 14, 16, 19));
            if (!IsVisible)
                Show();
            UpdateClickThrough();
            if (dragOrigin == null)
            {
                plate.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                if (settings.AttachedToReconnect)
                    PlaceAboveReconnect();
                else
                    Place(settings.Left, settings.Top);
            }
        }

        private void DrawPing(long? milliseconds, bool compact)
        {
            text.Inlines.Clear();
            text.Inlines.Add(new Run(compact ? "PING " : "PING: "));
            var number = milliseconds.HasValue
                ? Math.Min(999, milliseconds.Value).ToString(CultureInfo.InvariantCulture)
                : "—";
            if (number.Length < 3)
                text.Inlines.Add(new Run(new string('0', 3 - number.Length))
                    { FontFamily = PingDigitsFont, Foreground = Brushes.Transparent });
            text.Inlines.Add(new Run(number) { FontFamily = PingDigitsFont });
            if (milliseconds.HasValue)
                text.Inlines.Add(new Run(" " + Localization.Get("NetworkMilliseconds")));
        }

        private static long? GetSampleLatency(int phase)
        {
            switch (phase)
            {
                case 1: return 340;
                case 2: return null;
                default: return 42;
            }
        }

        private void ReconnectPositionChanged()
        {
            if (IsVisible && Settings.AttachedToReconnect && dragOrigin == null)
                PlaceAboveReconnect();
        }

        private void PlaceAboveReconnect()
        {
            var anchor = reconnectOverlay.OverlayBounds;
            if (double.IsNaN(anchor.Left) || double.IsNaN(anchor.Top))
                return;

            var top = anchor.Top - plate.DesiredSize.Height - OverlayGap;
            if (top < SystemParameters.VirtualScreenTop)
                top = anchor.Bottom + OverlayGap;
            Place(anchor.Left, top);
        }

        private bool NearReconnect()
        {
            var anchor = reconnectOverlay.OverlayBounds;
            if (double.IsNaN(anchor.Left) || double.IsNaN(anchor.Top))
                return false;
            anchor.Inflate(SnapDistance, SnapDistance);
            return anchor.IntersectsWith(new Rect(Left, Top, plate.DesiredSize.Width, plate.DesiredSize.Height));
        }

        private void SnapAfterReconnectMove()
        {
            if (!IsVisible || Settings.AttachedToReconnect || !NearReconnect())
                return;

            Settings.AttachedToReconnect = true;
            AppSettings.Instance.NetworkOverlay.AttachedToReconnect = true;
            PlaceAboveReconnect();
        }

        private void UpdateClickThrough()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var oldStyle = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
            var style = Settings.Locked ? oldStyle | WsExTransparent : oldStyle & ~WsExTransparent;
            if (style != oldStyle)
                Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, style);
        }

        private void Place(double left, double top)
        {
            var minLeft = SystemParameters.VirtualScreenLeft;
            var minTop = SystemParameters.VirtualScreenTop;
            Left = Math.Max(minLeft, Math.Min(minLeft + SystemParameters.VirtualScreenWidth - plate.DesiredSize.Width, left));
            Top = Math.Max(minTop, Math.Min(minTop + SystemParameters.VirtualScreenHeight - plate.DesiredSize.Height, top));
        }

        private void StartDrag(object sender, MouseButtonEventArgs e)
        {
            if (Settings.Locked)
                return;
            if (!plate.CaptureMouse())
                return;
            dragOrigin = PointToScreen(e.GetPosition(this));
            windowOrigin = new Point(Left, Top);
            e.Handled = true;
        }

        private void MoveDrag(object sender, MouseEventArgs e)
        {
            if (!dragOrigin.HasValue)
                return;
            var delta = PointToScreen(e.GetPosition(this)) - dragOrigin.Value;
            var dips = PresentationSource.FromVisual(this).CompositionTarget.TransformFromDevice.Transform(delta);
            Place(windowOrigin.X + dips.X, windowOrigin.Y + dips.Y);
        }

        private void EndDrag(object sender, MouseButtonEventArgs e)
        {
            if (!dragOrigin.HasValue)
                return;
            plate.ReleaseMouseCapture();
            Settings.Left = Left;
            Settings.Top = Top;
            var saved = AppSettings.Instance.NetworkOverlay;
            saved.Left = Left;
            saved.Top = Top;
            Settings.AttachedToReconnect = saved.AttachedToReconnect = NearReconnect();
            if (saved.AttachedToReconnect)
                PlaceAboveReconnect();
            if (preview != null)
                RestartPreviewTimer();
            AppSettings.Save();
            e.Handled = true;
        }
    }
}
