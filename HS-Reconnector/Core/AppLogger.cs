using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HS_Reconnector.Core
{
    internal static class AppLogger
    {
        private const int MaxEntries = 250;
        private static readonly object Sync = new object();
        private static readonly List<string> Entries = new List<string>(MaxEntries);

        public static bool DisableFileLogging { get; set; }
        public static event Action<string> EntryAdded;

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);
        public static void Error(string message) => Write("ERROR", message);
        public static void Error(Exception ex) => Write("ERROR", ex?.ToString() ?? "<null>");

        public static IReadOnlyList<string> GetSnapshot()
        {
            lock (Sync)
                return Entries.ToArray();
        }

        public static void Clear()
        {
            lock (Sync)
                Entries.Clear();
        }

        private static void Write(string level, string message)
        {
            var line = string.Format("[{0:HH:mm:ss}] [{1}] {2}", DateTime.Now, level, message ?? string.Empty);

            lock (Sync)
            {
                if (Entries.Count >= MaxEntries)
                    Entries.RemoveAt(0);
                Entries.Add(line);
            }

            if (!DisableFileLogging)
            {
                try
                {
                    var logDir = AppSettings.DataDirectory;
                    Directory.CreateDirectory(logDir);
                    var logPath = Path.Combine(logDir, "hs-reconnector.log");
                    TrimIfOversized(logPath);
                    File.AppendAllText(logPath, line + Environment.NewLine, new UTF8Encoding(false));
                }
                catch
                {
                    // Logging must never crash the application.
                }
            }

            EntryAdded?.Invoke(line);
        }

        private static void TrimIfOversized(string logPath)
        {
            var info = new FileInfo(logPath);
            if (!info.Exists || info.Length <= 512 * 1024)
                return;

            var backup = logPath + ".old";
            if (File.Exists(backup))
                File.Delete(backup);
            File.Move(logPath, backup);
        }
    }
}
