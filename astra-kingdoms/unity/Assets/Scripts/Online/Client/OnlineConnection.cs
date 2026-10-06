using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AstraKingdoms.Client.Online.Protocol;
using AstraKingdoms.Rules.Replay;

namespace AstraKingdoms.Client.Online
{
    public enum ConnectionStatus : byte
    {
        Disconnected = 0,
        Connecting = 1,
        /// <summary>Authenticated: the server's welcome arrived.</summary>
        Connected = 2,
        /// <summary>The link dropped unexpectedly; retrying with backoff.</summary>
        Reconnecting = 3,
        /// <summary>Stopped for good (rejected token, superseded by another device, too many retries).</summary>
        Failed = 4,
    }

    public sealed class OnlineClientOptions
    {
        /// <summary>Service base address, e.g. wss://play.example.com (the protocol path is appended).</summary>
        public Uri ServerUri;
        public string ClientVersion = "0.0.0";
        /// <summary>
        /// Supplies a fresh identity token for each (re)connection: a Firebase ID token in production
        /// (Firebase Auth SDK, not bundled here) or "dev:&lt;name&gt;" against a development server.
        /// </summary>
        public Func<CancellationToken, Task<string>> TokenProvider;
        public Func<IWebSocketTransport> TransportFactory = () => new WebSocketTransport();
        /// <summary>Backoff between reconnection attempts (the last value repeats).</summary>
        public int[] ReconnectDelaysMs = { 500, 1000, 2000, 4000, 8000 };
        /// <summary>Attempts after a drop before giving up (0 disables reconnection).</summary>
        public int MaxReconnectAttempts = 30;
        /// <summary>
        /// When true, messages are dispatched on the receive thread (serialized); otherwise they wait
        /// for <see cref="OnlineConnection.Pump"/>, which Unity calls from the main thread.
        /// </summary>
        public bool DispatchOnReceiveThread;
    }

    /// <summary>
    /// Owns the WebSocket link: authenticates the upgrade with a bearer token, says hello, keeps
    /// one ordered writer, queues received messages for main-thread dispatch and reconnects with
    /// backoff after an unexpected drop. It knows nothing about matches; <see cref="OnlineClient"/>
    /// re-subscribes the active match and re-sends pending commands after <see cref="Welcomed"/>.
    /// </summary>
    public sealed class OnlineConnection : IDisposable
    {
        private readonly OnlineClientOptions _options;
        private readonly ConcurrentQueue<JsonNode> _inbox = new ConcurrentQueue<JsonNode>();
        private readonly ConcurrentQueue<string> _outbox = new ConcurrentQueue<string>();
        private readonly SemaphoreSlim _outboxSignal = new SemaphoreSlim(0);
        private readonly object _dispatchGate = new object();
        private CancellationTokenSource _life;
        private IWebSocketTransport _transport;
        private Task _runner;
        private volatile ConnectionStatus _status;
        private volatile bool _welcomed;
        private TaskCompletionSource<bool> _firstWelcome;

        public OnlineConnection(OnlineClientOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (options.ServerUri == null) throw new ArgumentException("ServerUri is required.", nameof(options));
            if (options.TokenProvider == null) throw new ArgumentException("TokenProvider is required.", nameof(options));
        }

        public ConnectionStatus Status => _status;
        /// <summary>Why the connection failed or last dropped (for the status line).</summary>
        public string LastFailure { get; private set; }
        public WelcomeMessage Welcome { get; private set; }
        /// <summary>Number of completed (re)connections.</summary>
        public int ConnectCount { get; private set; }

        public event Action<ConnectionStatus> StatusChanged;
        /// <summary>A (re)connection completed authentication. The argument is true on reconnection.</summary>
        public event Action<WelcomeMessage, bool> Welcomed;
        public event Action<JsonNode> MessageReceived;

        /// <summary>Starts the connection loop and waits until the first welcome, failure or cancellation.</summary>
        public async Task ConnectAsync(CancellationToken ct = default)
        {
            if (_runner != null) throw new InvalidOperationException("Already started.");
            _life = new CancellationTokenSource();
            _firstWelcome = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _runner = Task.Run(() => RunAsync(_life.Token));
            using (ct.Register(() => _firstWelcome.TrySetCanceled()))
                await _firstWelcome.Task.ConfigureAwait(false);
        }

        /// <summary>Queues a message. Returns false (nothing queued) unless the connection is authenticated.</summary>
        public bool Send(JsonNode message)
        {
            if (_status != ConnectionStatus.Connected || message == null) return false;
            _outbox.Enqueue(message.ToCanonicalString());
            _outboxSignal.Release();
            return true;
        }

        /// <summary>Dispatches received messages on the calling thread (Unity: once per frame).</summary>
        public void Pump()
        {
            while (_inbox.TryDequeue(out JsonNode n)) Dispatch(n);
        }

        /// <summary>Simulates a dropped network link (tests and the development menu): the loop reconnects.</summary>
        public void DropLink()
        {
            IWebSocketTransport t = _transport;
            t?.Dispose();
        }

        public async Task CloseAsync()
        {
            CancellationTokenSource life = _life;
            if (life == null) return;
            IWebSocketTransport t = _transport;
            if (t != null)
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                    await t.CloseAsync(timeout.Token).ConfigureAwait(false);
            }
            life.Cancel();
            try
            {
                if (_runner != null) await _runner.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            SetStatus(ConnectionStatus.Disconnected);
        }

