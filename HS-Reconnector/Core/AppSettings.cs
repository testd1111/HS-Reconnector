using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Xml.Serialization;

namespace HS_Reconnector.Core
{
    [Flags]
    public enum HotkeyModifiers
    {
        None = 0,
        Alt = 1,
        Control = 2,
        Shift = 4,
        Windows = 8
    }

    public sealed class AppSettings
    {
        private static AppSettings _instance;
        private static string recoveryBackupSuffix;
        private static readonly object Sync = new object();

        public static string DataDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HS-Reconnector");

        public static string SettingsPath => Path.Combine(DataDirectory, "settings.xml");

        internal const Key DefaultHotkey = Key.F10;
        internal const HotkeyModifiers DefaultHotkeyModifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt;

        public Key Hotkey { get; set; } = DefaultHotkey;
        public HotkeyModifiers HotkeyModifiers { get; set; } = DefaultHotkeyModifiers;
        public int ReconnectCooldownSeconds { get; set; } = 2;
        public int DisconnectTimeoutSeconds { get; set; } = 20;

        public bool EnableOverlay { get; set; } = true;
        public bool ShowOverlayOnlyInMatch { get; set; } = false;
        public bool ShowOverlayOnlyWhenGameFocused { get; set; } = true;
        public bool LockOverlayPosition { get; set; } = false;
        public bool UseMiniOverlay { get; set; }
        public int OverlayOpacityPercent { get; set; } = 95;
        public int OverlayBackgroundOpacityPercent { get; set; } = 100;
        public int OverlayScalePercent { get; set; } = 100;
        public int OverlayFontWeight { get; set; } = 2;
        public double OverlayLeft { get; set; } = double.NaN;
        public double OverlayTop { get; set; } = double.NaN;
        public NetworkOverlaySettings NetworkOverlay { get; set; } = new NetworkOverlaySettings();

        public bool MinimizeToTrayOnClose { get; set; } = true;
        public bool MinimizeToTrayOnMinimize { get; set; } = false;
        public bool StartMinimizedToTray { get; set; } = false;
        public string CustomHearthstoneDirectory { get; set; } = string.Empty;
        public string Language { get; set; } = "auto";

        public static AppSettings Instance
        {
            get
            {
                lock (Sync)
                {
                    if (_instance == null)
                        _instance = new AppSettings();
                    return _instance;
                }
            }
        }

        public static void Load() => Load(SettingsPath);

        internal static void Load(string settingsPath)
        {
            lock (Sync)
            {
                try
                {
                    var serializer = new XmlSerializer(typeof(AppSettings));
                    using (var stream = File.OpenRead(settingsPath))
                    {
                        if (serializer.Deserialize(stream) is AppSettings loaded)
                            _instance = loaded;
                        recoveryBackupSuffix = null;
                    }
                }
                catch (FileNotFoundException) { recoveryBackupSuffix = null; }
                catch (DirectoryNotFoundException) { recoveryBackupSuffix = null; }
                catch (InvalidOperationException ex)
                {
                    recoveryBackupSuffix = ".corrupt";
                    AppLogger.Error("Could not deserialize settings: " + ex.Message);
                }
                catch (Exception ex)
                {
                    recoveryBackupSuffix = ".unreadable";
                    AppLogger.Error("Failed to load settings: " + ex.Message);
                }

                Instance.Normalize();
            }
        }

        public static void Save() => Save(SettingsPath);

        internal static void Save(string settingsPath)
        {
            lock (Sync)
            {
                try
                {
                    Instance.Normalize();
                    Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
                    var serializer = new XmlSerializer(typeof(AppSettings));
                    var tempPath = settingsPath + ".tmp";
                    var backupPath = recoveryBackupSuffix == null ? settingsPath + ".bak"
                        : settingsPath + "." + Guid.NewGuid().ToString("N") + recoveryBackupSuffix;
                    using (var stream = File.Create(tempPath))
                    {
                        serializer.Serialize(stream, Instance);
                    }
                    if (File.Exists(settingsPath))
                    {
                        File.Replace(tempPath, settingsPath, backupPath, true);
                        if (recoveryBackupSuffix == null)
                            TryDeleteFile(backupPath);
                    }
                    else
                    {
                        File.Move(tempPath, settingsPath);
                    }
                    recoveryBackupSuffix = null;
                }
                catch (Exception ex)
                {
                    TryDeleteFile(settingsPath + ".tmp");
                    AppLogger.Error("Failed to save settings: " + ex.Message);
                }
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best-effort cleanup for temporary/backup settings files.
            }
        }

