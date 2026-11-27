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
        private readonly OverlayWindow reconnectOverlay;
        private ControllerSnapshot snapshot;
        private NetworkOverlaySettings preview;
        private bool? previewSnapOverlays;
        private DateTime previewUntil;
        private DateTime? simulationStarted;
        private int simulationPhase;
        private DateTime nextCheck;
        private DateTime nextRegionCheck;
        private NetworkProbe.RegionLogScanner regionScanner = new NetworkProbe.RegionLogScanner();
        private string region;
        private long? latency;
        private bool checking;
        private bool closed;
        private bool hasRendered;
        private int generation;
        private Point? dragOrigin;
        private Point windowOrigin;

        private NetworkOverlaySettings Settings => preview ?? AppSettings.Instance.NetworkOverlay;
        private bool IsSnapEnabled => previewSnapOverlays
            ?? (reconnectOverlay.IsPreviewActive ? reconnectOverlay.IsSnapEnabled : AppSettings.Instance.SnapOverlays);

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
            plate.LostMouseCapture += (sender, args) => CancelDrag();
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

        public void TogglePreview(NetworkOverlaySettings draft, bool snapOverlays)
        {
            if (preview == null)
            {
                previewSnapOverlays = snapOverlays;
                if (!snapOverlays)
                    DetachFromReconnect(draft);
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
            previewSnapOverlays = null;
            simulationStarted = null;
            simulationPhase = 0;
        }

        public bool IsPreviewActive => preview != null;

        public void UpdatePreview(NetworkOverlaySettings draft, bool snapOverlays)
        {
            previewSnapOverlays = snapOverlays;
            if (!snapOverlays)
            {
                if (preview != null && !preview.AttachedToReconnect
                    && !double.IsNaN(preview.Left) && !double.IsNaN(preview.Top))
                {
                    draft.Left = preview.Left;
                    draft.Top = preview.Top;
                    draft.AttachedToReconnect = false;
                }
                else
                {
                    DetachFromReconnect(draft);
                }
            }
            else if (preview != null && preview.AttachedToReconnect)
            {
                draft.AttachedToReconnect = true;
            }
            preview = draft;
            RestartPreviewTimer();
            Refresh(true);
        }

        public void DetachFromReconnect(NetworkOverlaySettings target)
        {
            if (target == null || !target.AttachedToReconnect)
                return;

            target.AttachedToReconnect = false;
            if (preview != null && !preview.AttachedToReconnect
                && !double.IsNaN(preview.Left) && !double.IsNaN(preview.Top))
            {
                target.Left = ClampLeft(preview.Left);
                target.Top = ClampTop(preview.Top);
            }
            else if (IsVisible && !double.IsNaN(Left) && !double.IsNaN(Top))
            {
                target.Left = ClampLeft(Left);
                target.Top = ClampTop(Top);
            }
            else if (hasRendered && TryGetAttachedPosition(out var left, out var top))
            {
                target.Left = left;
                target.Top = top;
            }
            else
            {
                target.Left = double.NaN;
                target.Top = double.NaN;
            }
        }

        public void ApplySnapSetting()
        {
            if (!AppSettings.Instance.SnapOverlays)
                DetachFromReconnect(AppSettings.Instance.NetworkOverlay);
            if (preview != null)
            {
                previewSnapOverlays = AppSettings.Instance.SnapOverlays;
                if (!AppSettings.Instance.SnapOverlays)
                    DetachFromReconnect(preview);
            }
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
            saved.AttachedToReconnect = AppSettings.Instance.SnapOverlays;
            if (preview != null)
            {
                preview.Left = preview.Top = double.NaN;
                preview.AttachedToReconnect = IsSnapEnabled;
                RestartPreviewTimer();
            }
            Refresh(true);
            AppSettings.Save();
        }

        private void ResetMeasurement()
        {
            generation++;
            history.Clear();
            latency = null;
            region = null;
            regionScanner = new NetworkProbe.RegionLogScanner();
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
                if (dragOrigin.HasValue)
                    plate.ReleaseMouseCapture();
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
                    var scanner = regionScanner;
                    regionTask = Task.Run(() => scanner.Scan(logPath));
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
            catch (Exception ex)
            {
                AppLogger.Error("Network overlay probe failed: " + ex.Message);
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

            text.FontSize = 13 * settings.Scale / 100.0;
            text.FontWeight = OverlayWindow.TextWeights[settings.Weight];
            DrawPing(ms, settings.Compact);
            for (var i = 0; i < parts.Count; i++)
            {
                var isAddress = settings.ShowAddress && i == parts.Count - 1;
                text.Inlines.Add(new Run((settings.Compact && !isAddress ? " · " : Environment.NewLine) + parts[i]));
            }
            text.Foreground = !ms.HasValue ? Brushes.LightGray : ms.Value >= 300 ? Brushes.LightCoral
                : ms.Value >= 100 ? Brushes.Gold : Brushes.LightGreen;
            Opacity = settings.Opacity / 100.0;
            plate.Background = new SolidColorBrush(Color.FromArgb((byte)(255 * settings.BackgroundOpacity / 100), 14, 16, 19));
            if (dragOrigin == null)
            {
                plate.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                hasRendered = plate.DesiredSize.Width > 0 && plate.DesiredSize.Height > 0;
                if (IsSnapEnabled && settings.AttachedToReconnect)
                    PlaceAboveReconnect();
                else if (double.IsNaN(settings.Left) || double.IsNaN(settings.Top))
                    PlaceInitialDetached(settings);
                else
                    Place(settings.Left, settings.Top);
            }
            if (!IsVisible)
                Show();
            UpdateClickThrough();
        }

        private void DrawPing(long? milliseconds, bool compact)
        {
            text.Inlines.Clear();
            text.Inlines.Add(new Run(compact ? "PING " : "PING: "));
            var number = milliseconds.HasValue
                ? Math.Min(999, milliseconds.Value).ToString(CultureInfo.InvariantCulture)
                : "—";
            var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
            var pixelsPerDip = VisualTreeHelper.GetDpi(text).PixelsPerDip;
            var widestDigit = 0.0;
            for (var digit = 0; digit <= 9; digit++)
                widestDigit = Math.Max(widestDigit, MeasurePingText(digit.ToString(CultureInfo.InvariantCulture), typeface, pixelsPerDip));

            var padding = Math.Max(0, 3 * widestDigit - MeasurePingText(number, typeface, pixelsPerDip));
            if (padding > 0)
                text.Inlines.Add(new InlineUIContainer(new Border { Width = padding, Height = 0 }));
            text.Inlines.Add(new Run(number));
            var unit = new Run(" " + Localization.Get("NetworkMilliseconds"));
            if (!milliseconds.HasValue)
                unit.Foreground = Brushes.Transparent;
            text.Inlines.Add(unit);
        }

        private double MeasurePingText(string number, Typeface typeface, double pixelsPerDip)
        {
            return new FormattedText(number, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, text.FontSize, Brushes.Transparent, pixelsPerDip).WidthIncludingTrailingWhitespace;
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
            if (IsVisible && IsSnapEnabled && Settings.AttachedToReconnect && dragOrigin == null)
                PlaceAboveReconnect();
        }

        private bool TryGetAttachedPosition(out double left, out double top)
        {
            var anchor = reconnectOverlay.OverlayBounds;
            if (double.IsNaN(anchor.Left) || double.IsNaN(anchor.Top))
            {
                left = double.NaN;
                top = double.NaN;
                return false;
            }

            var candidateTop = anchor.Top - plate.DesiredSize.Height - OverlayGap;
            if (candidateTop < SystemParameters.VirtualScreenTop)
                candidateTop = anchor.Bottom + OverlayGap;
            left = ClampLeft(anchor.Left);
            top = ClampTop(candidateTop);
            return true;
        }

        private void PlaceDefault(out double left, out double top)
        {
            var workArea = SystemParameters.WorkArea;
            left = ClampLeft(Math.Max(workArea.Left, workArea.Right - plate.DesiredSize.Width - 24));
            top = ClampTop(Math.Max(workArea.Top, workArea.Bottom - plate.DesiredSize.Height - 114));
            Place(left, top);
        }

        private void PlaceInitialDetached(NetworkOverlaySettings target)
        {
            double left;
            double top;
            if (TryGetAttachedPosition(out left, out top))
                Place(left, top);
            else
                PlaceDefault(out left, out top);

            target.Left = left;
            target.Top = top;
            target.AttachedToReconnect = false;
        }

        private void PlaceAboveReconnect()
        {
            if (TryGetAttachedPosition(out var left, out var top))
                Place(left, top);
            else
                PlaceDefault(out _, out _);
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
            if (!IsVisible || !IsSnapEnabled || Settings.AttachedToReconnect || !NearReconnect())
                return;

            Settings.AttachedToReconnect = true;
            if (AppSettings.Instance.SnapOverlays)
                AppSettings.Instance.NetworkOverlay.AttachedToReconnect = true;
            PlaceAboveReconnect();
        }

        private void UpdateClickThrough()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
                return;
            var oldStyle = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
            var style = Settings.Locked ? oldStyle | WsExTransparent : oldStyle & ~WsExTransparent;
            if (style != oldStyle)
                Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, style);
        }

        private double ClampLeft(double left)
        {
            var minLeft = SystemParameters.VirtualScreenLeft;
            var maxLeft = Math.Max(minLeft, minLeft + SystemParameters.VirtualScreenWidth - plate.DesiredSize.Width);
            return Math.Max(minLeft, Math.Min(maxLeft, left));
        }

        private double ClampTop(double top)
        {
            var minTop = SystemParameters.VirtualScreenTop;
            var maxTop = Math.Max(minTop, minTop + SystemParameters.VirtualScreenHeight - plate.DesiredSize.Height);
            return Math.Max(minTop, Math.Min(maxTop, top));
        }

        private void Place(double left, double top)
        {
            Left = ClampLeft(left);
            Top = ClampTop(top);
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
            var source = PresentationSource.FromVisual(this);
            var target = source?.CompositionTarget;
            var dips = target != null ? target.TransformFromDevice.Transform(delta) : delta;
            Place(windowOrigin.X + dips.X, windowOrigin.Y + dips.Y);
        }

        private void EndDrag(object sender, MouseButtonEventArgs e)
        {
            if (!dragOrigin.HasValue)
                return;
            dragOrigin = null;
            plate.ReleaseMouseCapture();
            Settings.Left = Left;
            Settings.Top = Top;
            var saved = AppSettings.Instance.NetworkOverlay;
            saved.Left = Left;
            saved.Top = Top;
            var attach = IsSnapEnabled && NearReconnect();
            Settings.AttachedToReconnect = attach;
            saved.AttachedToReconnect = AppSettings.Instance.SnapOverlays && attach;
            if (attach)
                PlaceAboveReconnect();
            if (preview != null)
                RestartPreviewTimer();
            AppSettings.Save();
            e.Handled = true;
        }

        private void CancelDrag()
        {
            if (!dragOrigin.HasValue)
                return;
            dragOrigin = null;
            if (IsSnapEnabled && Settings.AttachedToReconnect)
                PlaceAboveReconnect();
            else
                Place(windowOrigin.X, windowOrigin.Y);
        }
    }
}
