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
        private static readonly object Sync = new object();

        public static string DataDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HS-Reconnector");

        public static string SettingsPath => Path.Combine(DataDirectory, "settings.xml");

        public Key Hotkey { get; set; } = Key.R;
        public HotkeyModifiers HotkeyModifiers { get; set; } = HotkeyModifiers.Control | HotkeyModifiers.Shift;
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
        public double OverlayLeft { get; set; } = double.NaN;
        public double OverlayTop { get; set; } = double.NaN;

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

        public static void Load()
        {
            lock (Sync)
            {
                try
                {
                    if (File.Exists(SettingsPath))
                    {
                        var serializer = new XmlSerializer(typeof(AppSettings));
                        using (var stream = File.OpenRead(SettingsPath))
                        {
                            if (serializer.Deserialize(stream) is AppSettings loaded)
                                _instance = loaded;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error("Failed to load settings: " + ex.Message);
                }

                Instance.Normalize();
            }
        }

        public static void Save()
        {
            lock (Sync)
            {
                try
                {
                    Instance.Normalize();
                    Directory.CreateDirectory(DataDirectory);
                    var serializer = new XmlSerializer(typeof(AppSettings));
                    var tempPath = SettingsPath + ".tmp";
                    var backupPath = SettingsPath + ".bak";
                    using (var stream = File.Create(tempPath))
                    {
                        serializer.Serialize(stream, Instance);
                    }
                    if (File.Exists(SettingsPath))
                    {
                        File.Replace(tempPath, SettingsPath, backupPath, true);
                        TryDeleteFile(backupPath);
                    }
                    else
                    {
                        File.Move(tempPath, SettingsPath);
                    }
                }
                catch (Exception ex)
                {
                    TryDeleteFile(SettingsPath + ".tmp");
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
            ReconnectCooldownSeconds = Clamp(ReconnectCooldownSeconds, 0, 10);
            DisconnectTimeoutSeconds = Clamp(DisconnectTimeoutSeconds, 5, 60);
            OverlayOpacityPercent = Clamp(OverlayOpacityPercent, 15, 100);
            OverlayBackgroundOpacityPercent = Clamp(OverlayBackgroundOpacityPercent, 0, 100);
            OverlayScalePercent = Clamp(OverlayScalePercent, 75, 150);

            if (Hotkey == Key.F12 || (Hotkey != Key.None && HotkeyModifiers == HotkeyModifiers.None))
            {
                Hotkey = Key.R;
                HotkeyModifiers = HotkeyModifiers.Control | HotkeyModifiers.Shift;
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
}
