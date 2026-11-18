using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using HS_Reconnector.Native;
using Microsoft.Win32;

namespace HS_Reconnector.Core
{
    internal static class HearthstoneLocator
    {
        private const string HearthstoneProcessName = "Hearthstone";
        private const string LogSearchPattern = "Hearthstone.log";

        public static bool IsHearthstoneInForeground()
        {
            var processId = Win32.GetForegroundProcessId();
            if (processId == 0)
                return false;

            return string.Equals(Path.GetFileNameWithoutExtension(Win32.TryGetProcessPath(processId)),
                HearthstoneProcessName, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsHearthstoneRunning(out DateTime startedAtUtc)
        {
            startedAtUtc = DateTime.MinValue;
            int currentSessionId;
            using (var current = Process.GetCurrentProcess())
                currentSessionId = current.SessionId;

            var processes = Process.GetProcessesByName(HearthstoneProcessName);
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        if (process.SessionId == currentSessionId && !process.HasExited)
                        {
                            startedAtUtc = process.StartTime.ToUniversalTime();
                            return true;
                        }
                    }
                    catch
                    {
                        // Process exited during inspection.
                    }
                }
                return false;
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }

        public static string FindHearthstoneDirectory(string customDirectory = null)
        {
            if (!string.IsNullOrWhiteSpace(customDirectory))
            {
                var trimmed = customDirectory.Trim();
                if (Directory.Exists(Path.Combine(trimmed, "Logs")))
                    return trimmed;
            }

            var fromProcess = TryFindFromRunningProcess();
            if (!string.IsNullOrEmpty(fromProcess))
                return fromProcess;

            var fromRegistry = TryFindFromRegistry();
            if (!string.IsNullOrEmpty(fromRegistry))
                return fromRegistry;

            foreach (var candidate in GetCommonInstallPaths())
            {
                if (!string.IsNullOrEmpty(candidate) && Directory.Exists(Path.Combine(candidate, "Logs")))
                    return candidate;
            }

            foreach (var candidate in GetCommonInstallPaths())
            {
                if (!string.IsNullOrEmpty(candidate) && Directory.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        public static string FindLatestLogFile(string hearthstoneDirectory, DateTime notBeforeUtc = default)
        {
            if (string.IsNullOrWhiteSpace(hearthstoneDirectory))
                return null;

            var logRoot = Path.Combine(hearthstoneDirectory, "Logs");
            if (!Directory.Exists(logRoot))
                return null;

            DirectoryInfo[] sessions;
            try
            {
                sessions = new DirectoryInfo(logRoot).GetDirectories();
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }

            foreach (var dir in sessions.OrderByDescending(d => d.CreationTimeUtc))
            {
                try
                {
                    var logFile = dir.EnumerateFiles(LogSearchPattern)
                        .FirstOrDefault(f => f.Exists && f.LastWriteTimeUtc >= notBeforeUtc);
                    if (logFile != null)
                        return logFile.FullName;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            var directLog = Path.Combine(logRoot, LogSearchPattern);
            try
            {
                return File.Exists(directLog) && File.GetLastWriteTimeUtc(directLog) >= notBeforeUtc ? directLog : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static string TryFindFromRunningProcess()
        {
            int currentSessionId;
            using (var current = Process.GetCurrentProcess())
                currentSessionId = current.SessionId;

            var processes = Process.GetProcessesByName(HearthstoneProcessName);
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        if (process.SessionId != currentSessionId || process.HasExited)
                            continue;

                        var exePath = Win32.TryGetProcessPath(process.Id);
                        if (!string.IsNullOrEmpty(exePath))
                        {
                            var dir = Path.GetDirectoryName(exePath);
                            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                                return dir;
                        }
                    }
                    catch
                    {
                        // Ignore inaccessible or exiting process.
                    }
                }
                return null;
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }

        private static string TryFindFromRegistry()
        {
            var registryPaths = new[]
            {
                Tuple.Create(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Hearthstone"),
                Tuple.Create(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Hearthstone"),
                Tuple.Create(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Hearthstone")
            };

            foreach (var entry in registryPaths)
            {
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(entry.Item1, RegistryView.Registry64))
                    using (var key = baseKey.OpenSubKey(entry.Item2))
                    {
                        var location = key?.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrWhiteSpace(location) && Directory.Exists(location))
                            return location;
                    }
                }
                catch
                {
                    // Ignore registry permission errors.
                }
            }

            return null;
        }

        private static IEnumerable<string> GetCommonInstallPaths()
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Hearthstone");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Hearthstone");

            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                    continue;

                yield return Path.Combine(drive.Name, "Hearthstone");
                yield return Path.Combine(drive.Name, "Games", "Hearthstone");
            }
        }
    }
}
