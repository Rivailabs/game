using System;
using System.Threading;
using System.Threading.Tasks;
using AstraKingdoms.Client.Online.Protocol;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Replay;

namespace AstraKingdoms.Client.Online
{
    /// <summary>
    /// Client facade for online play: connection, friend room, queue (with the labelled bot offer)
    /// and the current <see cref="OnlineMatchSession"/>. Screens bind to its events; everything is
    /// raised from <see cref="OnlineConnection.Pump"/> (the Unity main thread) unless the options ask
    /// for receive-thread dispatch.
    /// </summary>
    public sealed class OnlineClient : IDisposable
    {
        private readonly Func<string> _newId;

        public OnlineClient(OnlineClientOptions options, Func<string> newRequestId = null)
        {
            Connection = new OnlineConnection(options);
            _newId = newRequestId ?? (() => Guid.NewGuid().ToString("D"));
            Connection.MessageReceived += OnMessage;
            Connection.Welcomed += OnWelcomed;
        }

        public OnlineConnection Connection { get; }
        public ConnectionStatus Status => Connection.Status;
        public RoomStateMessage Room { get; private set; }
        public QueueStateMessage Queue { get; private set; }
        public OnlineMatchSession Match { get; private set; }
        public ErrorMessage LastError { get; private set; }

        public event Action<RoomStateMessage> RoomChanged;
        public event Action<RoomClosedMessage> RoomClosed;
        public event Action<QueueStateMessage> QueueChanged;
        public event Action<OnlineMatchSession> MatchStarted;
        public event Action<MatchEndMessage> MatchEnded;
        public event Action<ErrorMessage> ErrorReceived;
        /// <summary>The server is shutting down; the link will drop and the client reconnects.</summary>
        public event Action ServerDraining;

        public Task ConnectAsync(CancellationToken ct = default) => Connection.ConnectAsync(ct);
        public Task CloseAsync() => Connection.CloseAsync();
        public void Pump() => Connection.Pump();
        public void Dispose() => Connection.Dispose();

        // ------------------------------------------------------------------ lobby actions (each returns its request ID, or null when offline)

        public string CreateRoom(CatalogPreset catalog) => Send(ClientMessages.RoomCreate(_newId(), catalog));
        public string ConfigureRoom(CatalogPreset catalog) => Send(ClientMessages.RoomConfigure(_newId(), catalog));
        public string JoinRoom(string code) => Send(ClientMessages.RoomJoin(_newId(), code));

        /// <summary>Confirms the room exactly as last shown (its revision), so a changed setting needs a new confirmation.</summary>
        public string ConfirmRoom() => Room == null ? null : Send(ClientMessages.RoomConfirm(_newId(), Room.Revision));

        public string LeaveRoom() => Send(ClientMessages.RoomLeave(_newId()));
        public string JoinQueue(CatalogPreset catalog) => Send(ClientMessages.QueueJoin(_newId(), catalog));
        /// <summary>Explicit consent to the offered, clearly labelled, unranked bot match.</summary>
        public string AcceptBotMatch() => Send(ClientMessages.QueueAcceptBot(_newId()));
        public string KeepWaiting() => Send(ClientMessages.QueueKeepWaiting(_newId()));
        public string CancelQueue() => Send(ClientMessages.QueueCancel(_newId()));

        /// <summary>Sends any message (tests use it for malformed or tampered input).</summary>
        public bool SendRaw(JsonNode message) => Connection.Send(message);

        private string Send(JsonNode message)
        {
            string rid = message.OptString("rid");
            return Connection.Send(message) ? rid : null;
        }

        // ------------------------------------------------------------------ dispatch

        private void OnWelcomed(WelcomeMessage welcome, bool reconnected)
        {
            OnlineMatchSession m = Match;
            if (m != null && !m.IsOver && (reconnected || welcome.ActiveMatchId == m.MatchId))
            {
                m.RequestResume();
                m.ResendPending();
            }
            else if (m == null && welcome.ActiveMatchId != null)
            {
                // A fresh process with a match still running (app restart): the server answers with
                // match.start and a snapshot.
                Connection.Send(ClientMessages.MatchResume(_newId(), welcome.ActiveMatchId, 0));
            }
        }

        private void OnMessage(JsonNode n)
        {
            try
            {
                Handle(n);
            }
            catch (FormatException)
            {
                // A malformed server message is ignored; the next update or a resume restores state.
            }
        }

        private void Handle(JsonNode n)
        {
            switch (n.Type())
            {
                case MessageTypes.RoomState:
                    Room = RoomStateMessage.Parse(n);
                    RoomChanged?.Invoke(Room);
                    break;
                case MessageTypes.RoomClosed:
                    RoomClosedMessage closed = RoomClosedMessage.Parse(n);
                    Room = null;
                    RoomClosed?.Invoke(closed);
                    break;
                case MessageTypes.QueueState:
                    Queue = QueueStateMessage.Parse(n);
                    QueueChanged?.Invoke(Queue);
                    if (Queue.Status == QueueStatus.Cancelled || Queue.Status == QueueStatus.Matched) Queue = null;
                    break;
                case MessageTypes.MatchStart:
                    MatchStartMessage start = MatchStartMessage.Parse(n);
                    if (Match != null && Match.MatchId == start.MatchId && !Match.IsOver) break; // resume of the same match
                    Room = null;
                    Queue = null;
                    Match = new OnlineMatchSession(start, Connection.Send, _newId);
                    MatchStarted?.Invoke(Match);
                    break;
                case MessageTypes.MatchUpdate:
                    Match?.OnUpdate(MatchUpdateMessage.Parse(n));
                    break;
                case MessageTypes.MatchReceipt:
                    Match?.OnReceipt(ReceiptMessage.Parse(n));
                    break;
                case MessageTypes.MatchEnd:
                    MatchEndMessage end = MatchEndMessage.Parse(n);
                    Match?.OnEnd(end);
                    MatchEnded?.Invoke(end);
                    break;
                case MessageTypes.ServerDraining:
                    ServerDraining?.Invoke();
                    break;
                case MessageTypes.Error:
                    ErrorMessage error = ErrorMessage.Parse(n);
                    LastError = error;
                    if (Match == null || !Match.OnError(error)) ErrorReceived?.Invoke(error);
                    break;
            }
        }
    }
}
