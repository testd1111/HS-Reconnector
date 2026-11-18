using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Input;
using System.Windows.Threading;
using HS_Reconnector.Core;
using HS_Reconnector.LogHandler;

class TestHarness
{
    static int failures;

    sealed class FakeSink : ILogEndpointSink
    {
        public string Address;
        public ushort Port;
        public bool Unsupported;
        public int ClearedCount;
        public Action OnInitialEndpoint;

        public bool SetRemoteEndpoint(string address, ushort port, string source)
        {
            System.Net.IPAddress parsed;
            if (!System.Net.IPAddress.TryParse(address, out parsed)
                || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
                || port == 0)
            {
                Address = null;
                Port = 0;
                Unsupported = true;
                return false;
            }
            if (source == "initial log scan")
                OnInitialEndpoint?.Invoke();
            Address = parsed.ToString();
            Port = port;
            Unsupported = false;
            return true;
        }

        public void ClearRemoteEndpointForMatchEnd(string reason)
        {
            Address = null;
            Port = 0;
            Unsupported = false;
            ClearedCount++;
        }
    }

    static void Check(bool ok, string msg)
    {
        if (!ok) throw new Exception(msg);
    }

    static void Test(string name, Action action)
    {
        try
        {
            action();
            Console.WriteLine("PASS " + name);
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine("FAIL " + name + ": " + ex.GetBaseException().Message);
        }
    }

