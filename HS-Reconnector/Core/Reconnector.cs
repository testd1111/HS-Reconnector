using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using HS_Reconnector.Native;

namespace HS_Reconnector.Core
{
    public enum ConnectionStatus
    {
        Connected,
        Disconnected
    }

    internal sealed class Reconnector
    {
        private const string HsProcessName = "Hearthstone";
        private bool tcpInspectionErrorReported;

        public ConnectionStatus Status { get; set; } = ConnectionStatus.Connected;

        public void ResumeConnect()
        {
            Status = ConnectionStatus.Connected;
            AppLogger.Info("Connection status resumed to Connected.");
        }

        public bool Disconnect(string addr, ushort port)
        {
            IntPtr hsTcpRowPtr = IntPtr.Zero;

            try
            {
                if (!TryGetReconnectTcp(addr, port, out var hsTcpRow))
                {
                    AppLogger.Error("No exact established Hearthstone TCP connection found.");
                    return false;
                }

                hsTcpRow.state = (uint)Iphlpapi.MIB_TCP_STATE.MIB_TCP_STATE_DELETE_TCB;
                hsTcpRowPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Iphlpapi.MIB_TCPROW)));
                Marshal.StructureToPtr(hsTcpRow, hsTcpRowPtr, false);

                var ret = (Iphlpapi.SetTcpErrorCode)Iphlpapi.SetTcpEntry(hsTcpRowPtr);
                switch (ret)
                {
                    case Iphlpapi.SetTcpErrorCode.NO_ERROR:
                        AppLogger.Info(string.Format("Disconnected Hearthstone TCP socket ({0}:{1}).", addr, port));
                        Status = ConnectionStatus.Disconnected;
                        return true;
                    case Iphlpapi.SetTcpErrorCode.ERROR_ACCESS_DENIED:
                        AppLogger.Error("SetTcpEntry failed: Access denied.");
                        break;
                    case Iphlpapi.SetTcpErrorCode.ERROR_INVALID_PARAMETER:
                        AppLogger.Error("SetTcpEntry failed: Invalid parameter.");
                        break;
                    case Iphlpapi.SetTcpErrorCode.ERROR_NOT_ELEVATED:
                        AppLogger.Error("SetTcpEntry failed: Administrator privileges are required.");
                        break;
                    case Iphlpapi.SetTcpErrorCode.ERROR_NOT_SUPPORTED:
                        AppLogger.Error("SetTcpEntry failed: Not supported.");
                        break;
                    default:
                        AppLogger.Error("SetTcpEntry failed: " + ret);
                        break;
                }
                return false;
            }
            catch (Exception ex)
            {
                AppLogger.Error("TCP disconnect failed: " + ex.Message);
                return false;
            }
            finally
            {
                if (hsTcpRowPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(hsTcpRowPtr);
            }
        }

        public bool HasEstablishedConnection(string addr, ushort port)
        {
            return CountEstablishedConnections(addr, port) > 0;
        }

        public int CountEstablishedConnections(string addr, ushort port)
        {
            if (string.IsNullOrWhiteSpace(addr)
                || port == 0
                || !IPAddress.TryParse(addr, out var expectedAddress)
                || expectedAddress.AddressFamily != AddressFamily.InterNetwork)
                return 0;

            var hsProcesses = Array.Empty<Process>();
            try
            {
                hsProcesses = Process.GetProcessesByName(HsProcessName);
                var count = FindMatchingTcpConnections(
                    Iphlpapi.GetAllTCPConnections(),
                    hsProcesses,
                    expectedAddress,
                    port,
                    out _);
                tcpInspectionErrorReported = false;
                return count;
            }
            catch (Exception ex)
            {
                if (!tcpInspectionErrorReported)
                {
                    tcpInspectionErrorReported = true;
                    AppLogger.Error("Could not inspect Hearthstone TCP state: " + ex.Message);
                }
                return 0;
            }
            finally
            {
                foreach (var process in hsProcesses)
                    process.Dispose();
            }
        }

        private bool TryGetReconnectTcp(string addr, ushort port, out Iphlpapi.MIB_TCPROW hsTcpRow)
        {
            hsTcpRow = new Iphlpapi.MIB_TCPROW();

            if (string.IsNullOrWhiteSpace(addr) || port == 0)
            {
                AppLogger.Error("Game server endpoint is not available. Disconnect cancelled.");
                return false;
            }

            if (!IPAddress.TryParse(addr, out var expectedAddress)
                || expectedAddress.AddressFamily != AddressFamily.InterNetwork)
            {
                AppLogger.Error(string.Format("Unsupported non-IPv4 game server endpoint '{0}:{1}'.", addr, port));
                return false;
            }

            var tcprows = Iphlpapi.GetAllTCPConnections();
            var hsProcesses = Process.GetProcessesByName(HsProcessName);

            try
            {
                var count = FindMatchingTcpConnections(tcprows, hsProcesses, expectedAddress, port, out var selected);
                if (count != 1)
                {
                    AppLogger.Error(string.Format(
                        "Expected 1 Hearthstone TCP connection for {0}:{1}, found {2}. Disconnect cancelled.",
                        expectedAddress,
                        port,
                        count));
                    return false;
                }

                hsTcpRow.localAddr = selected.localAddr;
                hsTcpRow.localPort = selected.localPort;
                hsTcpRow.remoteAddr = selected.remoteAddr;
                hsTcpRow.remotePort = selected.remotePort;
                return true;
            }
            finally
            {
                foreach (var process in hsProcesses)
                    process.Dispose();
            }
        }

        private static int FindMatchingTcpConnections(
            List<Iphlpapi.MIB_TCPROW_OWNER_PID> tcprows,
            Process[] processes,
            IPAddress expectedAddress,
            ushort port,
            out Iphlpapi.MIB_TCPROW_OWNER_PID firstMatch)
        {
            firstMatch = default;
            var pidToName = new Dictionary<uint, string>();
            int currentSessionId;

            using (var currentProcess = Process.GetCurrentProcess())
                currentSessionId = currentProcess.SessionId;

            foreach (var process in processes)
            {
                try
                {
                    if (process.SessionId == currentSessionId)
                        pidToName[(uint)process.Id] = process.ProcessName;
                }
                catch
                {
                    // Process exited or session cannot be queried.
                }
            }

            var matchCount = 0;
            foreach (var candidate in tcprows)
            {
                if (pidToName.ContainsKey(candidate.ProcessId)
                    && candidate.State == Iphlpapi.MIB_TCP_STATE.MIB_TCP_STATE_ESTAB
                    && candidate.RemotePort == port
                    && candidate.RemoteAddress.Equals(expectedAddress))
                {
                    if (matchCount == 0)
                        firstMatch = candidate;
                    matchCount++;
                }
            }

            return matchCount;
        }
    }
}