        public void Dispose()
        {
            _life?.Cancel();
            _transport?.Dispose();
        }

        // ------------------------------------------------------------------ loop

        private async Task RunAsync(CancellationToken ct)
        {
            int failures = 0;
            bool everWelcomed = false;
            while (!ct.IsCancellationRequested)
            {
                SetStatus(everWelcomed || failures > 0 ? ConnectionStatus.Reconnecting : ConnectionStatus.Connecting);
                string stopReason = await RunOnceAsync(ct).ConfigureAwait(false);
                if (_welcomed)
                {
                    everWelcomed = true;
                    failures = 0;
                }
                if (ct.IsCancellationRequested) return;
                if (stopReason != null)
                {
                    LastFailure = stopReason;
                    SetStatus(ConnectionStatus.Failed);
                    _firstWelcome?.TrySetException(new InvalidOperationException("Connection failed: " + stopReason));
                    return;
                }
                failures++;
                if (failures > _options.MaxReconnectAttempts)
                {
                    LastFailure = "Too many reconnection attempts.";
                    SetStatus(ConnectionStatus.Failed);
                    _firstWelcome?.TrySetException(new InvalidOperationException("Connection failed: " + LastFailure));
                    return;
                }
                SetStatus(ConnectionStatus.Reconnecting);
                int[] delays = _options.ReconnectDelaysMs;
                int delay = delays.Length == 0 ? 1000 : delays[Math.Min(failures - 1, delays.Length - 1)];
                try
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>One connection attempt. Returns a reason when retrying is pointless, otherwise null.</summary>
        private async Task<string> RunOnceAsync(CancellationToken ct)
        {
            _welcomed = false;
            IWebSocketTransport transport = _options.TransportFactory();
            _transport = transport;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                try
                {
                    string token = await _options.TokenProvider(ct).ConfigureAwait(false);
                    var headers = new Dictionary<string, string>
                    {
                        { "Authorization", "Bearer " + token },
                        { OnlineProtocol.ClientVersionHeader, _options.ClientVersion },
                    };
                    var uri = new Uri(_options.ServerUri, OnlineProtocol.WebSocketPath);
                    await transport.ConnectAsync(uri, headers, ct).ConfigureAwait(false);
                    await transport.SendTextAsync(ClientMessages.Hello(_options.ClientVersion).ToCanonicalString(), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    transport.Dispose();
                    return null;
                }
                catch (Exception e)
                {
                    transport.Dispose();
                    LastFailure = e.Message;
                    // A rejected upgrade (HTTP 401/403) will not succeed by retrying with the same identity.
                    return e.Message.Contains("401") || e.Message.Contains("403") ? "Authentication rejected." : null;
                }

                while (_outbox.TryDequeue(out _))
                {
                    // Messages queued for a dead link are dropped; commands are re-sent by OnlineClient.
                }
                Task writer = WriteLoopAsync(transport, linked.Token);
                string stop = null;
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        string text = await transport.ReceiveTextAsync(ct).ConfigureAwait(false);
                        if (text == null) break;
                        JsonNode n;
                        try
                        {
                            n = JsonNode.Parse(text);
                        }
                        catch (FormatException)
                        {
                            continue; // ignore a malformed frame rather than trusting part of it
                        }
                        if (n.Type() == MessageTypes.Welcome)
                        {
                            Welcome = WelcomeMessage.Parse(n);
                            n.Add(ReconnectMarker, ConnectCount > 0); // local marker, read back in Dispatch
                            ConnectCount++;
                            _welcomed = true;
                            SetStatus(ConnectionStatus.Connected);
                            _firstWelcome?.TrySetResult(true);
                        }
                        if (n.Type() == MessageTypes.Error)
                        {
                            string code = n.OptString("code");
                            if (code == ErrorCodes.ProtocolUnsupported || code == ErrorCodes.ClientTooOld || code == ErrorCodes.Unauthenticated)
                                stop = "Server refused the client: " + code;
                        }
                        if (_options.DispatchOnReceiveThread) Dispatch(n);
                        else _inbox.Enqueue(n);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
                catch (Exception e)
                {
                    LastFailure = e.Message;
                }
                string description = transport.CloseDescription;
                if (description == CloseReasons.Superseded) stop = "Signed in on another device.";
                if (description == CloseReasons.PolicyViolation) stop = "Disconnected by the server (policy).";
                linked.Cancel();
                try
                {
                    await writer.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                transport.Dispose();
                if (_transport == transport) _transport = null;
                _welcomed = false;
                return stop;
            }
        }

        private async Task WriteLoopAsync(IWebSocketTransport transport, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await _outboxSignal.WaitAsync(ct).ConfigureAwait(false);
                    while (_outbox.TryDequeue(out string text))
                        await transport.SendTextAsync(text, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
                transport.Dispose(); // a failed write ends the receive loop too; the run loop reconnects
            }
        }

        /// <summary>Raises <see cref="Welcomed"/> or <see cref="MessageReceived"/> in arrival order.</summary>
        private void Dispatch(JsonNode n)
        {
            lock (_dispatchGate)
            {
                if (n.Type() == MessageTypes.Welcome) Welcomed?.Invoke(WelcomeMessage.Parse(n), n.OptBool(ReconnectMarker));
                else MessageReceived?.Invoke(n);
            }
        }

        private const string ReconnectMarker = "_reconnected";

        private void SetStatus(ConnectionStatus status)
        {
            if (_status == status) return;
            _status = status;
            StatusChanged?.Invoke(status);
        }
    }
}
