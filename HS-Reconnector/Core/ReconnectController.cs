using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using HS_Reconnector.LogHandler;
using HS_Reconnector.Native;

namespace HS_Reconnector.Core
{
    internal enum ReconnectVisualState
    {
        Waiting,
        Ready,
        Disconnected,
        Cooldown,
        Unsupported
    }

    internal sealed class ControllerSnapshot
    {
        public bool IsElevated { get; set; }
        public bool IsHearthstoneRunning { get; set; }
        public string HearthstoneDirectory { get; set; }
        public string ActiveLogPath { get; set; }
        public string RemoteAddress { get; set; }
        public ushort RemotePort { get; set; }
        public bool EndpointUnsupported { get; set; }
        public int EstablishedTcpCount { get; set; }
        public ConnectionStatus ConnectionStatus { get; set; }
        public ReconnectVisualState VisualState { get; set; }
        public int CooldownRemainingSeconds { get; set; }
        public bool CanReconnect { get; set; }
    }

    internal sealed class ReconnectController : ILogEndpointSink, IDisposable
    {
        private readonly Dispatcher dispatcher;
        private readonly Reconnector reconnector = new Reconnector();
        private readonly HotkeyManager hotkeyManager = new HotkeyManager();
        private readonly DispatcherTimer monitorTimer;
        private readonly DispatcherTimer disconnectTimer;
        private readonly DispatcherTimer connectionRestoreTimer;
        private readonly DispatcherTimer cooldownTimer;
        private readonly object endpointLock = new object();

        private LogWatcher logWatcher;
        private bool hasRunningWatcher;
        private bool logWatcherStartInProgress;
        private bool gameRunObserved;
        private string detectedHsDirectory;
        private string lastCustomHsDirectory;
        private string activeLogPath;
        private DateTime nextLogSearchUtc = DateTime.MinValue;
        private int logSearchRetryDelaySeconds = 2;
        private bool logSearchBackoffReported;

        private string remoteAddr;
        private ushort remotePort;
        private bool endpointUnsupported;
        private int establishedTcpCount;
        private long endpointVersion;

        private string reconnectRemoteAddr;
        private ushort reconnectRemotePort;
        private int connectionCheckInProgress;
        private int statusTcpCheckInProgress;
        private int tcpRecheckRequested;
        private long reconnectAttemptId;
        private bool cooldownPendingAfterReconnect;
        private DateTime cooldownEndsAtUtc = DateTime.MinValue;
        private bool hotkeySuspended;
        private bool disposed;

        public event Action<ControllerSnapshot> StateChanged;

        public ReconnectController(Dispatcher dispatcher)
        {
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

            hotkeyManager.Pressed += OnHotkeyPressed;

            monitorTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            monitorTimer.Tick += MonitorTimer_Tick;

            disconnectTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
            {
                Interval = TimeSpan.FromSeconds(AppSettings.Instance.DisconnectTimeoutSeconds)
            };
            disconnectTimer.Tick += DisconnectTimer_Tick;

            connectionRestoreTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            connectionRestoreTimer.Tick += ConnectionRestoreTimer_Tick;

            cooldownTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            cooldownTimer.Tick += CooldownTimer_Tick;
        }

        public void Start()
        {
            ApplySettings();
            monitorTimer.Start();
            TickMonitor();
        }

        public bool ApplySettings()
        {
            var settings = AppSettings.Instance;
            settings.Normalize();

            disconnectTimer.Interval = TimeSpan.FromSeconds(settings.DisconnectTimeoutSeconds);

            if (!string.Equals(lastCustomHsDirectory, settings.CustomHearthstoneDirectory, StringComparison.OrdinalIgnoreCase))
            {
                lastCustomHsDirectory = settings.CustomHearthstoneDirectory;
                RestartLogDiscovery("Hearthstone folder setting changed");
            }

            var hotkeyApplied = true;
            if (hotkeySuspended)
            {
                hotkeyManager.Unregister();
            }
            else if (!hotkeyManager.Register(settings.Hotkey, settings.HotkeyModifiers))
            {
                hotkeyApplied = false;
                AppLogger.Warn("Configured global hotkey could not be registered.");
            }

            NotifyStateChanged();
            return hotkeyApplied;
        }

