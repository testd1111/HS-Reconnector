using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HS_Reconnector.Core;
using HS_Reconnector.Native;

namespace HS_Reconnector.LogHandler
{
    internal sealed class LogWatcher : IDisposable
    {
        internal const int UpdateDelay = 200;
        private const long MaxTailSearchBytes = 1L * 1024 * 1024;
        private const long MaxInitialEndpointScanBytes = 16L * 1024 * 1024;

        private readonly ConnectionLogHandler connectionLogHandler = new ConnectionLogHandler();
        private readonly string logPath;
        private ILogEndpointSink endpointSink;
        private readonly List<LogReader> logReaders = new List<LogReader>();
        private readonly object callbackLock = new object();
        private readonly object lifecycleLock = new object();
        private int stopped;
        private int liveEventObserved;

        public string LogPath => logPath;

        public bool IsHealthy
        {
            get
            {
                lock (lifecycleLock)
                    return Volatile.Read(ref stopped) == 0 && logReaders.Count > 0
                        && logReaders.TrueForAll(reader => reader.IsHealthy);
            }
        }

        public LogWatcher(ILogEndpointSink endpointSink, string logPath)
        {
            this.endpointSink = endpointSink;
            this.logPath = logPath;
        }

        public void Stop()
        {
            if (Interlocked.Exchange(ref stopped, 1) != 0)
                return;

            lock (callbackLock)
            {
                endpointSink = null;
            }

            List<LogReader> readers;
            lock (lifecycleLock)
            {
                readers = new List<LogReader>(logReaders);
                logReaders.Clear();
                foreach (var logReader in readers)
                {
                    logReader.LineHandler = null;
                    logReader.OnReset -= ConnectionLogReader_OnReset;
                    logReader.RequestStop();
                }
            }

            if (readers.Count > 0)
                Task.Run(() => DisposeReaders(readers));
        }

        public void Dispose()
        {
            Stop();
        }

        private bool ConnectionLogReader_HandleLine(string line)
        {
            lock (callbackLock)
            {
                if (Volatile.Read(ref stopped) != 0)
                    return false;

                var sink = endpointSink;
                if (sink == null)
                    return false;

                var handled = connectionLogHandler.Handle(line, sink);
                if (handled)
                    Volatile.Write(ref liveEventObserved, 1);
                return handled;
            }
        }

        private void ConnectionLogReader_OnReset()
        {
            lock (callbackLock)
            {
                if (Volatile.Read(ref stopped) != 0)
                    return;

                Volatile.Write(ref liveEventObserved, 1);
                endpointSink?.ClearRemoteEndpointForMatchEnd("log file truncated or overwritten");
            }
        }

        public async Task<bool> StartAsync()
        {
            if (Volatile.Read(ref stopped) != 0)
                return false;

            try
            {
                AppLogger.Info("Watching Hearthstone log: " + logPath);
                var scanResult = await Task.Run(() => InitializeFromExistingLog(logPath));

                if (scanResult.HasEndpoint)
                {
                    lock (callbackLock)
                    {
                        if (Volatile.Read(ref stopped) != 0)
                            return false;

                        endpointSink?.SetRemoteEndpoint(
                            scanResult.Host,
                            scanResult.Port,
                            "initial log scan");
                    }
                }

                LogReader reader;
                lock (lifecycleLock)
                {
                    if (Volatile.Read(ref stopped) != 0)
                        return false;

                    reader = new LogReader(
                        logPath,
                        scanResult.InitialOffset,
                        scanResult.EventOffset,
                        scanResult.EventBytes,
                        scanResult.FileIdentity);
                    reader.LineHandler = ConnectionLogReader_HandleLine;
                    reader.OnReset += ConnectionLogReader_OnReset;
                    logReaders.Add(reader);
                    reader.Start();
                }

                if (!scanResult.HasEndpoint
                    && !scanResult.FoundMatchEnd
                    && scanResult.NextHistoricalScanEndOffset > 0
                    && Volatile.Read(ref stopped) == 0)
                {
                    _ = Task.Run(() => ContinueHistoricalScan(scanResult, reader));
                }

                return Volatile.Read(ref stopped) == 0;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Could not start Hearthstone log watcher: " + ex.Message);
                Stop();
                return false;
            }
        }

        private InitialScanResult InitializeFromExistingLog(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var hasIdentity = Win32.TryGetFileIdentity(stream.SafeFileHandle, out var volumeSerial, out var fileIndex, out var lastWriteTicks);
                var snapshotLength = stream.Length;
                var initialOffset = FindTailOffset(stream, snapshotLength);
                var lowerBound = Math.Max(0, snapshotLength - MaxInitialEndpointScanBytes);

                var hasEndpoint = TryFindLatestEndpointInRange(
                    stream,
                    snapshotLength,
                    lowerBound,
                    connectionLogHandler,
                    () => Volatile.Read(ref stopped) != 0,
                    out var host,
                    out var port,
                    out var ended,
                    out var eventOffset,
                    out var eventBytes,
                    out var nextEndOffset);

                (uint VolumeSerial, ulong FileIndex, long LastWriteTicks)? fileIdentity = null;
                if (hasIdentity)
                    fileIdentity = (volumeSerial, fileIndex, lastWriteTicks);

                return new InitialScanResult(
                    initialOffset,
                    hasEndpoint && !ended,
                    ended,
                    host,
                    port,
                    eventOffset,
                    eventBytes,
                    fileIdentity,
                    nextEndOffset);
            }
        }