        public void Normalize()
        {
            NetworkOverlay = NetworkOverlay ?? new NetworkOverlaySettings();
            NetworkOverlay.Normalize();
            ReconnectCooldownSeconds = Clamp(ReconnectCooldownSeconds, 0, 10);
            DisconnectTimeoutSeconds = Clamp(DisconnectTimeoutSeconds, 5, 60);
            OverlayOpacityPercent = Clamp(OverlayOpacityPercent, 15, 100);
            OverlayBackgroundOpacityPercent = Clamp(OverlayBackgroundOpacityPercent, 0, 100);
            OverlayScalePercent = Clamp(OverlayScalePercent, 75, 150);
            OverlayFontWeight = Clamp(OverlayFontWeight, 0, 3);

            if (Hotkey == Key.F12 || (Hotkey != Key.None && HotkeyModifiers == HotkeyModifiers.None))
            {
                Hotkey = DefaultHotkey;
                HotkeyModifiers = DefaultHotkeyModifiers;
            }

            if (double.IsInfinity(OverlayLeft))
                OverlayLeft = double.NaN;
            if (double.IsInfinity(OverlayTop))
                OverlayTop = double.NaN;

            if (CustomHearthstoneDirectory == null)
                CustomHearthstoneDirectory = string.Empty;
            else
                CustomHearthstoneDirectory = CustomHearthstoneDirectory.Trim();

            if (!string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Language, "ru", StringComparison.OrdinalIgnoreCase))
            {
                Language = "auto";
            }
            else
            {
                Language = Language.ToLowerInvariant();
            }
        }

        public void ResetOverlayPosition(double overlayWidth, double overlayHeight)
        {
            var workArea = SystemParameters.WorkArea;
            OverlayLeft = Math.Max(workArea.Left, workArea.Right - overlayWidth - 24);
            OverlayTop = Math.Max(workArea.Top, workArea.Bottom - overlayHeight - 64);
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }
    }

    public sealed class NetworkOverlaySettings
    {
        public bool Enabled { get; set; }
        public bool ShowRegion { get; set; } = true;
        public string Region { get; set; } = "Auto";
        public bool ShowAddress { get; set; }
        public bool ShowFailedChecks { get; set; } = true;
        public bool HideZeroFailures { get; set; } = true;
        public bool Compact { get; set; } = true;
        public bool Locked { get; set; } = true;
        public int Weight { get; set; } = 1;
        public int Scale { get; set; } = 100;
        public int BackgroundOpacity { get; set; } = 10;
        public int Opacity { get; set; } = 100;
        public bool AttachedToReconnect { get; set; } = true;
        public int Interval { get; set; } = 3;
        public double Left { get; set; } = double.NaN;
        public double Top { get; set; } = double.NaN;

        internal void Normalize()
        {
            if (Region != "EU" && Region != "US" && Region != "ASIA" && Region != "CHINA")
                Region = "Auto";
            Weight = Math.Max(0, Math.Min(3, Weight));
            Scale = Math.Max(75, Math.Min(150, Scale));
            BackgroundOpacity = Math.Max(0, Math.Min(100, BackgroundOpacity));
            Opacity = Math.Max(15, Math.Min(100, Opacity));
            Interval = Math.Max(2, Math.Min(10, Interval));
            if (double.IsInfinity(Left))
                Left = double.NaN;
            if (double.IsInfinity(Top))
                Top = double.NaN;
            if (double.IsNaN(Left) || double.IsNaN(Top))
                AttachedToReconnect = true;
        }
    }
}