    [STAThread]
    static int Main()
    {
        AppLogger.DisableFileLogging = true;
        Test("parses real Hearthstone GotoGameServe line with leading space after =", () =>
        {
            var sink = new FakeSink();
            var handler = new ConnectionLogHandler();
            handler.Handle("I 23:10:14.3149554 [GameNetLogger] Network.GotoGameServe() - address= 198.51.100.42:1119, game=10001", sink);
            Check(sink.Address == "198.51.100.42" && sink.Port == 1119 && !sink.Unsupported, "endpoint not parsed");
        });

        Test("unbracketed IPv6 log entry invalidates cached IPv4", () =>
        {
            CheckUnsupportedEndpoint("2001:db8::1:1119");
        });

        Test("port 0 log entry invalidates cached IPv4", () =>
        {
            CheckUnsupportedEndpoint("203.0.113.20:0");
        });

        Test("EndGameScreen clears active endpoint", () =>
        {
            var sink = new FakeSink();
            var handler = new ConnectionLogHandler();
            handler.Handle("Network.GotoGameServe() address=203.0.113.10:1119", sink);
            handler.Handle("I 23:38:28.7577792 [GameNetLogger] Network.DisconnectFromGameServer() - Reason: EndGameScreen ", sink);
            Check(sink.Address == null && sink.ClearedCount == 1, "match end did not clear endpoint");
        });

        Test("initial scan ignores EndGameScreen-ended match", () =>
        {
            var text = "Network.GotoGameServe() address=203.0.113.10:1119\nNetwork.DisconnectFromGameServer() - Reason: EndGameScreen\n";
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(text)))
            {
                var found = LogWatcher.TryFindLatestEndpointWithEvent(stream, stream.Length, new ConnectionLogHandler(),
                    out _, out _, out var ended, out _, out _);
                Check(!found && ended, "ended match endpoint should not be selected");
            }
        });

        Test("initial scan ignores unterminated line and stops at unbracketed IPv6", () =>
        {
            var text = "Network.GotoGameServe() address=203.0.113.10:1119\nNetwork.GotoGameServe() address=2001:db8::1:1119\nNetwork.GotoGameServe() address=203.0.113.20:1";
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(text)))
            {
                var found = LogWatcher.TryFindLatestEndpointWithEvent(stream, stream.Length, new ConnectionLogHandler(),
                    out var host, out var port, out _, out _, out _);
                Check(found && host == "2001:db8::1" && port == 1119, "scan fell back past IPv6");
            }
        });

        Test("stream reader completes partial line and recovers from truncation", () =>
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "Network.GotoGameServe() address=203.0.113.20:1", new UTF8Encoding(false));
                using (var reader = new LogReader(path))
                {
                    string lastLine = null;
                    reader.OnNewLine += x => lastLine = x;
                    var readMethod = typeof(LogReader).GetMethod("ReadAvailableLines", BindingFlags.NonPublic | BindingFlags.Instance);
                    readMethod.Invoke(reader, null);
                    Check(lastLine == null, "partial line emitted prematurely");
                    File.AppendAllText(path, "119 padding-before-truncation\n", new UTF8Encoding(false));
                    readMethod.Invoke(reader, null);
                    Check(lastLine != null && lastLine.Contains(":1119"), "completed line missed");

                    // Truncate file to shorter length and write a fresh line
                    File.WriteAllText(path, "Network.GotoGameServe() address=203.0.113.30:1119\n", new UTF8Encoding(false));
                    readMethod.Invoke(reader, null);
                    Check(lastLine != null && lastLine.Contains("203.0.113.30:1119"), "truncated file not re-read");
                }
            }
            finally { File.Delete(path); }
        });

        Test("log overwrite to >= old length resets endpoint and reads from start", () =>
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "Network.GotoGameServe() address=203.0.113.10:1119\n", new UTF8Encoding(false));
                var sink = new FakeSink();
                var handler = new ConnectionLogHandler();
                using (var reader = new LogReader(path))
                {
                    var resetFired = false;
                    reader.OnReset += () =>
                    {
                        resetFired = true;
                        sink.ClearRemoteEndpointForMatchEnd("log reset");
                    };
                    reader.OnNewLine += line => handler.Handle(line, sink);

                    var readMethod = typeof(LogReader).GetMethod("ReadAvailableLines", BindingFlags.NonPublic | BindingFlags.Instance);
                    readMethod.Invoke(reader, null);
                    Check(sink.Address == "203.0.113.10" && sink.Port == 1119, "initial endpoint not read");

                    // Overwrite with a LARGER file whose new endpoint is near byte 0 (below old offset)
                    var padding = new string('x', 200);
                    File.WriteAllText(path, "Network.GotoGameServe() address=203.0.113.99:3724\n" + padding + "\n", new UTF8Encoding(false));
                    readMethod.Invoke(reader, null);
                    Check(resetFired, "OnReset not fired on >= length overwrite");
                    Check(sink.Address == "203.0.113.99" && sink.Port == 3724, "new endpoint at start of overwritten file missed");

                    // Overwrite with a file containing NO match endpoint: old endpoint must remain cleared
                    File.WriteAllText(path, "I 00:00:01.0000000 Fresh session without match\n" + padding + "\n", new UTF8Encoding(false));
                    readMethod.Invoke(reader, null);
                    Check(sink.Address == null && sink.Port == 0, "stale endpoint survived log overwrite without match");
                }
            }
            finally { File.Delete(path); }
        });

        Test("timestamp-only change does not replay a large log", () =>
        {
            var path = Path.GetTempFileName();
            try
            {
                const long size = 17 * 1024 * 1024;
                using (var stream = File.OpenWrite(path))
                    stream.SetLength(size);
                using (var reader = new LogReader(path, size))
                {
                    typeof(LogReader).GetField("offset", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(reader, size);
                    var resets = 0;
                    reader.OnReset += () => resets++;
                    var read = typeof(LogReader).GetMethod("ReadAvailableLines", BindingFlags.NonPublic | BindingFlags.Instance);
                    read.Invoke(reader, null);
                    File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(5));
                    read.Invoke(reader, null);
                    Check(resets == 0, "timestamp change reset the reader");
                }
            }
            finally { File.Delete(path); }
        });

        Test("same-length middle overwrite and file replacement reset and re-read endpoint", () =>
        {
            var path = Path.GetTempFileName();
            try
            {
                var head = new string('H', 600) + "\n";
                var tail = new string('T', 600) + "\n";
                var lineA = "Network.GotoGameServe() address=203.0.113.10:1119\n";
                var lineB = "Network.GotoGameServe() address=203.0.113.20:3724\n";
                var lineBlank = new string('X', lineA.Length - 1) + "\n";
                Check(lineA.Length == lineB.Length && lineA.Length == lineBlank.Length, "test lines must match length");

                File.WriteAllText(path, head + lineA + tail, new UTF8Encoding(false));
                var sink = new FakeSink();
                var handler = new ConnectionLogHandler();
                using (var reader = new LogReader(path))
                {
                    var resetCount = 0;
                    reader.OnReset += () =>
                    {
                        resetCount++;
                        sink.ClearRemoteEndpointForMatchEnd("log reset");
                    };
                    reader.OnNewLine += line => handler.Handle(line, sink);

                    var readMethod = typeof(LogReader).GetMethod("ReadAvailableLines", BindingFlags.NonPublic | BindingFlags.Instance);
                    readMethod.Invoke(reader, null);
                    Check(sink.Address == "203.0.113.10" && sink.Port == 1119, "initial endpoint not read");

                    // 1. Same-length overwrite changing only the middle line (even with preserved head/tail 600B)
                    Thread.Sleep(25);
                    File.WriteAllText(path, head + lineB + tail, new UTF8Encoding(false));
                    readMethod.Invoke(reader, null);
                    Check(resetCount == 1, "same-length middle overwrite did not reset");
                    Check(sink.Address == "203.0.113.20" && sink.Port == 3724, "new middle endpoint not picked up");

                    // 2. Overwrite in place WITH growth while head/tail around old offset look valid, modifying matched line
                    File.WriteAllText(path, head + lineBlank + tail + "extra_growth_bytes\n", new UTF8Encoding(false));
                    readMethod.Invoke(reader, null);
                    Check(resetCount == 2, "in-place middle overwrite with growth did not reset");
                    Check(sink.Address == null && sink.Port == 0, "cleared middle endpoint survived");

                    // 3. Atomic file replacement at same path with identical content & timestamp
                    File.WriteAllText(path, head + lineA + tail, new UTF8Encoding(false));
                    readMethod.Invoke(reader, null);
                    var countBeforeReplace = resetCount;
                    var replacement = path + ".new";
                    File.WriteAllText(replacement, head + lineA + tail, new UTF8Encoding(false));
                    File.SetLastWriteTimeUtc(replacement, File.GetLastWriteTimeUtc(path));
                    File.Replace(replacement, path, null);
                    readMethod.Invoke(reader, null);
                    Check(resetCount == countBeforeReplace + 1, "file replacement with identical bytes did not reset");
                    Check(sink.Address == "203.0.113.10" && sink.Port == 1119, "re-read after file replacement failed");
                }
            }
            finally { File.Delete(path); }
        });

        Test("watcher re-reads a log replaced between initial scan and live reader", () =>
        {
            var path = Path.GetTempFileName();
            var replacement = path + ".new";
            try
            {
                var endpoint = "Network.GotoGameServe() address=203.0.113.10:1119\n";
                var matchEnd = "Network.DisconnectFromGameServer() Reason: EndGameScreen\n";
                var previousLog = endpoint + new string('x', matchEnd.Length - 1) + "\n";
                File.WriteAllText(path, previousLog, new UTF8Encoding(false));
                File.WriteAllText(replacement, endpoint + matchEnd, new UTF8Encoding(false));
                File.SetLastWriteTimeUtc(replacement, File.GetLastWriteTimeUtc(path));

                var sink = new FakeSink
                {
                    OnInitialEndpoint = () => File.Replace(replacement, path, null)
                };
                using (var watcher = new LogWatcher(sink, path))
                {
                    Check(watcher.StartAsync().GetAwaiter().GetResult(), "watcher failed to start");
                    Check(SpinWait.SpinUntil(() => Volatile.Read(ref sink.ClearedCount) == 2, 3000),
                        "replacement was not reset and read through match end");
                    Check(sink.Address == null, "finished match left a stale endpoint");
                }
            }
            finally
            {
                File.Delete(replacement);
                File.Delete(path);
            }
        });

        Test("watcher rejects missing log file", () =>
        {
            var sink = new FakeSink();
            using (var watcher = new LogWatcher(sink, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log")))
            {
                Check(!watcher.StartAsync().GetAwaiter().GetResult(), "missing file should fail startup");
            }
        });

        Test("settings normalize clamps bounds and rejects F12", () =>
        {
            var s = new AppSettings
            {
                ReconnectCooldownSeconds = 99,
                DisconnectTimeoutSeconds = 1,
                OverlayOpacityPercent = 1,
                OverlayBackgroundOpacityPercent = -5,
                OverlayScalePercent = 999,
                Hotkey = Key.F12,
                HotkeyModifiers = HotkeyModifiers.Control
            };
            s.Normalize();
            Check(s.ReconnectCooldownSeconds == 10 && s.DisconnectTimeoutSeconds == 5, "timers not clamped");
            Check(s.OverlayOpacityPercent == 15 && s.OverlayBackgroundOpacityPercent == 0 && s.OverlayScalePercent == 150, "overlay bounds not clamped");
            Check(s.Hotkey == Key.F10 && s.HotkeyModifiers == (HotkeyModifiers.Control | HotkeyModifiers.Alt), "F12 not reset");
        });

        Test("HearthstoneLocator finds newest session log", () =>
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "hs-loc-" + Guid.NewGuid().ToString("N"));
            try
            {
                var d1 = Directory.CreateDirectory(Path.Combine(tempRoot, "Logs", "Hearthstone_1"));
                File.WriteAllText(Path.Combine(d1.FullName, "Hearthstone.log"), "old");
                Thread.Sleep(30);
                var d2 = Directory.CreateDirectory(Path.Combine(tempRoot, "Logs", "Hearthstone_2"));
                var expected = Path.Combine(d2.FullName, "Hearthstone.log");
                File.WriteAllText(expected, "new");

                var found = HearthstoneLocator.FindLatestLogFile(tempRoot);
                Check(string.Equals(found, expected, StringComparison.OrdinalIgnoreCase), "did not pick newest log");

                Thread.Sleep(30);
                Directory.CreateDirectory(Path.Combine(tempRoot, "Logs", "Hearthstone_3_empty"));
                var foundAfterEmpty = HearthstoneLocator.FindLatestLogFile(tempRoot);
                Check(string.Equals(foundAfterEmpty, expected, StringComparison.OrdinalIgnoreCase), "empty newer dir broke log lookup");
                var gameStarted = File.GetLastWriteTimeUtc(expected).AddSeconds(1);
                Check(HearthstoneLocator.FindLatestLogFile(tempRoot, gameStarted) == null, "old session log was accepted");
                File.SetLastWriteTimeUtc(expected, gameStarted);
                Check(HearthstoneLocator.FindLatestLogFile(tempRoot, gameStarted) == expected, "current session log was rejected");
            }
            finally
            {
                if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
            }
        });

        Test("settings round trip preserves malformed XML before replacing it", () =>
        {
            var path = Path.GetTempFileName();
            const string invalid = "<AppSettings><UseMiniOverlay>";
            try
            {
                File.WriteAllText(path, invalid);
                AppSettings.Load(path);
                AppSettings.Instance.UseMiniOverlay = true;
                AppSettings.Save(path);
                var backups = Directory.GetFiles(Path.GetDirectoryName(path), Path.GetFileName(path) + ".*.corrupt");
                Check(backups.Length == 1 && File.ReadAllText(backups[0]) == invalid, "invalid XML was not preserved");
                AppSettings.Instance.UseMiniOverlay = false;
                AppSettings.Load(path);
                Check(AppSettings.Instance.UseMiniOverlay, "saved overlay setting was not restored");
            }
            finally
            {
                foreach (var backup in Directory.GetFiles(Path.GetDirectoryName(path), Path.GetFileName(path) + ".*.corrupt"))
                    File.Delete(backup);
                File.Delete(path);
            }
        });

        Test("network overlay defaults off and detects the latest known region from the log", () =>
        {
            var path = Path.GetTempFileName();
            try
            {
                Check(!new AppSettings().NetworkOverlay.Enabled, "network overlay enabled by default");
                File.WriteAllText(path, "Connecting to server. address=us.actual.battle.net port=1119\n"
                    + "Connecting to server. address=kr.actual.battle.net port=1119\n"
                    + "Connecting to server. address=eu.actual.battle.net.example.org port=1119\n");
                Check(NetworkProbe.ReadRegion(path) == "ASIA", "incorrect region or address suffix not rejected");
                foreach (var sample in new[] { ("EU", "EU"), ("US", "US"), ("KR", "ASIA"),
                    ("TW", "ASIA"), ("ASIA", "ASIA"), ("CN", "CHINA"), ("CHINA", "CHINA") })
                {
                    File.WriteAllText(path, "Region: " + sample.Item1 + "\n");
                    Check(NetworkProbe.ReadRegion(path) == sample.Item2, "unexpected region for " + sample.Item1);
                }
                File.WriteAllText(path, "Network.GotoGameServe() address=192.0.2.1:1119\n");
                Check(NetworkProbe.ReadRegion(path) == null, "guessed a region from an IP");
            }
            finally { File.Delete(path); }
        });

        Test("TCP probe measures a successful loopback connection", () =>
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
                Check(NetworkProbe.MeasureAsync(IPAddress.Loopback, port).GetAwaiter().GetResult().HasValue,
                    "loopback connection was not measured");
            }
            finally { listener.Stop(); }
        });

        Console.WriteLine("Failures: " + failures);
        return failures == 0 ? 0 : 1;
    }

    static void CheckUnsupportedEndpoint(string endpoint)
    {
        using (var controller = new ReconnectController(Dispatcher.CurrentDispatcher))
        {
            var handler = new ConnectionLogHandler();
            handler.Handle("Network.GotoGameServe() address=203.0.113.10:1119", controller);
            Check(controller.GetSnapshot().RemoteAddress == "203.0.113.10", "initial IPv4 not set");
            handler.Handle("Network.GotoGameServe() address=" + endpoint, controller);
            var snapshot = controller.GetSnapshot();
            Check(snapshot.RemoteAddress == null && snapshot.RemotePort == 0 && snapshot.EndpointUnsupported,
                "invalid endpoint did not clear the cached IPv4");
        }
    }
}
