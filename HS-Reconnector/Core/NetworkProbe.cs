using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HS_Reconnector.Native;

namespace HS_Reconnector.Core
{
    internal static class NetworkProbe
    {
        private static readonly Regex ClientRegion = new Regex(
            @"\bRegion:[ \t]*(?<region>US|EU|KR|TW|ASIA|CN|CHINA)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex LoginAddress = new Regex(
            @"Connecting to server\.\s+address=(?<region>us|eu|kr|tw|cn)\.actual\.battle\.net\b(?=\s|:|$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static async Task<long?> MeasureAsync(IPAddress address, ushort port)
        {
            try
            {
                using (var client = new TcpClient(address.AddressFamily))
                using (var timeout = new CancellationTokenSource())
                {
                    try
                    {
                        var stopwatch = Stopwatch.StartNew();
                        var connect = client.ConnectAsync(address, port);
                        if (await Task.WhenAny(connect, Task.Delay(1500, timeout.Token)).ConfigureAwait(false) != connect)
                        {
                            // Disposing the socket aborts the outstanding connect. Observe its eventual fault.
                            _ = connect.ContinueWith(task => { var error = task.Exception; },
                                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                            return null;
                        }

                        await connect.ConfigureAwait(false);
                        return stopwatch.ElapsedMilliseconds;
                    }
                    finally
                    {
                        timeout.Cancel();
                    }
                }
            }
            catch (SocketException)
            {
                return null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        public static string ReadRegion(string logPath)
        {
            return new RegionLogScanner().Scan(logPath, flushTrailingLine: true);
        }

        internal sealed class RegionLogScanner
        {
            private const int ReadChunkBytes = 64 * 1024;
            private const int MaxPendingLineBytes = 64 * 1024;
            private static readonly byte[] RegionMarkerLower = Encoding.ASCII.GetBytes("region:");
            private static readonly byte[] LoginMarkerLower = Encoding.ASCII.GetBytes("actual.battle.net");

            private readonly byte[] pendingLineBytes = new byte[MaxPendingLineBytes];
            private readonly byte[] readBuffer = new byte[ReadChunkBytes];
            private int pendingLineLength;
            private string currentPath;
            private long offset;
            private bool hasFileIdentity;
            private uint volumeSerial;
            private ulong fileIndex;
            private bool discardUntilLineFeed;
            private string latestClientRegion;
            private string latestLoginRegion;

            public void Reset()
            {
                currentPath = null;
                offset = 0;
                hasFileIdentity = false;
                volumeSerial = 0;
                fileIndex = 0;
                pendingLineLength = 0;
                discardUntilLineFeed = false;
                latestClientRegion = null;
                latestLoginRegion = null;
            }

            public string Scan(string logPath, bool flushTrailingLine = false)
            {
                if (string.IsNullOrEmpty(logPath))
                {
                    Reset();
                    return null;
                }

                if (!string.Equals(currentPath, logPath, StringComparison.OrdinalIgnoreCase))
                {
                    Reset();
                    currentPath = logPath;
                }

                try
                {
                    using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        var fileLength = stream.Length;
                        var hasCurrentId = Win32.TryGetFileIdentity(
                            stream.SafeFileHandle,
                            out var currentVolumeSerial,
                            out var currentFileIndex,
                            out _);

                        var identityChanged = hasFileIdentity && hasCurrentId
                            && (currentVolumeSerial != volumeSerial || currentFileIndex != fileIndex);
                        if (identityChanged || fileLength < offset)
                        {
                            Reset();
                            currentPath = logPath;
                        }

                        if (hasCurrentId)
                        {
                            hasFileIdentity = true;
                            volumeSerial = currentVolumeSerial;
                            fileIndex = currentFileIndex;
                        }

                        if (offset < fileLength)
                        {
                            stream.Seek(offset, SeekOrigin.Begin);
                            int read;
                            while ((read = stream.Read(readBuffer, 0, readBuffer.Length)) > 0)
                            {
                                offset += read;
                                ProcessBytes(readBuffer, read);
                            }
                        }

                        if (flushTrailingLine && !discardUntilLineFeed && pendingLineLength > 0)
                            InspectPendingLine();

                        return latestClientRegion ?? latestLoginRegion;
                    }
                }
                catch (IOException) { return latestClientRegion ?? latestLoginRegion; }
                catch (UnauthorizedAccessException) { return latestClientRegion ?? latestLoginRegion; }
                catch (ArgumentException) { return null; }
                catch (NotSupportedException) { return null; }
            }

            private void ProcessBytes(byte[] buffer, int count)
            {
                for (var i = 0; i < count; i++)
                {
                    var current = buffer[i];
                    if (discardUntilLineFeed)
                    {
                        if (current == (byte)'\n')
                            discardUntilLineFeed = false;
                        continue;
                    }

                    if (current == (byte)'\n')
                    {
                        InspectPendingLine();
                        continue;
                    }

                    if (pendingLineLength >= MaxPendingLineBytes)
                    {
                        pendingLineLength = 0;
                        discardUntilLineFeed = true;
                        continue;
                    }

                    pendingLineBytes[pendingLineLength++] = current;
                }
            }

            private void InspectPendingLine()
            {
                var length = pendingLineLength;
                if (length > 0 && pendingLineBytes[length - 1] == (byte)'\r')
                    length--;

                if (length > 0 && (ContainsAsciiIgnoreCase(pendingLineBytes, length, RegionMarkerLower)
                    || ContainsAsciiIgnoreCase(pendingLineBytes, length, LoginMarkerLower)))
                {
                    var line = Encoding.UTF8.GetString(pendingLineBytes, 0, length);
                    var clientRegions = ClientRegion.Matches(line);
                    if (clientRegions.Count > 0)
                        latestClientRegion = NormalizeRegion(clientRegions[clientRegions.Count - 1].Groups["region"].Value);

                    var loginMatches = LoginAddress.Matches(line);
                    if (loginMatches.Count > 0)
                        latestLoginRegion = NormalizeRegion(loginMatches[loginMatches.Count - 1].Groups["region"].Value);
                }

                pendingLineLength = 0;
            }

            private static bool ContainsAsciiIgnoreCase(byte[] text, int length, byte[] lowerMarker)
            {
                if (length < lowerMarker.Length)
                    return false;

                var lastStart = length - lowerMarker.Length;
                for (var start = 0; start <= lastStart; start++)
                {
                    var matches = true;
                    for (var i = 0; i < lowerMarker.Length; i++)
                    {
                        var ch = text[start + i];
                        if (ch >= (byte)'A' && ch <= (byte)'Z')
                            ch = (byte)(ch + 32);
                        if (ch == lowerMarker[i])
                            continue;
                        matches = false;
                        break;
                    }

                    if (matches)
                        return true;
                }

                return false;
            }
        }

        private static string NormalizeRegion(string region)
        {
            switch (region.ToUpperInvariant())
            {
                case "KR":
                case "TW":
                case "ASIA": return "ASIA";
                case "CN":
                case "CHINA": return "CHINA";
                case "US": return "US";
                case "EU": return "EU";
                default: return null;
            }
        }
    }
}
