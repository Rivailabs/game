using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AstraKingdoms.Client.Online.Protocol;

namespace AstraKingdoms.Client.Online
{
    /// <summary>One text-frame WebSocket connection. Implementations need not be thread-safe for concurrent sends.</summary>
    public interface IWebSocketTransport : IDisposable
    {
        bool IsOpen { get; }
        /// <summary>Close description sent by the server, if any (e.g. "superseded").</summary>
        string CloseDescription { get; }

        Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken ct);
        Task SendTextAsync(string text, CancellationToken ct);
        /// <summary>Next complete text message, or null when the connection closed.</summary>
        Task<string> ReceiveTextAsync(CancellationToken ct);
        Task CloseAsync(CancellationToken ct);
    }

    /// <summary>
    /// Transport over any <see cref="WebSocket"/>. The default connects a
    /// <see cref="ClientWebSocket"/> (available in Unity's .NET profile on Android and desktop);
    /// tests pass a connector that returns an in-process socket.
    /// </summary>
    public sealed class WebSocketTransport : IWebSocketTransport
    {
        private readonly Func<Uri, IReadOnlyDictionary<string, string>, CancellationToken, Task<WebSocket>> _connector;
        private WebSocket _socket;

        public WebSocketTransport() : this(ConnectClientWebSocket)
        {
        }

        public WebSocketTransport(Func<Uri, IReadOnlyDictionary<string, string>, CancellationToken, Task<WebSocket>> connector)
        {
            _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        }

        public bool IsOpen => _socket != null && _socket.State == WebSocketState.Open;
        public string CloseDescription => _socket?.CloseStatusDescription;

        public async Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
        {
            _socket = await _connector(uri, headers, ct).ConfigureAwait(false);
        }

        private static async Task<WebSocket> ConnectClientWebSocket(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
        {
            var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            foreach (KeyValuePair<string, string> h in headers) ws.Options.SetRequestHeader(h.Key, h.Value);
            try
            {
                await ws.ConnectAsync(uri, ct).ConfigureAwait(false);
            }
            catch
            {
                ws.Dispose();
                throw;
            }
            return ws;
        }

        public Task SendTextAsync(string text, CancellationToken ct)
        {
            if (_socket == null) throw new InvalidOperationException("Not connected.");
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            return _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }

        public async Task<string> ReceiveTextAsync(CancellationToken ct)
        {
            if (_socket == null) return null;
            var buffer = new byte[8192];
            using (var ms = new MemoryStream())
            {
                while (true)
                {
                    WebSocketReceiveResult r;
                    try
                    {
                        r = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    }
                    catch (WebSocketException)
                    {
                        return null;
                    }
                    if (r.MessageType == WebSocketMessageType.Close) return null;
                    ms.Write(buffer, 0, r.Count);
                    if (ms.Length > OnlineProtocol.MaxMessageBytes * 64L) return null; // views with ownership are ~12 KB
                    if (r.EndOfMessage) return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                }
            }
        }

        public async Task CloseAsync(CancellationToken ct)
        {
            if (_socket == null) return;
            try
            {
                // CloseOutputAsync sends our close frame without reading: the receive loop owns reads and
                // sees the server's close reply.
                if (_socket.State == WebSocketState.Open || _socket.State == WebSocketState.CloseReceived)
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", ct).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
            }
            catch (InvalidOperationException)
            {
                // includes ObjectDisposedException: the link is already gone
            }
            catch (OperationCanceledException)
            {
            }
        }

        public void Dispose() => _socket?.Dispose();
    }
}
