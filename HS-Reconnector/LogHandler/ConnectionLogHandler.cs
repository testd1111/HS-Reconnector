using System;
using System.Text.RegularExpressions;

namespace HS_Reconnector.LogHandler
{
    internal interface ILogEndpointSink
    {
        bool SetRemoteEndpoint(string address, ushort port, string source);
        void ClearRemoteEndpointForMatchEnd(string reason);
    }

    internal sealed class ConnectionLogHandler
    {
        private static readonly Regex GotoGameServer = new Regex(
            @"Network\.GotoGameServe(?:r)?\(\).*address=\s*(?<endpoint>[^\s,]+)",
            RegexOptions.Compiled);

        private static readonly Regex EndGameDisconnect = new Regex(
            @"Network\.DisconnectFromGameServer\(\).*Reason:\s*EndGameScreen",
            RegexOptions.Compiled);

        internal bool TryExtractEndpoint(string line, out string host, out ushort port)
        {
            host = null;
            port = 0;

            if (string.IsNullOrEmpty(line))
                return false;

            var match = GotoGameServer.Match(line);
            if (!match.Success)
                return false;

            SplitEndpoint(match.Groups["endpoint"].Value.Trim(), out host, out port);
            return !string.IsNullOrEmpty(host);
        }

        internal bool IsMatchEndLine(string line)
        {
            return !string.IsNullOrEmpty(line) && EndGameDisconnect.IsMatch(line);
        }

        private static void SplitEndpoint(string endpoint, out string host, out ushort port)
        {
            port = 0;
            if (string.IsNullOrEmpty(endpoint))
            {
                host = null;
                return;
            }

            if (endpoint.StartsWith("[", StringComparison.Ordinal))
            {
                var closeBracket = endpoint.IndexOf(']');
                if (closeBracket <= 1)
                {
                    host = endpoint.Trim('[', ']');
                    return;
                }

                host = endpoint.Substring(1, closeBracket - 1);
                if (closeBracket + 1 < endpoint.Length && endpoint[closeBracket + 1] == ':')
                    ushort.TryParse(endpoint.Substring(closeBracket + 2), out port);
                return;
            }

            var lastColon = endpoint.LastIndexOf(':');
            if (lastColon <= 0 || lastColon == endpoint.Length - 1)
            {
                host = endpoint;
                return;
            }

            host = endpoint.Substring(0, lastColon);
            ushort.TryParse(endpoint.Substring(lastColon + 1), out port);
        }

        internal bool IsTrackedEventLine(string line)
        {
            if (string.IsNullOrEmpty(line))
                return false;

            return TryExtractEndpoint(line, out _, out _) || IsMatchEndLine(line);
        }

        public bool Handle(string line, ILogEndpointSink sink)
        {
            if (sink == null || string.IsNullOrEmpty(line))
                return false;

            if (TryExtractEndpoint(line, out var host, out var port))
            {
                sink.SetRemoteEndpoint(host, port, "log");
                return true;
            }

            if (IsMatchEndLine(line))
            {
                sink.ClearRemoteEndpointForMatchEnd("match ended (EndGameScreen)");
                return true;
            }

            return false;
        }
    }
}
