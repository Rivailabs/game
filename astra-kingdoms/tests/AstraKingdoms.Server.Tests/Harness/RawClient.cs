using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using AstraKingdoms.Client.Online.Protocol;
using AstraKingdoms.Rules.Replay;

namespace AstraKingdoms.Server.Tests.Harness;

/// <summary>A bare socket for malformed, tampered and flooding traffic that the client library would never send.</summary>
internal sealed class RawClient : IAsyncDisposable
{
    private readonly WebSocket _ws;
    private readonly ConcurrentQueue<string> _received = new();
    private readonly Task _reader;

    private RawClient(WebSocket ws)
    {
        _ws = ws;
        _reader = Task.Run(ReadAsync);
    }

    public static async Task<RawClient> ConnectAsync(ServerHarness h, string token, bool hello = true)
    {
        var c = new RawClient(await h.RawSocketAsync(token));
        if (hello)
        {
            await c.SendAsync(ClientMessages.Hello("1.0.0").ToCanonicalString());
            await ServerHarness.Until(() => c.Messages.Any(m => m.Type() == MessageTypes.Welcome), "welcome");
        }
        return c;
    }

    public bool Closed { get; private set; }
    public string CloseDescription => _ws.CloseStatusDescription;
    public IReadOnlyList<JsonNode> Messages => _received.Select(JsonNode.Parse).ToList();
    public IEnumerable<ErrorMessage> Errors => Messages.Where(m => m.Type() == MessageTypes.Error).Select(ErrorMessage.Parse);
    public bool HasError(string code) => Errors.Any(e => e.Code == code);

    public Task SendAsync(string text) => _ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    public Task SendAsync(JsonNode message) => SendAsync(message.ToCanonicalString());

    private async Task ReadAsync()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buffer, CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        Closed = true;
                        return;
                    }
                    ms.Write(buffer, 0, r.Count);
                }
                while (!r.EndOfMessage);
                _received.Enqueue(Encoding.UTF8.GetString(ms.ToArray()));
            }
        }
        catch (Exception)
        {
            Closed = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open) await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch (Exception)
        {
        }
        await Task.WhenAny(_reader, Task.Delay(1000));
        _ws.Dispose();
    }
}