        public void SuspendHotkey(bool suspend)
        {
            if (hotkeySuspended == suspend)
                return;

            hotkeySuspended = suspend;
            if (suspend)
                hotkeyManager.Unregister();
            else
                ApplySettings();
        }

        public void ExecuteReconnect()
        {
            if (disposed)
                return;

            if (!Win32.IsElevated())
            {
                AppLogger.Error("Reconnect cancelled: Administrator privileges are required.");
                return;
            }

            if (IsOnCooldown())
            {
                AppLogger.Info("Reconnect ignored because cooldown is active.");
                return;
            }

            if (reconnector.Status != ConnectionStatus.Connected)
            {
                AppLogger.Info("Reconnect ignored while previous disconnect is still completing.");
                return;
            }

            GetEndpointState(out var addr, out var port, out var unsupported);
            if (unsupported || string.IsNullOrWhiteSpace(addr) || port == 0)
            {
                AppLogger.Info("Reconnect ignored: no active IPv4 game server endpoint.");
                return;
            }

            AppLogger.Info(string.Format("Reconnect requested for {0}:{1}", addr, port));
            cooldownPendingAfterReconnect = false;

            if (!reconnector.Disconnect(addr, port))
            {
                RefreshTcpCountAsync();
                return;
            }

            cooldownPendingAfterReconnect = true;
            reconnectRemoteAddr = addr;
            reconnectRemotePort = port;
            Interlocked.Increment(ref reconnectAttemptId);
            lock (endpointLock)
            {
                establishedTcpCount = 0;
            }

            disconnectTimer.Stop();
            disconnectTimer.Start();
            connectionRestoreTimer.Stop();
            connectionRestoreTimer.Start();

            NotifyStateChanged();
        }

        public ControllerSnapshot GetSnapshot()
        {
            GetEndpointState(out var addr, out var port, out var unsupported, out var tcpCount, out _);
            var isElevated = Win32.IsElevated();
            var onCooldown = IsOnCooldown();
            var cooldownRemaining = onCooldown
                ? Math.Max(1, (int)Math.Ceiling((cooldownEndsAtUtc - DateTime.UtcNow).TotalSeconds))
                : 0;

            var hasEndpoint = !string.IsNullOrWhiteSpace(addr) && port != 0;
            var hasExactSocket = tcpCount == 1;

            ReconnectVisualState visualState;
            if (reconnector.Status == ConnectionStatus.Disconnected)
                visualState = ReconnectVisualState.Disconnected;
            else if (onCooldown)
                visualState = ReconnectVisualState.Cooldown;
            else if (unsupported)
                visualState = ReconnectVisualState.Unsupported;
            else if (hasEndpoint && hasExactSocket)
                visualState = ReconnectVisualState.Ready;
            else
                visualState = ReconnectVisualState.Waiting;

            var canReconnect = isElevated
                && reconnector.Status == ConnectionStatus.Connected
                && !onCooldown
                && !unsupported
                && hasEndpoint
                && hasExactSocket;

            return new ControllerSnapshot
            {
                IsElevated = isElevated,
                IsHearthstoneRunning = gameRunObserved,
                HearthstoneDirectory = detectedHsDirectory,
                ActiveLogPath = activeLogPath,
                RemoteAddress = addr,
                RemotePort = port,
                EndpointUnsupported = unsupported,
                EstablishedTcpCount = tcpCount,
                ConnectionStatus = reconnector.Status,
                VisualState = visualState,
                CooldownRemainingSeconds = cooldownRemaining,
                CanReconnect = canReconnect
            };
        }