        private void ContinueHistoricalScan(InitialScanResult initial, LogReader liveReader)
        {
            try
            {
                if (Volatile.Read(ref stopped) != 0 || Volatile.Read(ref liveEventObserved) != 0)
                    return;

                using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (initial.FileIdentity.HasValue)
                    {
                        var hasCurrentId = Win32.TryGetFileIdentity(stream.SafeFileHandle, out var volumeSerial, out var fileIndex, out _);
                        if (!hasCurrentId
                            || volumeSerial != initial.FileIdentity.Value.VolumeSerial
                            || fileIndex != initial.FileIdentity.Value.FileIndex)
                        {
                            return;
                        }
                    }

                    var endOffset = initial.NextHistoricalScanEndOffset;
                    if (stream.Length < endOffset)
                        return;

                    while (endOffset > 0
                        && Volatile.Read(ref stopped) == 0
                        && Volatile.Read(ref liveEventObserved) == 0)
                    {
                        var lowerBound = Math.Max(0, endOffset - MaxInitialEndpointScanBytes);
                        var found = TryFindLatestEndpointInRange(
                            stream,
                            endOffset,
                            lowerBound,
                            connectionLogHandler,
                            () => Volatile.Read(ref stopped) != 0 || Volatile.Read(ref liveEventObserved) != 0,
                            out var host,
                            out var port,
                            out var ended,
                            out var eventOffset,
                            out var eventBytes,
                            out var nextEndOffset);

                        if (Volatile.Read(ref stopped) != 0 || Volatile.Read(ref liveEventObserved) != 0)
                            return;

                        if (ended)
                            return;

                        if (found)
                        {
                            lock (callbackLock)
                            {
                                if (Volatile.Read(ref stopped) != 0 || Volatile.Read(ref liveEventObserved) != 0)
                                    return;

                                liveReader?.TrySetBaselineEvent(eventOffset, eventBytes);
                                endpointSink?.SetRemoteEndpoint(host, port, "initial log scan");
                            }
                            return;
                        }

                        if (nextEndOffset >= endOffset)
                            return;

                        endOffset = nextEndOffset;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (Exception ex)
            {
                AppLogger.Error("Background Hearthstone log scan failed: " + ex.Message);
            }
        }

        private static void DisposeReaders(IEnumerable<LogReader> readers)
        {
            foreach (var reader in readers)
            {
                try
                {
                    reader.Dispose();
                }
                catch (Exception ex)
                {
                    AppLogger.Error("Could not dispose log reader: " + ex.Message);
                }
            }
        }

        private struct InitialScanResult
        {
            public readonly long InitialOffset;
            public readonly bool HasEndpoint;
            public readonly bool FoundMatchEnd;
            public readonly string Host;
            public readonly ushort Port;
            public readonly long EventOffset;
            public readonly byte[] EventBytes;
            public readonly (uint VolumeSerial, ulong FileIndex, long LastWriteTicks)? FileIdentity;
            public readonly long NextHistoricalScanEndOffset;

            public InitialScanResult(
                long initialOffset,
                bool hasEndpoint,
                bool foundMatchEnd,
                string host,
                ushort port,
                long eventOffset,
                byte[] eventBytes,
                (uint VolumeSerial, ulong FileIndex, long LastWriteTicks)? fileIdentity,
                long nextHistoricalScanEndOffset)
            {
                InitialOffset = initialOffset;
                HasEndpoint = hasEndpoint;
                FoundMatchEnd = foundMatchEnd;
                Host = host;
                Port = port;
                EventOffset = eventOffset;
                EventBytes = eventBytes;
                FileIdentity = fileIdentity;
                NextHistoricalScanEndOffset = nextHistoricalScanEndOffset;
            }
        }

        private static long FindTailOffset(Stream stream, long length)
        {
            if (length <= 0)
                return 0;

            const int bufferSize = 64 * 1024;
            var buffer = new byte[bufferSize];
            var position = length;
            var lowerBound = Math.Max(0, length - MaxTailSearchBytes);

            while (position > lowerBound)
            {
                var count = (int)Math.Min(buffer.Length, position - lowerBound);
                position -= count;
                var read = ReadBlock(stream, position, buffer, count);

                for (var i = read - 1; i >= 0; i--)
                {
                    if (buffer[i] != (byte)'\n')
                        continue;

                    var lineEnd = position + i + 1;
                    return lineEnd == length ? length : lineEnd;
                }
            }

            return lowerBound == 0 ? 0 : length;
        }

        internal static bool TryFindLatestEndpointWithEvent(
            Stream stream,
            long length,
            ConnectionLogHandler handler,
            out string host,
            out ushort port,
            out bool matchEnded,
            out long eventOffset,
            out byte[] eventBytes)
        {
            var lowerBound = Math.Max(0, length - MaxInitialEndpointScanBytes);
            return TryFindLatestEndpointInRange(
                stream,
                length,
                lowerBound,
                handler,
                null,
                out host,
                out port,
                out matchEnded,
                out eventOffset,
                out eventBytes,
                out _);
        }

        internal static bool TryFindLatestEndpointInRange(
            Stream stream,
            long endOffset,
            long lowerBound,
            ConnectionLogHandler handler,
            Func<bool> shouldCancel,
            out string host,
            out ushort port,
            out bool matchEnded,
            out long eventOffset,
            out byte[] eventBytes,
            out long nextEndOffset)
        {
            const int bufferSize = 64 * 1024;
            const int maxLineBytes = 1024 * 1024;
            var buffer = new byte[bufferSize];
            var reversedLine = new List<byte>(256);
            var skipCurrentLine = true;
            var position = endOffset;
            lowerBound = Math.Max(0, lowerBound);
            var earliestNewlineOffset = -1L;

            while (position > lowerBound)
            {
                if (shouldCancel != null && shouldCancel())
                {
                    host = null;
                    port = 0;
                    matchEnded = false;
                    eventOffset = -1;
                    eventBytes = null;
                    nextEndOffset = 0;
                    return false;
                }

                var count = (int)Math.Min(buffer.Length, position - lowerBound);
                position -= count;
                var read = ReadBlock(stream, position, buffer, count);

                for (var i = read - 1; i >= 0; i--)
                {
                    if (buffer[i] == (byte)'\n')
                    {
                        earliestNewlineOffset = position + i;
                        if (!skipCurrentLine && TryInspectReversedLine(reversedLine, handler, out host, out port, out matchEnded, out eventBytes))
                        {
                            eventOffset = position + i + 1;
                            nextEndOffset = 0;
                            return !matchEnded;
                        }

                        reversedLine.Clear();
                        skipCurrentLine = false;
                        continue;
                    }

                    if (skipCurrentLine)
                        continue;

                    reversedLine.Add(buffer[i]);
                    if (reversedLine.Count > maxLineBytes)
                    {
                        reversedLine.Clear();
                        skipCurrentLine = true;
                    }
                }
            }

            if (lowerBound == 0
                && !skipCurrentLine
                && TryInspectReversedLine(reversedLine, handler, out host, out port, out matchEnded, out eventBytes))
            {
                eventOffset = 0;
                nextEndOffset = 0;
                return !matchEnded;
            }

            host = null;
            port = 0;
            matchEnded = false;
            eventOffset = -1;
            eventBytes = null;
            if (lowerBound == 0)
                nextEndOffset = 0;
            else if (earliestNewlineOffset >= 0 && earliestNewlineOffset + 1 < endOffset)
                nextEndOffset = earliestNewlineOffset + 1;
            else
                nextEndOffset = lowerBound;
            return false;
        }

        private static bool TryInspectReversedLine(
            List<byte> reversedLine,
            ConnectionLogHandler handler,
            out string host,
            out ushort port,
            out bool matchEnded,
            out byte[] rawBytes)
        {
            host = null;
            port = 0;
            matchEnded = false;
            rawBytes = null;

            if (reversedLine.Count == 0)
                return false;

            var bytes = reversedLine.ToArray();
            Array.Reverse(bytes);
            var line = Encoding.UTF8.GetString(bytes).TrimEnd('\r');

            if (handler.IsMatchEndLine(line))
            {
                matchEnded = true;
                rawBytes = TrimTrailingCarriageReturn(bytes);
                return true;
            }

            if (handler.TryExtractEndpoint(line, out host, out port))
            {
                rawBytes = TrimTrailingCarriageReturn(bytes);
                return true;
            }

            return false;
        }

        private static byte[] TrimTrailingCarriageReturn(byte[] bytes)
        {
            if (bytes.Length > 0 && bytes[bytes.Length - 1] == (byte)'\r')
            {
                var trimmed = new byte[bytes.Length - 1];
                Buffer.BlockCopy(bytes, 0, trimmed, 0, trimmed.Length);
                return trimmed;
            }
            return bytes;
        }

        private static int ReadBlock(Stream stream, long position, byte[] buffer, int count)
        {
            stream.Seek(position, SeekOrigin.Begin);
            var totalRead = 0;
            while (totalRead < count)
            {
                var read = stream.Read(buffer, totalRead, count - totalRead);
                if (read <= 0)
                    break;
                totalRead += read;
            }
            return totalRead;
        }
    }
}
