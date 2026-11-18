using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;

namespace HS_Reconnector.Native
{
    internal static class Iphlpapi
    {
        public const int AF_INET = 2;
        private const uint ERROR_INSUFFICIENT_BUFFER = 122;

        public enum MIB_TCP_STATE
        {
            MIB_TCP_STATE_CLOSED = 1,
            MIB_TCP_STATE_LISTEN = 2,
            MIB_TCP_STATE_SYN_SENT = 3,
            MIB_TCP_STATE_SYN_RCVD = 4,
            MIB_TCP_STATE_ESTAB = 5,
            MIB_TCP_STATE_FIN_WAIT1 = 6,
            MIB_TCP_STATE_FIN_WAIT2 = 7,
            MIB_TCP_STATE_CLOSE_WAIT = 8,
            MIB_TCP_STATE_CLOSING = 9,
            MIB_TCP_STATE_LAST_ACK = 10,
            MIB_TCP_STATE_TIME_WAIT = 11,
            MIB_TCP_STATE_DELETE_TCB = 12
        }

        public enum TCP_TABLE_CLASS
        {
            TCP_TABLE_BASIC_LISTENER,
            TCP_TABLE_BASIC_CONNECTIONS,
            TCP_TABLE_BASIC_ALL,
            TCP_TABLE_OWNER_PID_LISTENER,
            TCP_TABLE_OWNER_PID_CONNECTIONS,
            TCP_TABLE_OWNER_PID_ALL,
            TCP_TABLE_OWNER_MODULE_LISTENER,
            TCP_TABLE_OWNER_MODULE_CONNECTIONS,
            TCP_TABLE_OWNER_MODULE_ALL
        }

        public enum SetTcpErrorCode
        {
            NO_ERROR = 0,
            ERROR_ACCESS_DENIED = 5,
            ERROR_INVALID_PARAMETER = 87,
            ERROR_NOT_SUPPORTED = 50,
            ERROR_NOT_ELEVATED = 317
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MIB_TCPROW
        {
            public uint state;
            public uint localAddr;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public byte[] localPort;
            public uint remoteAddr;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public byte[] remotePort;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MIB_TCPROW_OWNER_PID
        {
            public uint state;
            public uint localAddr;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public byte[] localPort;
            public uint remoteAddr;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public byte[] remotePort;
            public uint owningPid;

            public uint ProcessId => owningPid;
            public IPAddress LocalAddress => new IPAddress(localAddr);
            public ushort LocalPort => BitConverter.ToUInt16(new byte[2] { localPort[1], localPort[0] }, 0);
            public IPAddress RemoteAddress => new IPAddress(remoteAddr);
            public ushort RemotePort => BitConverter.ToUInt16(new byte[2] { remotePort[1], remotePort[0] }, 0);
            public MIB_TCP_STATE State => (MIB_TCP_STATE)state;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MIB_TCPTABLE_OWNER_PID
        {
            public uint dwNumEntries;
            [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.Struct, SizeConst = 1)]
            public MIB_TCPROW_OWNER_PID[] table;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
        private delegate uint GetExtendedTcpTableDelegate(IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, TCP_TABLE_CLASS tblClass, uint reserved);

        [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
        private delegate int SetTcpEntryDelegate(IntPtr pTcprow);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        private static readonly Lazy<IntPtr> IphlpapiModule = new Lazy<IntPtr>(() =>
        {
            var handle = LoadLibrary(Path.Combine(Environment.SystemDirectory, "iphlpapi.dll"));
            if (handle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return handle;
        });

        private static readonly Lazy<GetExtendedTcpTableDelegate> GetExtendedTcpTableProc =
            new Lazy<GetExtendedTcpTableDelegate>(() => GetProc<GetExtendedTcpTableDelegate>("GetExtendedTcpTable"));

        private static readonly Lazy<SetTcpEntryDelegate> SetTcpEntryProc =
            new Lazy<SetTcpEntryDelegate>(() => GetProc<SetTcpEntryDelegate>("SetTcpEntry"));

        private static T GetProc<T>(string procName) where T : class
        {
            var proc = GetProcAddress(IphlpapiModule.Value, procName);
            if (proc == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return Marshal.GetDelegateForFunctionPointer(proc, typeof(T)) as T;
        }

        private static uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, TCP_TABLE_CLASS tblClass, uint reserved = 0)
        {
            return GetExtendedTcpTableProc.Value(pTcpTable, ref dwOutBufLen, sort, ipVersion, tblClass, reserved);
        }

        public static int SetTcpEntry(IntPtr pTcprow)
        {
            return SetTcpEntryProc.Value(pTcprow);
        }

        public static List<MIB_TCPROW_OWNER_PID> GetAllTCPConnections()
        {
            return GetTCPConnections<MIB_TCPROW_OWNER_PID, MIB_TCPTABLE_OWNER_PID>(
                AF_INET,
                TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_CONNECTIONS);
        }

        private static List<IPR> GetTCPConnections<IPR, IPT>(int ipVersion, TCP_TABLE_CLASS tableClass)
        {
            int buffSize = 0;

            for (var attempt = 0; attempt < 3; attempt++)
            {
                uint sizeResult = GetExtendedTcpTable(IntPtr.Zero, ref buffSize, false, ipVersion, tableClass);
                if (sizeResult != 0 && sizeResult != ERROR_INSUFFICIENT_BUFFER)
                    throw new Win32Exception(unchecked((int)sizeResult));
                if (buffSize <= 0)
                    return new List<IPR>();

                IntPtr tcpTablePtr = Marshal.AllocHGlobal(buffSize);
                try
                {
                    uint ret = GetExtendedTcpTable(tcpTablePtr, ref buffSize, false, ipVersion, tableClass);
                    if (ret == ERROR_INSUFFICIENT_BUFFER)
                        continue;
                    if (ret != 0)
                        throw new Win32Exception(unchecked((int)ret));

                    int numEntries = Marshal.ReadInt32(tcpTablePtr);
                    if (numEntries <= 0)
                        return new List<IPR>();

                    int rowStructSize = Marshal.SizeOf(typeof(IPR));
                    var tableRows = new IPR[numEntries];
                    IntPtr rowPtr = (IntPtr)((long)tcpTablePtr + Marshal.OffsetOf(typeof(IPT), "table").ToInt64());

                    for (int i = 0; i < numEntries; i++)
                    {
                        tableRows[i] = (IPR)Marshal.PtrToStructure(rowPtr, typeof(IPR));
                        rowPtr = (IntPtr)((long)rowPtr + rowStructSize);
                    }

                    return tableRows.ToList();
                }
                finally
                {
                    Marshal.FreeHGlobal(tcpTablePtr);
                }
            }

            throw new Win32Exception(unchecked((int)ERROR_INSUFFICIENT_BUFFER), "The TCP table changed repeatedly while it was being read");
        }
    }
}