        public bool SetRemoteEndpoint(string address, ushort port, string source)
        {
            if (!IPAddress.TryParse(address, out var parsed)
                || parsed.AddressFamily != AddressFamily.InterNetwork
                || port == 0)
            {
                lock (endpointLock)
                {
                    remoteAddr = null;
                    remotePort = 0;
                    endpointUnsupported = true;
                    establishedTcpCount = 0;
                    endpointVersion++;
                }
                AppLogger.Warn(string.Format(
                    "Unsupported non-IPv4 or invalid endpoint from {0}: {1}:{2}",
                    source,
                    address ?? "<null>",
                    port));
                NotifyStateChangedAsync();
                return false;
            }

            var normalized = parsed.ToString();
            bool changed;
            lock (endpointLock)
            {
                changed = !string.Equals(remoteAddr, normalized, StringComparison.Ordinal)
                    || remotePort != port
                    || endpointUnsupported;
                if (changed)
                {
                    remoteAddr = normalized;
                    remotePort = port;
                    endpointUnsupported = false;
                    establishedTcpCount = 0;
                    endpointVersion++;
                }
            }

            if (changed)
            {
                AppLogger.Info(string.Format("Game server endpoint ({0}): {1}:{2}", source, normalized, port));
                NotifyEndpointDetectedAsync();
            }

            return true;
        }

