using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

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
                catch (SocketException)
                {
                    return null;
                }
                finally
                {
                    timeout.Cancel();
                }
            }
        }

        public static string ReadRegion(string logPath)
        {
            if (string.IsNullOrEmpty(logPath))
                return null;

            try
            {
                using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new BinaryReader(stream))
                {
                    var text = Encoding.UTF8.GetString(reader.ReadBytes(256 * 1024));
                    var clientRegions = ClientRegion.Matches(text);
                    if (clientRegions.Count > 0)
                        return NormalizeRegion(clientRegions[clientRegions.Count - 1].Groups["region"].Value);

                    var matches = LoginAddress.Matches(text);
                    if (matches.Count == 0)
                        return null;

                    return NormalizeRegion(matches[matches.Count - 1].Groups["region"].Value);
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
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
