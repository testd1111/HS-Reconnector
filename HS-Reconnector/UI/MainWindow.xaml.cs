using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using HS_Reconnector.Core;
using HS_Reconnector.Native;
using Localization = HS_Reconnector.Core.Localization;

namespace HS_Reconnector.UI
{
    public partial class MainWindow : Window
    {
        private static readonly Brush AdminBg = new SolidColorBrush(Color.FromRgb(0x16, 0x30, 0x24));
        private static readonly Brush AdminBorder = new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x67));
        private static readonly Brush AdminFg = new SolidColorBrush(Color.FromRgb(0x5B, 0xE0, 0x9C));
        private static readonly Brush WarnBg = new SolidColorBrush(Color.FromRgb(0x34, 0x1B, 0x1E));
        private static readonly Brush WarnBorder = new SolidColorBrush(Color.FromRgb(0xA6, 0x3A, 0x46));
        private static readonly Brush WarnFg = new SolidColorBrush(Color.FromRgb(0xFF, 0x9A, 0xA2));
        private static readonly Brush GreenText = new SolidColorBrush(Color.FromRgb(0x5B, 0xE0, 0x9C));
        private static readonly Brush AmberText = new SolidColorBrush(Color.FromRgb(0xFF, 0xC2, 0x3A));
        private static readonly Brush MutedTextBrush = new SolidColorBrush(Color.FromRgb(0x93, 0xA0, 0xB2));

        private readonly ReconnectController controller;
        private readonly OverlayWindow overlayWindow;
        private readonly NetworkOverlayWindow networkOverlay;
        private Key draftHotkey;
        private HotkeyModifiers draftModifiers;
        private Key failedHotkey = Key.None;
        private HotkeyModifiers failedModifiers = HotkeyModifiers.None;
        private bool loadingDraft;
        private bool allowExit;
        private int logRefreshPending;

        internal MainWindow(ReconnectController controller, OverlayWindow overlayWindow, NetworkOverlayWindow networkOverlay)
        {
            this.controller = controller;
            this.overlayWindow = overlayWindow;
            this.networkOverlay = networkOverlay;
            loadingDraft = true;
            InitializeComponent();

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = "v" + version.ToString(version.Build == 0 ? 2 : 3);

            LoadSettingsToForm();
            ApplyLocalization();
            LoadLogSnapshot();

            AppLogger.EntryAdded += OnLogEntryAdded;
            Localization.LanguageChanged += OnLanguageChanged;
        }

        public void PrepareForExit()
        {
            allowExit = true;
            AppLogger.EntryAdded -= OnLogEntryAdded;
            Localization.LanguageChanged -= OnLanguageChanged;
        }

        public void SyncOverlayCheckBox()
        {
            EnableOverlayCheck.IsChecked = AppSettings.Instance.EnableOverlay;
        }

        public void ShowAndActivate()
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            if (!IsVisible)
                Show();

            var hwnd = new WindowInteropHelper(this).Handle;
            Win32.BringWindowToFront(hwnd);

            var wasTopmost = Topmost;
            Topmost = true;
            Activate();
            Topmost = wasTopmost;
            Focus();
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);

            if (!allowExit
                && AppSettings.Instance.MinimizeToTrayOnMinimize
                && WindowState == WindowState.Minimized)
            {
                Hide();
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!allowExit && AppSettings.Instance.MinimizeToTrayOnClose)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            PrepareForExit();
            base.OnClosing(e);
        }

        internal void UpdateState(ControllerSnapshot snapshot)
        {
            if (snapshot == null)
                return;

            if (snapshot.IsElevated)
            {
                ElevationBadge.Background = AdminBg;
                ElevationBadge.BorderBrush = AdminBorder;
                ElevationBadgeText.Foreground = AdminFg;
                ElevationBadgeText.Text = Localization.Get("BadgeAdmin");
                ElevationWarningBanner.Visibility = Visibility.Collapsed;
            }
            else
            {
                ElevationBadge.Background = WarnBg;
                ElevationBadge.BorderBrush = WarnBorder;
                ElevationBadgeText.Foreground = WarnFg;
                ElevationBadgeText.Text = Localization.Get("BadgeNoAdmin");
                ElevationWarningText.Text = Localization.Get("ElevationWarning");
                ElevationWarningBanner.Visibility = Visibility.Visible;
            }

            ValueGame.Text = snapshot.IsHearthstoneRunning
                ? Localization.Get("GameRunning")
                : Localization.Get("GameNotRunning");
            ValueGame.Foreground = snapshot.IsHearthstoneRunning ? GreenText : MutedTextBrush;

            if (snapshot.EndpointUnsupported)
            {
                ValueEndpoint.Text = Localization.Get("EndpointUnsupported");
                ValueEndpoint.Foreground = AmberText;
            }
            else if (!string.IsNullOrWhiteSpace(snapshot.RemoteAddress) && snapshot.RemotePort != 0)
            {
                ValueEndpoint.Text = snapshot.RemoteAddress + ":" + snapshot.RemotePort;
                ValueEndpoint.Foreground = GreenText;
            }
            else
            {
                ValueEndpoint.Text = Localization.Get("EndpointWaiting");
                ValueEndpoint.Foreground = MutedTextBrush;
            }

            if (snapshot.ConnectionStatus == ConnectionStatus.Disconnected)
            {
                ValueTcp.Text = Localization.Get("TcpDisconnected");
                ValueTcp.Foreground = AmberText;
            }
            else if (snapshot.EstablishedTcpCount == 1)
            {
                ValueTcp.Text = Localization.Format("TcpEstablished", snapshot.EstablishedTcpCount);
                ValueTcp.Foreground = GreenText;
            }
            else if (snapshot.EstablishedTcpCount > 1)
            {
                ValueTcp.Text = Localization.Format("TcpMultiple", snapshot.EstablishedTcpCount);
                ValueTcp.Foreground = AmberText;
            }
            else
            {
                ValueTcp.Text = Localization.Get("TcpNone");
                ValueTcp.Foreground = MutedTextBrush;
            }

            if (!string.IsNullOrEmpty(snapshot.ActiveLogPath))
            {
                ValueLog.Text = Path.GetFileName(snapshot.ActiveLogPath);
                ValueLog.ToolTip = snapshot.ActiveLogPath;
                ValueLog.Foreground = Brushes.White;
            }
            else
            {
                ValueLog.Text = Localization.Get("LogWaiting");
                ValueLog.ToolTip = snapshot.HearthstoneDirectory;
                ValueLog.Foreground = MutedTextBrush;
            }

            ReconnectButton.IsEnabled = snapshot.CanReconnect;
            switch (snapshot.VisualState)
            {
                case ReconnectVisualState.Disconnected:
                    ReconnectButtonText.Text = Localization.Get("BtnDisconnected");
                    break;
                case ReconnectVisualState.Cooldown:
                    ReconnectButtonText.Text = Localization.Format("BtnWait", snapshot.CooldownRemainingSeconds);
                    break;
                default:
                    ReconnectButtonText.Text = Localization.Get("BtnReconnect");
                    break;
            }
        }

        private void LoadSettingsToForm()
        {
            loadingDraft = true;
            try
            {
                var s = AppSettings.Instance;
                s.Normalize();

                draftHotkey = s.Hotkey;
                draftModifiers = s.HotkeyModifiers;
                HotkeyBox.Text = FormatHotkey(draftModifiers, draftHotkey);

                CooldownSlider.Value = s.ReconnectCooldownSeconds;
                TimeoutSlider.Value = s.DisconnectTimeoutSeconds;
                EnableOverlayCheck.IsChecked = s.EnableOverlay;
                OverlayOnlyMatchCheck.IsChecked = s.ShowOverlayOnlyInMatch;
                OverlayOnlyWhenFocusedCheck.IsChecked = s.ShowOverlayOnlyWhenGameFocused;
                LockOverlayCheck.IsChecked = s.LockOverlayPosition;
                MiniOverlayCheck.IsChecked = s.UseMiniOverlay;
                OpacitySlider.Value = s.OverlayOpacityPercent;
                BgOpacitySlider.Value = s.OverlayBackgroundOpacityPercent;
                ScaleSlider.Value = s.OverlayScalePercent;
                OverlayWeightCombo.SelectedIndex = s.OverlayFontWeight;
                LoadNetworkSettings(s.NetworkOverlay);

                MinimizeToTrayCheck.IsChecked = s.MinimizeToTrayOnClose;
                MinimizeToTrayOnMinimizeCheck.IsChecked = s.MinimizeToTrayOnMinimize;
                StartMinimizedCheck.IsChecked = s.StartMinimizedToTray;
                HsPathTextBox.Text = s.CustomHearthstoneDirectory;

                SelectLanguageCombo(s.Language);

                UpdateSliderTexts();
            }
            finally
            {
                loadingDraft = false;
            }
        }

        private void ApplyLocalization()
        {
            Title = Localization.Get("AppTitle");
            HeaderTitle.Text = Localization.Get("AppTitle");
            StatusCardTitle.Text = Localization.Get("StatusCardTitle");
            LabelGame.Text = Localization.Get("LabelGame") + ":";
            LabelEndpoint.Text = Localization.Get("LabelEndpoint") + ":";
            LabelTcp.Text = Localization.Get("LabelTcp") + ":";
            LabelLog.Text = Localization.Get("LabelLog") + ":";

            TabSettings.Header = Localization.Get("SectionHotkey");
            TabBehavior.Header = Localization.Get("SectionBehavior");
            TabLogs.Header = Localization.Get("SectionLogs");

            OptHotkeyLabel.Text = Localization.Get("OptHotkey");
            ResetHotkeyButton.Content = Localization.Get("BtnResetHotkey");
            ClearHotkeyButton.Content = Localization.Get("BtnClearHotkey");
            OptCooldownLabel.Text = Localization.Get("OptCooldown");
            OptTimeoutLabel.Text = Localization.Get("OptTimeout");

            EnableOverlayCheck.Content = Localization.Get("OptEnableOverlay");
            OverlayOnlyMatchCheck.Content = Localization.Get("OptOverlayOnlyMatch");
            OverlayOnlyWhenFocusedCheck.Content = Localization.Get("OptOverlayOnlyWhenFocused");
            LockOverlayCheck.Content = Localization.Get("OptLockOverlay");
            OverlayWeightLabel.Text = Localization.Get("NetworkWeight");
            OverlayNormalItem.Content = Localization.Get("NetworkNormal");
            OverlaySemiboldItem.Content = Localization.Get("NetworkSemibold");
            OverlayBoldItem.Content = Localization.Get("NetworkBold");
            OverlayExtraBoldItem.Content = Localization.Get("NetworkExtraBold");
            MiniOverlayCheck.Content = Localization.Get("OptMiniOverlay");
            TestOverlayButton.ToolTip = Localization.Get("OverlayTestTooltip");
            OptOpacityLabel.Text = Localization.Get("OptOverlayOpacity");
            OptBgOpacityLabel.Text = Localization.Get("OptOverlayBgOpacity");
            OptScaleLabel.Text = Localization.Get("OptOverlayScale");
            ResetOverlayButton.Content = Localization.Get("BtnResetOverlay");
            SaveSettingsButton.Content = Localization.Get("BtnSaveApply");
            SaveBehaviorButton.Content = Localization.Get("BtnSaveApply");

            MinimizeToTrayCheck.Content = Localization.Get("OptMinToTray");
            MinimizeToTrayOnMinimizeCheck.Content = Localization.Get("OptMinToTrayOnMinimize");
            StartMinimizedCheck.Content = Localization.Get("OptStartMin");
            OptLanguageLabel.Text = Localization.Get("OptLanguage");
            OptHsPathLabel.Text = Localization.Get("OptHsPath");
            BrowseHsButton.Content = Localization.Get("BtnBrowse");
            ClearLogButton.Content = Localization.Get("BtnClearLog");

            if (LanguageCombo.Items.Count >= 3)
            {
                ((ComboBoxItem)LanguageCombo.Items[0]).Content = Localization.Get("LangAuto");
                ((ComboBoxItem)LanguageCombo.Items[1]).Content = Localization.Get("LangEn");
                ((ComboBoxItem)LanguageCombo.Items[2]).Content = Localization.Get("LangRu");
            }

            var isRu = Localization.IsRussian;
            LangRuButton.Tag = isRu ? "Active" : null;
            LangEnButton.Tag = isRu ? null : "Active";
            LangRuButton.ToolTip = Localization.Get("LangSwitchTooltipRu");
            LangEnButton.ToolTip = Localization.Get("LangSwitchTooltipEn");

            UpdateSliderTexts();
            LocalizeNetworkSettings();

            if (!HotkeyBox.IsKeyboardFocused)
                HotkeyBox.Text = FormatHotkey(draftModifiers, draftHotkey);

            if (failedHotkey != Key.None)
                HotkeyErrorText.Text = Localization.Format("HotkeyRegisterFailed", FormatHotkey(failedModifiers, failedHotkey));

            UpdateState(controller.GetSnapshot());
        }

        private void LoadNetworkSettings(NetworkOverlaySettings settings)
        {
            NetworkEnabledCheck.IsChecked = settings.Enabled;
            NetworkShowRegionCheck.IsChecked = settings.ShowRegion;
            NetworkShowAddressCheck.IsChecked = settings.ShowAddress;
            NetworkShowFailedChecksCheck.IsChecked = settings.ShowFailedChecks;
            NetworkHideZeroFailuresCheck.IsChecked = settings.HideZeroFailures;
            NetworkCompactCheck.IsChecked = settings.Compact;
            NetworkLockedCheck.IsChecked = settings.Locked;
            foreach (ComboBoxItem option in NetworkRegionCombo.Items)
            {
                if (string.Equals(option.Tag as string, settings.Region, StringComparison.Ordinal))
                {
                    NetworkRegionCombo.SelectedItem = option;
                    break;
                }
            }
            NetworkWeightCombo.SelectedIndex = settings.Weight;
            NetworkScaleSlider.Value = settings.Scale;
            NetworkOpacitySlider.Value = settings.Opacity;
            NetworkBackgroundOpacitySlider.Value = settings.BackgroundOpacity;
            NetworkIntervalSlider.Value = settings.Interval;
        }

        private void LocalizeNetworkSettings()
        {
            TabNetwork.Header = Localization.Get("NetworkTab");
            NetworkHintText.Text = Localization.Get("NetworkHint");
            NetworkEnabledCheck.Content = Localization.Get("NetworkEnable");
            NetworkShowRegionCheck.Content = Localization.Get("NetworkShowRegion");
            NetworkShowAddressCheck.Content = Localization.Get("NetworkShowAddress");
            NetworkShowFailedChecksCheck.Content = Localization.Get("NetworkShowFailures");
            NetworkHideZeroFailuresCheck.Content = Localization.Get("NetworkHideZero");
            NetworkCompactCheck.Content = Localization.Get("NetworkCompact");
            NetworkLockedCheck.Content = Localization.Get("NetworkLocked");
            NetworkRegionLabel.Text = Localization.Get("NetworkRegion");
            NetworkWeightLabel.Text = Localization.Get("NetworkWeight");
            NetworkAutoItem.Content = Localization.Get("NetworkAuto");
            NetworkNormalItem.Content = Localization.Get("NetworkNormal");
            NetworkSemiboldItem.Content = Localization.Get("NetworkSemibold");
            NetworkBoldItem.Content = Localization.Get("NetworkBold");
            NetworkExtraBoldItem.Content = Localization.Get("NetworkExtraBold");
            NetworkScaleLabel.Text = Localization.Get("NetworkScale");
            NetworkOpacityLabel.Text = Localization.Get("OptOverlayOpacity");
            NetworkBackgroundOpacityLabel.Text = Localization.Get("OptOverlayBgOpacity");
            NetworkIntervalLabel.Text = Localization.Get("NetworkInterval");
            NetworkPreviewButton.ToolTip = Localization.Get("NetworkTestTooltip");
            NetworkResetPositionButton.Content = Localization.Get("BtnResetOverlay");
            NetworkSaveButton.Content = SaveSettingsButton.Content;
            UpdateNetworkSliderTexts();
            networkOverlay.RefreshLanguage();
        }

        private void NetworkSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (IsInitialized && !loadingDraft)
                UpdateNetworkSliderTexts();
            NetworkPreviewSettings_Changed(sender, e);
        }

        private void NetworkPreviewSettings_Changed(object sender, RoutedEventArgs e)
        {
            if (IsInitialized && !loadingDraft && networkOverlay.IsPreviewActive)
                networkOverlay.UpdatePreview(ReadNetworkSettings());
        }

        private void UpdateNetworkSliderTexts()
        {
            NetworkScaleValue.Text = NetworkScaleSlider.Value + "%";
            NetworkOpacityValue.Text = NetworkOpacitySlider.Value + "%";
            NetworkBackgroundOpacityValue.Text = NetworkBackgroundOpacitySlider.Value + "%";
            NetworkIntervalValue.Text = Localization.Format("NetworkSeconds", NetworkIntervalSlider.Value);
        }

        private NetworkOverlaySettings ReadNetworkSettings()
        {
            var saved = AppSettings.Instance.NetworkOverlay;
            return new NetworkOverlaySettings
            {
                Enabled = NetworkEnabledCheck.IsChecked == true,
                ShowRegion = NetworkShowRegionCheck.IsChecked == true,
                ShowAddress = NetworkShowAddressCheck.IsChecked == true,
                ShowFailedChecks = NetworkShowFailedChecksCheck.IsChecked == true,
                HideZeroFailures = NetworkHideZeroFailuresCheck.IsChecked == true,
                Compact = NetworkCompactCheck.IsChecked == true,
                Locked = NetworkLockedCheck.IsChecked == true,
                Region = (NetworkRegionCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Auto",
                Weight = NetworkWeightCombo.SelectedIndex,
                Scale = (int)NetworkScaleSlider.Value,
                Opacity = (int)NetworkOpacitySlider.Value,
                BackgroundOpacity = (int)NetworkBackgroundOpacitySlider.Value,
                Interval = (int)NetworkIntervalSlider.Value,
                Left = saved.Left,
                Top = saved.Top,
                AttachedToReconnect = saved.AttachedToReconnect
            };
        }

        private void NetworkPreview_Click(object sender, RoutedEventArgs e)
        {
            networkOverlay.TogglePreview(ReadNetworkSettings());
        }

        private void NetworkResetPosition_Click(object sender, RoutedEventArgs e)
        {
            networkOverlay.ResetPosition();
        }

        private void NetworkSave_Click(object sender, RoutedEventArgs e)
        {
            AppSettings.Instance.NetworkOverlay = ReadNetworkSettings();
            AppSettings.Save();
            networkOverlay.ApplySettings();
        }

        private void OnLanguageChanged()
        {
            Dispatcher.BeginInvoke(new Action(ApplyLocalization));
        }

        private void LoadLogSnapshot()
        {
            IReadOnlyList<string> lines = AppLogger.GetSnapshot();
            LogTextBox.Text = lines.Count == 0 ? string.Empty : string.Join(Environment.NewLine, lines) + Environment.NewLine;
            LogTextBox.ScrollToEnd();
        }

        private void OnLogEntryAdded()
        {
            if (Interlocked.Exchange(ref logRefreshPending, 1) != 0)
                return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref logRefreshPending, 0);
                LoadLogSnapshot();
            }));
        }

        private void ReconnectButton_Click(object sender, RoutedEventArgs e)
        {
            controller.ExecuteReconnect();
        }

        private void Sliders_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (loadingDraft || !IsInitialized)
                return;

            UpdateSliderTexts();
            if (sender != TimeoutSlider)
                RefreshOverlayPreview();
        }

        private void OverlayCheck_Changed(object sender, RoutedEventArgs e)
        {
            RefreshOverlayPreview();
        }

        private void RefreshOverlayPreview()
        {
            if (!loadingDraft && overlayWindow?.IsPreviewActive == true)
                overlayWindow.UpdatePreviewSettings(ReadOverlayPreviewSettings());
        }

        private void UpdateSliderTexts()
        {
            CooldownValueText.Text = Localization.Format("UnitSeconds", (int)Math.Round(CooldownSlider.Value));
            TimeoutValueText.Text = Localization.Format("UnitSeconds", (int)Math.Round(TimeoutSlider.Value));
            OpacityValueText.Text = ((int)Math.Round(OpacitySlider.Value)) + "%";
            BgOpacityValueText.Text = ((int)Math.Round(BgOpacitySlider.Value)) + "%";
            ScaleValueText.Text = ((int)Math.Round(ScaleSlider.Value)) + "%";
        }

        private void LangRuButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyQuickLanguage("ru");
        }

        private void LangEnButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyQuickLanguage("en");
        }

        private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (loadingDraft || !IsInitialized)
                return;

            var selectedLang = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            if (!string.IsNullOrEmpty(selectedLang))
                ApplyQuickLanguage(selectedLang);
        }

        private void ApplyQuickLanguage(string lang)
        {
            var s = AppSettings.Instance;
            if (string.Equals(s.Language, lang, StringComparison.OrdinalIgnoreCase))
                return;

            s.Language = lang;
            AppSettings.Save();

            loadingDraft = true;
            try
            {
                SelectLanguageCombo(lang);
            }
            finally
            {
                loadingDraft = false;
            }

            overlayWindow.ApplySettingsAndPosition();
            Localization.NotifyChanged();
        }

        private void SelectLanguageCombo(string lang)
        {
            foreach (ComboBoxItem item in LanguageCombo.Items)
            {
                if (string.Equals(item.Tag as string, lang, StringComparison.OrdinalIgnoreCase))
                {
                    LanguageCombo.SelectedItem = item;
                    return;
                }
            }
            if (LanguageCombo.Items.Count > 0)
                LanguageCombo.SelectedIndex = 0;
        }

        private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            overlayWindow.StopPreview();
            var s = AppSettings.Instance;
            var previousHotkey = s.Hotkey;
            var previousModifiers = s.HotkeyModifiers;
            var attemptedHotkey = draftHotkey;
            var attemptedModifiers = draftModifiers;

            s.Hotkey = draftHotkey;
            s.HotkeyModifiers = draftModifiers;
            s.ReconnectCooldownSeconds = (int)Math.Round(CooldownSlider.Value);
            s.DisconnectTimeoutSeconds = (int)Math.Round(TimeoutSlider.Value);
            s.EnableOverlay = EnableOverlayCheck.IsChecked == true;
            s.ShowOverlayOnlyInMatch = OverlayOnlyMatchCheck.IsChecked == true;
            s.ShowOverlayOnlyWhenGameFocused = OverlayOnlyWhenFocusedCheck.IsChecked == true;
            s.LockOverlayPosition = LockOverlayCheck.IsChecked == true;
            s.OverlayFontWeight = OverlayWeightCombo.SelectedIndex;
            s.UseMiniOverlay = MiniOverlayCheck.IsChecked == true;
            var oldScalePercent = s.OverlayScalePercent;
            var newScalePercent = (int)Math.Round(ScaleSlider.Value);
            s.OverlayOpacityPercent = (int)Math.Round(OpacitySlider.Value);
            s.OverlayBackgroundOpacityPercent = (int)Math.Round(BgOpacitySlider.Value);
            s.OverlayScalePercent = newScalePercent;
            s.MinimizeToTrayOnClose = MinimizeToTrayCheck.IsChecked == true;
            s.MinimizeToTrayOnMinimize = MinimizeToTrayOnMinimizeCheck.IsChecked == true;
            s.StartMinimizedToTray = StartMinimizedCheck.IsChecked == true;
            s.CustomHearthstoneDirectory = HsPathTextBox.Text;

            var selectedLang = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
            var languageChanged = !string.Equals(s.Language, selectedLang, StringComparison.OrdinalIgnoreCase);
            s.Language = selectedLang;

            s.Normalize();
            overlayWindow.AdjustPositionForScaleChange(oldScalePercent, s.OverlayScalePercent);

            if (!controller.ApplySettings())
            {
                s.Hotkey = previousHotkey;
                s.HotkeyModifiers = previousModifiers;
                draftHotkey = previousHotkey;
                draftModifiers = previousModifiers;
                HotkeyBox.Text = FormatHotkey(draftModifiers, draftHotkey);
                controller.ApplySettings();
                ShowHotkeyError(attemptedModifiers, attemptedHotkey);
                if (!TabSettings.IsSelected)
                    MainTabs.SelectedItem = TabSettings;
            }
            else
            {
                ClearHotkeyError();
            }

            AppSettings.Save();
            overlayWindow.ApplySettingsAndPosition();
            if (languageChanged)
                Localization.NotifyChanged();
        }

        private void ResetOverlayButton_Click(object sender, RoutedEventArgs e)
        {
            overlayWindow.StopPreview();
            overlayWindow.ResetPosition();
            AppSettings.Save();
            overlayWindow.ApplySettingsAndPosition();
        }

        private void TestOverlayButton_Click(object sender, RoutedEventArgs e)
        {
            overlayWindow.TogglePreview(ReadOverlayPreviewSettings());
        }

        private AppSettings ReadOverlayPreviewSettings()
        {
            return new AppSettings
            {
                UseMiniOverlay = MiniOverlayCheck.IsChecked == true,
                LockOverlayPosition = LockOverlayCheck.IsChecked == true,
                OverlayFontWeight = OverlayWeightCombo.SelectedIndex,
                OverlayOpacityPercent = (int)Math.Round(OpacitySlider.Value),
                OverlayBackgroundOpacityPercent = (int)Math.Round(BgOpacitySlider.Value),
                OverlayScalePercent = (int)Math.Round(ScaleSlider.Value),
                ReconnectCooldownSeconds = (int)Math.Round(CooldownSlider.Value)
            };
        }

        private void BrowseHsButton_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = Localization.Get("FolderDialogDesc");
                if (Directory.Exists(HsPathTextBox.Text))
                    dialog.SelectedPath = HsPathTextBox.Text;

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    HsPathTextBox.Text = dialog.SelectedPath;
            }
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            AppLogger.Clear();
            LogTextBox.Clear();
        }

        private void ResetHotkeyButton_Click(object sender, RoutedEventArgs e)
        {
            ClearHotkeyError();
            draftHotkey = AppSettings.DefaultHotkey;
            draftModifiers = AppSettings.DefaultHotkeyModifiers;
            HotkeyBox.Text = FormatHotkey(draftModifiers, draftHotkey);
        }

        private void ClearHotkeyButton_Click(object sender, RoutedEventArgs e)
        {
            ClearHotkeyError();
            draftHotkey = Key.None;
            draftModifiers = HotkeyModifiers.None;
            HotkeyBox.Text = FormatHotkey(draftModifiers, draftHotkey);
        }

        private void HotkeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            ClearHotkeyError();
            controller.SuspendHotkey(true);
            HotkeyBox.Text = Localization.Get("OptHotkeyPrompt");
        }

        internal void ShowHotkeyError(HotkeyModifiers modifiers, Key key)
        {
            failedModifiers = modifiers;
            failedHotkey = key;
            HotkeyErrorText.Text = Localization.Format("HotkeyRegisterFailed", FormatHotkey(modifiers, key));
            HotkeyErrorText.Visibility = Visibility.Visible;
        }

        private void ClearHotkeyError()
        {
            failedModifiers = HotkeyModifiers.None;
            failedHotkey = Key.None;
            HotkeyErrorText.Text = string.Empty;
            HotkeyErrorText.Visibility = Visibility.Collapsed;
        }

        private void HotkeyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            controller.SuspendHotkey(false);
            HotkeyBox.Text = FormatHotkey(draftModifiers, draftHotkey);
        }

        private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.ImeProcessed)
                key = e.ImeProcessedKey;

            if (IsModifierKey(key))
                return;

            if (key == Key.F12)
            {
                HotkeyBox.Text = Localization.Get("OptHotkeyF12");
                return;
            }

            var modifiers = ReadKeyboardModifiers();
            if (modifiers == HotkeyModifiers.None)
            {
                HotkeyBox.Text = Localization.Get("OptHotkeyNeedMod");
                return;
            }

            draftHotkey = key;
            draftModifiers = modifiers;
            HotkeyBox.Text = FormatHotkey(draftModifiers, draftHotkey);
        }

        private static bool IsModifierKey(Key key)
        {
            return key == Key.LeftCtrl || key == Key.RightCtrl
                || key == Key.LeftShift || key == Key.RightShift
                || key == Key.LeftAlt || key == Key.RightAlt
                || key == Key.LWin || key == Key.RWin;
        }

        private static HotkeyModifiers ReadKeyboardModifiers()
        {
            var result = HotkeyModifiers.None;
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                result |= HotkeyModifiers.Control;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                result |= HotkeyModifiers.Shift;
            if ((Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
                result |= HotkeyModifiers.Alt;
            if ((Keyboard.Modifiers & ModifierKeys.Windows) == ModifierKeys.Windows)
                result |= HotkeyModifiers.Windows;
            return result;
        }

        private static string FormatHotkey(HotkeyModifiers modifiers, Key key)
        {
            if (key == Key.None)
                return Localization.Get("OptHotkeyNone");

            var text = string.Empty;
            if ((modifiers & HotkeyModifiers.Control) == HotkeyModifiers.Control)
                text += "Ctrl+";
            if ((modifiers & HotkeyModifiers.Shift) == HotkeyModifiers.Shift)
                text += "Shift+";
            if ((modifiers & HotkeyModifiers.Alt) == HotkeyModifiers.Alt)
                text += "Alt+";
            if ((modifiers & HotkeyModifiers.Windows) == HotkeyModifiers.Windows)
                text += "Win+";
            if (key >= Key.D0 && key <= Key.D9)
                return text + (key - Key.D0);
            return text + (key == Key.OemPlus ? "+" : key.ToString());
        }
    }
}
