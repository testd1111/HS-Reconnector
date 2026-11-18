using System;
using System.IO;
using System.Text;
using System.Threading;
using HS_Reconnector.Core;
using HS_Reconnector.Native;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace HS_Reconnector.LogHandler
{
    internal sealed class LogReader : IDisposable
    {
        private const int ReadBufferSize = 64 * 1024;
        private const int MaxReadBlocksPerCycle = 16;
        private const int MaxPendingLineBytes = 64 * 1024;
        private const int HeadFingerprintSize = 512;
        private const int TailFingerprintSize = 256;
        private const int EventFingerprintSize = 512;
        private const long MaxSpanHashBytes = 16L * 1024 * 1024;
        private const ulong FnvOffsetBasis = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        private static readonly byte[] EndpointMarkerBytes = Encoding.ASCII.GetBytes("Network.GotoGameServe");
        private static readonly byte[] DisconnectMarkerBytes = Encoding.ASCII.GetBytes("Network.DisconnectFromGameServer");

        private readonly string filePath;
        private readonly long initialOffset;
        private readonly long initialEventOffset;
        private readonly byte[] initialEventBytes;
        private readonly byte[] pendingLineBytes = new byte[MaxPendingLineBytes];
        private readonly ManualResetEvent stopEvent = new ManualResetEvent(false);
        private readonly byte[] headFingerprint = new byte[HeadFingerprintSize];
        private readonly byte[] tailFingerprint = new byte[TailFingerprintSize];
        private readonly byte[] eventFingerprint = new byte[EventFingerprintSize];
        private readonly byte[] verifyBuffer = new byte[HeadFingerprintSize];

        private int pendingLineLength;
        private long currentLineStartOffset;
        private int headFingerprintLength;
        private int tailFingerprintLength;
        private long tailFingerprintEndOffset;
        private long eventOffset = -1;
        private int eventFingerprintLength;
        private bool hasFileIdentity;
        private uint baselineVolumeSerial;
        private ulong baselineFileIndex;
        private long baselineLastWriteTicks;
        private bool hasSpanHash;
        private ulong spanHashValue = FnvOffsetBasis;

        private long offset;
        private volatile bool stop;
        private int stopRequested;
        private int started;
        private int disposed;
        private bool discardUntilLineFeed;
        private int oversizedLineCount;
        private Thread threadRead;
        private long lastSuccessfulReadTimestamp = Stopwatch.GetTimestamp();

        public bool IsHealthy => !stop &&
            (Stopwatch.GetTimestamp() - Interlocked.Read(ref lastSuccessfulReadTimestamp))
                / (double)Stopwatch.Frequency < 10;

        public event Action<string> OnNewLine;
        public event Action OnReset;

        public LogReader(string filePath, long initialOffset = 0, long initialEventOffset = -1, byte[] initialEventBytes = null,
            (uint VolumeSerial, ulong FileIndex, long LastWriteTicks)? initialFileIdentity = null)
        {
            this.filePath = filePath;
            this.initialOffset = Math.Max(0, initialOffset);
            this.initialEventOffset = initialEventOffset;
            this.initialEventBytes = initialEventBytes;
            if (initialFileIdentity.HasValue)
            {
                hasFileIdentity = true;
                baselineVolumeSerial = initialFileIdentity.Value.VolumeSerial;
                baselineFileIndex = initialFileIdentity.Value.FileIndex;
                baselineLastWriteTicks = initialFileIdentity.Value.LastWriteTicks;
            }
        }

        public void Start()
        {
            if (Interlocked.CompareExchange(ref started, 1, 0) != 0)
                throw new InvalidOperationException("LogReader can only be started once.");
            if (Volatile.Read(ref disposed) != 0)
                throw new ObjectDisposedException(nameof(LogReader));

            offset = initialOffset;
            currentLineStartOffset = offset;
            if (initialEventOffset >= 0 && initialEventBytes != null && initialEventBytes.Length > 0)
            {
                eventOffset = initialEventOffset;
                eventFingerprintLength = Math.Min(initialEventBytes.Length, EventFingerprintSize);
                Buffer.BlockCopy(initialEventBytes, 0, eventFingerprint, 0, eventFingerprintLength);
            }

            threadRead = new Thread(ReadLogFile) { IsBackground = true };
            threadRead.Start();
        }

        public void RequestStop()
        {
            if (Interlocked.Exchange(ref stopRequested, 1) != 0)
                return;

            stop = true;
            stopEvent.Set();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            RequestStop();
            WaitForThread(threadRead);
            stopEvent.Dispose();
        }

        private static void WaitForThread(Thread thread)
        {
            if (thread == null || (thread.ThreadState & ThreadState.Unstarted) != 0)
                return;

            thread.Join();
        }

        private void ReadLogFile()
        {
            var failureCount = 0;

            while (!stop)
            {
                var delay = LogWatcher.UpdateDelay;
                try
                {
                    for (var i = 0; i < MaxReadBlocksPerCycle && !stop; i++)
                    {
                        if (!ReadAvailableLines())
                            break;
                    }
                    Interlocked.Exchange(ref lastSuccessfulReadTimestamp, Stopwatch.GetTimestamp());
                    failureCount = 0;
                }
                catch (Exception ex)
                {
                    failureCount++;
                    LogRepeatedFailure(ex, failureCount);
                    delay = GetFailureDelay(failureCount);
                }

                if (stopEvent.WaitOne(delay))
                    return;
            }
        }

        private static int GetFailureDelay(int failureCount)
        {
            return Math.Min(30000, 500 * Math.Min(failureCount, 60));
        }

        private static void LogRepeatedFailure(Exception ex, int failureCount)
        {
            if (failureCount == 1 || failureCount == 5 || failureCount % 20 == 0)
                AppLogger.Error(ex);
        }

        private void ResetReaderState()
        {
            offset = 0;
            currentLineStartOffset = 0;
            pendingLineLength = 0;
            discardUntilLineFeed = false;
            headFingerprintLength = 0;
            tailFingerprintLength = 0;
            tailFingerprintEndOffset = 0;
            eventOffset = -1;
            eventFingerprintLength = 0;
            hasFileIdentity = false;
            baselineVolumeSerial = 0;
            baselineFileIndex = 0;
            baselineLastWriteTicks = 0;
            hasSpanHash = false;
            spanHashValue = FnvOffsetBasis;
        }

        private bool ReadAvailableLines()
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Watched Hearthstone log is unavailable.", filePath);

            // Open the stream directly instead of relying on FileInfo.Length, which can lag on NTFS while Hearthstone writes.
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var fileLength = fs.Length;
                var hasCurrentId = Win32.TryGetFileIdentity(
                    fs.SafeFileHandle,
                    out var volumeSerial,
                    out var fileIndex,
                    out var lastWriteTicks);

                if (ShouldResetForRewrite(fs, fileLength, hasCurrentId, volumeSerial, fileIndex, lastWriteTicks))
                {
                    ResetReaderState();
                    OnReset?.Invoke();
                }

                if (offset > 0 && (headFingerprintLength == 0 || !hasFileIdentity))
                    CaptureInitialBaseline(fs, hasCurrentId, volumeSerial, fileIndex, lastWriteTicks);

                var available = fileLength - offset;
                if (available <= 0)
                {
                    if (hasCurrentId)
                    {
                        if (!hasFileIdentity)
                        {
                            hasFileIdentity = true;
                            baselineVolumeSerial = volumeSerial;
                            baselineFileIndex = fileIndex;
                        }
                        baselineLastWriteTicks = lastWriteTicks;
                    }
                    return false;
                }

                fs.Seek(offset, SeekOrigin.Begin);
                var bytesToRead = (int)Math.Min(available, ReadBufferSize);
                var buffer = new byte[bytesToRead];
                var read = fs.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                    return false;

                var readStartOffset = offset;
                UpdateFingerprintsAfterRead(buffer, read, readStartOffset);
                if (hasCurrentId)
                {
                    hasFileIdentity = true;
                    baselineVolumeSerial = volumeSerial;
                    baselineFileIndex = fileIndex;
                    baselineLastWriteTicks = lastWriteTicks;
                }

                offset += read;
                ProcessBufferBytes(buffer, read, readStartOffset);
                return offset < fs.Length;
            }
        }

        private bool ShouldResetForRewrite(
            FileStream fs,
            long fileLength,
            bool hasCurrentId,
            uint volumeSerial,
            ulong fileIndex,
            long lastWriteTicks)
        {
            if (offset == 0 && !hasFileIdentity)
                return false;

            if (fileLength < offset)
                return true;

            if (hasFileIdentity && hasCurrentId && (volumeSerial != baselineVolumeSerial || fileIndex != baselineFileIndex))
                return true;

            if (!VerifyExistingFingerprints(fs, fileLength))
                return true;

            if (fileLength == offset && hasFileIdentity && hasCurrentId && lastWriteTicks != baselineLastWriteTicks)
            {
                baselineLastWriteTicks = lastWriteTicks;
                if (hasSpanHash && offset <= MaxSpanHashBytes)
                {
                    if (ComputeSpanHash(fs, offset) != spanHashValue)
                        return true;
                }
                else
                {
                    return true;
                }
            }

            return false;
        }

        private bool VerifyExistingFingerprints(FileStream fs, long fileLength)
        {
            if (headFingerprintLength > 0)
            {
                if (fileLength < headFingerprintLength || !MatchesSlice(fs, 0, headFingerprint, headFingerprintLength))
                    return false;
            }

            if (tailFingerprintLength > 0)
            {
                var start = tailFingerprintEndOffset - tailFingerprintLength;
                if (fileLength < tailFingerprintEndOffset || !MatchesSlice(fs, start, tailFingerprint, tailFingerprintLength))
                    return false;
            }

            if (eventOffset >= 0 && eventFingerprintLength > 0)
            {
                if (fileLength < eventOffset + eventFingerprintLength || !MatchesSlice(fs, eventOffset, eventFingerprint, eventFingerprintLength))
                    return false;
            }

            return true;
        }

        private bool MatchesSlice(FileStream fs, long position, byte[] expected, int length)
        {
            fs.Seek(position, SeekOrigin.Begin);
            var totalRead = 0;
            while (totalRead < length)
            {
                var read = fs.Read(verifyBuffer, totalRead, length - totalRead);
                if (read <= 0)
                    return false;
                totalRead += read;
            }

            for (var i = 0; i < length; i++)
            {
                if (verifyBuffer[i] != expected[i])
                    return false;
            }

            return true;
        }

        private void CaptureInitialBaseline(
            FileStream fs,
            bool hasCurrentId,
            uint volumeSerial,
            ulong fileIndex,
            long lastWriteTicks)
        {
            if (headFingerprintLength == 0 && offset > 0)
            {
                var headCount = (int)Math.Min(offset, HeadFingerprintSize);
                if (headCount > 0)
                {
                    fs.Seek(0, SeekOrigin.Begin);
                    headFingerprintLength = ReadExact(fs, headFingerprint, headCount);
                }

                var tailCount = (int)Math.Min(offset, TailFingerprintSize);
                if (tailCount > 0)
                {
                    fs.Seek(offset - tailCount, SeekOrigin.Begin);
                    tailFingerprintLength = ReadExact(fs, tailFingerprint, tailCount);
                    tailFingerprintEndOffset = tailFingerprintLength > 0 ? offset : 0;
                }

                if (offset <= MaxSpanHashBytes)
                {
                    spanHashValue = ComputeSpanHash(fs, offset);
                    hasSpanHash = true;
                }
            }

            if (hasCurrentId && !hasFileIdentity)
            {
                hasFileIdentity = true;
                baselineVolumeSerial = volumeSerial;
                baselineFileIndex = fileIndex;
                baselineLastWriteTicks = lastWriteTicks;
            }
        }

        private void UpdateFingerprintsAfterRead(byte[] buffer, int read, long readStartOffset)
        {
            if (headFingerprintLength < HeadFingerprintSize && readStartOffset < HeadFingerprintSize)
            {
                var copyStart = (int)readStartOffset;
                var canCopy = Math.Min(read, HeadFingerprintSize - copyStart);
                if (canCopy > 0)
                {
                    Buffer.BlockCopy(buffer, 0, headFingerprint, copyStart, canCopy);
                    headFingerprintLength = Math.Max(headFingerprintLength, copyStart + canCopy);
                }
            }

            if (read >= TailFingerprintSize)
            {
                Buffer.BlockCopy(buffer, read - TailFingerprintSize, tailFingerprint, 0, TailFingerprintSize);
                tailFingerprintLength = TailFingerprintSize;
                tailFingerprintEndOffset = readStartOffset + read;
            }
            else if (read > 0)
            {
                var keepOld = Math.Min(tailFingerprintLength, TailFingerprintSize - read);
                if (keepOld > 0)
                    Buffer.BlockCopy(tailFingerprint, tailFingerprintLength - keepOld, tailFingerprint, 0, keepOld);
                Buffer.BlockCopy(buffer, 0, tailFingerprint, keepOld, read);
                tailFingerprintLength = keepOld + read;
                tailFingerprintEndOffset = readStartOffset + read;
            }

            if (readStartOffset == 0 && !hasSpanHash && read <= MaxSpanHashBytes)
            {
                spanHashValue = FnvOffsetBasis;
                hasSpanHash = true;
            }

            if (hasSpanHash)
            {
                if (readStartOffset + read <= MaxSpanHashBytes)
                    spanHashValue = AdvanceFnv1a(spanHashValue, buffer, read);
                else
                    hasSpanHash = false;
            }
        }

        private static ulong ComputeSpanHash(FileStream fs, long length)
        {
            fs.Seek(0, SeekOrigin.Begin);
            var hash = FnvOffsetBasis;
            var buf = new byte[Math.Min(ReadBufferSize, (int)Math.Max(1, length))];
            long remaining = length;
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buf.Length, remaining);
                var read = fs.Read(buf, 0, toRead);
                if (read <= 0)
                    break;
                hash = AdvanceFnv1a(hash, buf, read);
                remaining -= read;
            }
            return hash;
        }

        private static ulong AdvanceFnv1a(ulong hash, byte[] buffer, int count)
        {
            for (var i = 0; i < count; i++)
            {
                hash ^= buffer[i];
                hash *= FnvPrime;
            }
            return hash;
        }

        private static int ReadExact(FileStream fs, byte[] target, int count)
        {
            var totalRead = 0;
            while (totalRead < count)
            {
                var read = fs.Read(target, totalRead, count - totalRead);
                if (read <= 0)
                    break;
                totalRead += read;
            }
            return totalRead;
        }

        private void ProcessBufferBytes(byte[] buffer, int count, long readStartOffset)
        {
            for (var i = 0; i < count; i++)
            {
                var current = buffer[i];

                if (discardUntilLineFeed)
                {
                    if (current == (byte)'\n')
                    {
                        discardUntilLineFeed = false;
                        currentLineStartOffset = readStartOffset + i + 1;
                    }
                    continue;
                }

                if (current == (byte)'\n')
                {
                    ProcessPendingLine();
                    currentLineStartOffset = readStartOffset + i + 1;
                    continue;
                }

                if (pendingLineLength >= MaxPendingLineBytes)
                {
                    pendingLineLength = 0;
                    discardUntilLineFeed = true;
                    ReportOversizedLine();
                    continue;
                }

                pendingLineBytes[pendingLineLength++] = current;
            }
        }

        private void ProcessPendingLine()
        {
            var length = pendingLineLength;
            if (length > 0 && pendingLineBytes[length - 1] == (byte)'\r')
                length--;

            if (length > 0 && (ContainsAsciiMarker(pendingLineBytes, length, EndpointMarkerBytes)
                || ContainsAsciiMarker(pendingLineBytes, length, DisconnectMarkerBytes)))
            {
                eventOffset = currentLineStartOffset;
                eventFingerprintLength = Math.Min(length, EventFingerprintSize);
                Buffer.BlockCopy(pendingLineBytes, 0, eventFingerprint, 0, eventFingerprintLength);

                var line = Encoding.UTF8.GetString(pendingLineBytes, 0, length);
                OnNewLine?.Invoke(line);
            }

            pendingLineLength = 0;
        }

        private static bool ContainsAsciiMarker(byte[] text, int length, byte[] marker)
        {
            if (length < marker.Length)
                return false;

            for (var start = 0; start <= length - marker.Length; start++)
            {
                var matches = true;
                for (var i = 0; i < marker.Length; i++)
                {
                    if (text[start + i] == marker[i])
                        continue;
                    matches = false;
                    break;
                }

                if (matches)
                    return true;
            }

            return false;
        }

        private void ReportOversizedLine()
        {
            oversizedLineCount++;
            if (oversizedLineCount == 1 || oversizedLineCount % 100 == 0)
                AppLogger.Error("Oversized Hearthstone log line was discarded.");
        }
    }
}