        public void ClearRemoteEndpointForMatchEnd(string reason)
        {
            ClearEndpoint(reason);
            if (!disposed)
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    if (disposed)
                        return;
                    if (reconnector.Status == ConnectionStatus.Disconnected)
                    {
                        cooldownPendingAfterReconnect = false;
                        ResumeConnectionState();
                    }
                    else
                    {
                        NotifyStateChanged();
                    }
                }));
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            Interlocked.Increment(ref reconnectAttemptId);
            monitorTimer.Stop();
            disconnectTimer.Stop();
            connectionRestoreTimer.Stop();
            cooldownTimer.Stop();
            hotkeyManager.Pressed -= OnHotkeyPressed;
            hotkeyManager.Dispose();

            logWatcher?.Stop();
            logWatcher = null;
        }

        private void OnHotkeyPressed()
        {
            if (disposed)
                return;

            dispatcher.BeginInvoke(new Action(ExecuteReconnect));
        }

        private void MonitorTimer_Tick(object sender, EventArgs e)
        {
            TickMonitor();
        }

        private void TickMonitor()
        {
            if (disposed)
                return;

            var isRunning = HearthstoneLocator.IsHearthstoneRunning();
            if (isRunning && !gameRunObserved)
            {
                gameRunObserved = true;
                AppLogger.Info("Hearthstone process detected.");
                if (reconnector.Status == ConnectionStatus.Disconnected)
                {
                    cooldownPendingAfterReconnect = false;
                    ResumeConnectionState();
                }
                ClearEndpoint("Hearthstone started");
                ResetLogSearchBackoff();
                nextLogSearchUtc = DateTime.UtcNow;
                NotifyStateChanged();
            }
            else if (!isRunning && gameRunObserved)
            {
                gameRunObserved = false;
                AppLogger.Info("Hearthstone process closed.");
                StopWatcher();
                disconnectTimer.Stop();
                connectionRestoreTimer.Stop();
                cooldownPendingAfterReconnect = false;
                if (reconnector.Status == ConnectionStatus.Disconnected)
                    reconnector.ResumeConnect();
                ClearEndpoint("Hearthstone stopped");
                ResetLogSearchBackoff();
                nextLogSearchUtc = DateTime.MinValue;
                NotifyStateChanged();
            }

            if (hasRunningWatcher && logWatcher != null && !logWatcher.IsHealthy)
            {
                AppLogger.Warn("Hearthstone.log became unavailable; restarting log discovery.");
                StopWatcher();
                ClearEndpoint("log watcher unavailable");
                ScheduleNextLogSearch();
                NotifyStateChanged();
            }

            if (isRunning && !logWatcherStartInProgress)
            {
                if (!hasRunningWatcher && DateTime.UtcNow >= nextLogSearchUtc)
                    TryStartLogWatcher();
                else if (hasRunningWatcher)
                    CheckForNewerLogFile();
            }

            if (reconnector.Status == ConnectionStatus.Connected)
                RefreshTcpCountAsync();
        }

        private void TryStartLogWatcher()
        {
            var hsDir = HearthstoneLocator.FindHearthstoneDirectory(AppSettings.Instance.CustomHearthstoneDirectory);
            if (!string.Equals(detectedHsDirectory, hsDir, StringComparison.OrdinalIgnoreCase))
            {
                detectedHsDirectory = hsDir;
                NotifyStateChanged();
            }

            if (string.IsNullOrEmpty(hsDir))
            {
                ScheduleNextLogSearch();
                return;
            }

            var logPath = HearthstoneLocator.FindLatestLogFile(hsDir);
            if (string.IsNullOrEmpty(logPath))
            {
                ScheduleNextLogSearch();
                return;
            }

            StartLogWatcherForPath(logPath);
        }

        private void CheckForNewerLogFile()
        {
            var hsDir = !string.IsNullOrEmpty(detectedHsDirectory)
                ? detectedHsDirectory
                : HearthstoneLocator.FindHearthstoneDirectory(AppSettings.Instance.CustomHearthstoneDirectory);

            var latestLogPath = HearthstoneLocator.FindLatestLogFile(hsDir);
            if (string.IsNullOrEmpty(latestLogPath)
                || string.Equals(latestLogPath, activeLogPath, StringComparison.OrdinalIgnoreCase)
                || !IsNewerLogFile(latestLogPath, activeLogPath))
            {
                return;
            }

            AppLogger.Info("Switching to newer Hearthstone log: " + latestLogPath);
            StopWatcher();
            ClearEndpoint("newer Hearthstone log detected");
            ResetLogSearchBackoff();
            StartLogWatcherForPath(latestLogPath);
        }

        private void StartLogWatcherForPath(string logPath)
        {
            var watcher = new LogWatcher(this, logPath);
            logWatcher = watcher;
            activeLogPath = logPath;
            logWatcherStartInProgress = true;
            _ = StartLogWatcherAsync(watcher);
        }

        private static bool IsNewerLogFile(string candidatePath, string currentPath)
        {
            if (string.IsNullOrEmpty(currentPath) || !File.Exists(currentPath))
                return true;

            return GetLogTimestampUtc(candidatePath) > GetLogTimestampUtc(currentPath);
        }

        private static DateTime GetLogTimestampUtc(string logPath)
        {
            var dir = Path.GetDirectoryName(logPath);
            var fileTime = File.GetCreationTimeUtc(logPath);
            var dirTime = !string.IsNullOrEmpty(dir) ? Directory.GetCreationTimeUtc(dir) : DateTime.MinValue;
            return fileTime >= dirTime ? fileTime : dirTime;
        }

        private async Task StartLogWatcherAsync(LogWatcher watcher)
        {
            var started = false;
            try
            {
                started = await watcher.StartAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Unexpected log watcher startup error: " + ex.Message);
                watcher.Stop();
            }
            finally
            {
                if (!disposed && ReferenceEquals(logWatcher, watcher))
                {
                    if (started)
                    {
                        hasRunningWatcher = true;
                        ResetLogSearchBackoff();
                    }
                    else
                    {
                        logWatcher = null;
                        activeLogPath = null;
                        hasRunningWatcher = false;
                        ScheduleNextLogSearch();
                    }
                }
                else
                {
                    watcher.Stop();
                }

                logWatcherStartInProgress = false;
                NotifyStateChanged();
            }
        }

        private void RestartLogDiscovery(string reason)
        {
            StopWatcher();
            ClearEndpoint(reason);
            detectedHsDirectory = HearthstoneLocator.FindHearthstoneDirectory(AppSettings.Instance.CustomHearthstoneDirectory);
            ResetLogSearchBackoff();
            nextLogSearchUtc = DateTime.UtcNow;
        }

        private void StopWatcher()
        {
            logWatcher?.Stop();
            logWatcher = null;
            hasRunningWatcher = false;
            activeLogPath = null;
        }

        private async void ConnectionRestoreTimer_Tick(object sender, EventArgs e)
        {
            if (disposed || reconnector.Status != ConnectionStatus.Disconnected)
            {
                connectionRestoreTimer.Stop();
                return;
            }

            if (Interlocked.CompareExchange(ref connectionCheckInProgress, 1, 0) != 0)
                return;

            var attemptId = Interlocked.Read(ref reconnectAttemptId);
            var previousAddr = reconnectRemoteAddr;
            var previousPort = reconnectRemotePort;
            GetEndpointState(out var currentAddr, out var currentPort, out var currentUnsupported, out _, out var capturedVersion);

            var hasCurrent = !currentUnsupported && !string.IsNullOrWhiteSpace(currentAddr) && currentPort != 0;
            var hasDifferentPrevious = !string.IsNullOrWhiteSpace(previousAddr)
                && previousPort != 0
                && (!hasCurrent || !string.Equals(previousAddr, currentAddr, StringComparison.Ordinal) || previousPort != currentPort);

            try
            {
                var pollResult = await Task.Run(() =>
                {
                    var currentCount = hasCurrent ? reconnector.CountEstablishedConnections(currentAddr, currentPort) : 0;
                    var restored = currentCount > 0
                        || (hasDifferentPrevious && reconnector.HasEstablishedConnection(previousAddr, previousPort));
                    return Tuple.Create(restored, currentCount);
                });

                if (disposed
                    || attemptId != Interlocked.Read(ref reconnectAttemptId)
                    || reconnector.Status != ConnectionStatus.Disconnected
                    || !pollResult.Item1)
                    return;

                AppLogger.Info("Hearthstone TCP connection restored.");
                bool needsRecheck;
                lock (endpointLock)
                {
                    if (capturedVersion == endpointVersion)
                    {
                        establishedTcpCount = pollResult.Item2;
                        needsRecheck = false;
                    }
                    else
                    {
                        establishedTcpCount = 0;
                        needsRecheck = true;
                    }
                }
                ResumeConnectionState();
                if (needsRecheck)
                    RefreshTcpCountAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Error("TCP connection poll failed: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref connectionCheckInProgress, 0);
            }
        }

        private void DisconnectTimer_Tick(object sender, EventArgs e)
        {
            disconnectTimer.Stop();
            connectionRestoreTimer.Stop();
            if (disposed)
                return;

            AppLogger.Warn("Disconnect timeout elapsed; restoring Ready state.");
            ResumeConnectionState();
            RefreshTcpCountAsync();
        }

        private void CooldownTimer_Tick(object sender, EventArgs e)
        {
            if (!IsOnCooldown())
                cooldownTimer.Stop();

            NotifyStateChanged();
        }

        private void ResumeConnectionState()
        {
            var startCooldown = cooldownPendingAfterReconnect;
            cooldownPendingAfterReconnect = false;
            disconnectTimer.Stop();
            connectionRestoreTimer.Stop();
            Interlocked.Increment(ref reconnectAttemptId);
            reconnectRemoteAddr = null;
            reconnectRemotePort = 0;
            reconnector.ResumeConnect();

            if (startCooldown && AppSettings.Instance.ReconnectCooldownSeconds > 0)
            {
                cooldownEndsAtUtc = DateTime.UtcNow.AddSeconds(AppSettings.Instance.ReconnectCooldownSeconds);
                cooldownTimer.Start();
            }
            else
            {
                cooldownEndsAtUtc = DateTime.MinValue;
                cooldownTimer.Stop();
            }

            NotifyStateChanged();
        }

        private async void RefreshTcpCountAsync()
        {
            GetEndpointState(out var addr, out var port, out var unsupported, out _, out var targetVersion);
            if (unsupported || string.IsNullOrWhiteSpace(addr) || port == 0)
                return;

            if (Interlocked.CompareExchange(ref statusTcpCheckInProgress, 1, 0) != 0)
            {
                Volatile.Write(ref tcpRecheckRequested, 1);
                return;
            }

            var attemptId = Interlocked.Read(ref reconnectAttemptId);
            try
            {
                var count = await Task.Run(() => reconnector.CountEstablishedConnections(addr, port));
                bool changed = false;
                lock (endpointLock)
                {
                    if (!disposed
                        && reconnector.Status == ConnectionStatus.Connected
                        && attemptId == Interlocked.Read(ref reconnectAttemptId)
                        && targetVersion == endpointVersion
                        && count != establishedTcpCount)
                    {
                        establishedTcpCount = count;
                        changed = true;
                    }
                }
                if (changed)
                    NotifyStateChanged();
            }
            finally
            {
                Interlocked.Exchange(ref statusTcpCheckInProgress, 0);
                if (!disposed
                    && Interlocked.Exchange(ref tcpRecheckRequested, 0) != 0
                    && reconnector.Status == ConnectionStatus.Connected)
                {
                    RefreshTcpCountAsync();
                }
            }
        }

        private void ClearEndpoint(string reason)
        {
            bool hadEndpoint;
            lock (endpointLock)
            {
                hadEndpoint = !string.IsNullOrEmpty(remoteAddr) || remotePort != 0 || endpointUnsupported;
                remoteAddr = null;
                remotePort = 0;
                endpointUnsupported = false;
                establishedTcpCount = 0;
                endpointVersion++;
            }

            if (hadEndpoint)
                AppLogger.Info("Cleared game server endpoint: " + reason);
        }

        private void GetEndpointState(out string addr, out ushort port, out bool unsupported)
        {
            lock (endpointLock)
            {
                addr = remoteAddr;
                port = remotePort;
                unsupported = endpointUnsupported;
            }
        }

        private void GetEndpointState(out string addr, out ushort port, out bool unsupported, out int tcpCount, out long version)
        {
            lock (endpointLock)
            {
                addr = remoteAddr;
                port = remotePort;
                unsupported = endpointUnsupported;
                tcpCount = establishedTcpCount;
                version = endpointVersion;
            }
        }

        private bool IsOnCooldown()
        {
            return AppSettings.Instance.ReconnectCooldownSeconds > 0
                && DateTime.UtcNow < cooldownEndsAtUtc;
        }

        private void ScheduleNextLogSearch()
        {
            nextLogSearchUtc = DateTime.UtcNow.AddSeconds(logSearchRetryDelaySeconds);
            if (logSearchRetryDelaySeconds >= 30)
            {
                if (!logSearchBackoffReported)
                {
                    AppLogger.Info("Hearthstone.log is still unavailable; checking every 30 seconds.");
                    logSearchBackoffReported = true;
                }
                return;
            }

            if (logSearchRetryDelaySeconds < 5)
                logSearchRetryDelaySeconds = 5;
            else if (logSearchRetryDelaySeconds < 10)
                logSearchRetryDelaySeconds = 10;
            else
                logSearchRetryDelaySeconds = 30;
        }

        private void ResetLogSearchBackoff()
        {
            logSearchRetryDelaySeconds = 2;
            logSearchBackoffReported = false;
        }

        private void NotifyStateChanged()
        {
            if (!disposed)
                StateChanged?.Invoke(GetSnapshot());
        }

        private void NotifyStateChangedAsync()
        {
            if (!disposed)
                dispatcher.BeginInvoke(new Action(NotifyStateChanged));
        }

        private void NotifyEndpointDetectedAsync()
        {
            if (disposed)
                return;

            dispatcher.BeginInvoke(new Action(() =>
            {
                if (disposed)
                    return;
                if (reconnector.Status == ConnectionStatus.Connected)
                    RefreshTcpCountAsync();
                NotifyStateChanged();
            }));
        }
    }
}
