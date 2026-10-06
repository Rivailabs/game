using AstraKingdoms.Client.Online.Protocol;
using AstraKingdoms.Rules.Replay;
using AstraKingdoms.Server.Lobby;
using AstraKingdoms.Server.Matches;
using AstraKingdoms.Server.Realtime;
using AstraKingdoms.Server.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AstraKingdoms.Server.Ops;

/// <summary>Set when shutdown begins: /readyz turns 503 and new connections, rooms and queue entries are refused.</summary>
public sealed class DrainState
{
    private int _draining;
    public bool IsDraining => Volatile.Read(ref _draining) == 1;

    /// <summary>Starts draining; true only for the first caller.</summary>
    public bool Begin() => Interlocked.Exchange(ref _draining, 1) == 0;
}

/// <summary>
/// Startup recovery and graceful shutdown (ticket 56).
/// <para>
/// <b>Start:</b> matches suspended by a graceful shutdown are rebuilt by replaying their stored
/// record and resumed with the phase time they had left; matches left "active" by a crash are
/// settled as technical voids (the service could not resolve them: no reward, no loss).
/// </para>
/// <para>
/// <b>Stop:</b> readiness drops, lobby entries close with a reason, every active match is
/// suspended (Preserve, default) or technically voided (Void), clients are told the server is
/// draining and sockets close with 1001 so the client reconnects to the next instance.
/// </para>
/// </summary>
public sealed class LifecycleService : IHostedService
{
    private readonly MatchRegistry _matches;
    private readonly LobbyService _lobby;
    private readonly ConnectionRegistry _connections;
    private readonly DrainState _drain;
    private readonly ServerOptions _options;
    private readonly ILogger<LifecycleService> _log;

    public LifecycleService(MatchRegistry matches, LobbyService lobby, ConnectionRegistry connections, DrainState drain,
        IOptions<ServerOptions> options, ILogger<LifecycleService> log)
    {
        _matches = matches;
        _lobby = lobby;
        _connections = connections;
        _drain = drain;
        _options = options.Value;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _matches.RecoverAtStartup();
        _log.LogInformation("Match service ready: auth {Auth}, shutdown policy {Policy}", _options.Auth.Mode, _options.Lifecycle.ShutdownPolicy);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        DrainNow();
        return Task.CompletedTask;
    }

    /// <summary>Runs the shutdown sequence (idempotent).</summary>
    public void DrainNow()
    {
        if (!_drain.Begin()) return;
        _log.LogInformation("Draining: {Matches} active matches, policy {Policy}", _matches.ActiveCount, _options.Lifecycle.ShutdownPolicy);
        _lobby.CloseAll();
        _matches.OnShutdown(_options.Lifecycle.ShutdownPolicy);
        JsonNode notice = Json.Message(MessageTypes.ServerDraining)
            .Add("policy", _options.Lifecycle.ShutdownPolicy == ShutdownMatchPolicy.Preserve ? "preserve" : "void");
        foreach (ClientConnection c in _connections.All)
        {
            c.Enqueue(notice.ToCanonicalString());
            c.RequestClose(CloseReasons.ShuttingDown);
        }
    }
}

/// <summary>Hourly retention sweep: settled match records past their retention date are deleted (plan default 30 days).</summary>
public sealed class RetentionService : BackgroundService
{
    private readonly IMatchRepository _repo;
    private readonly TimeProvider _time;
    private readonly ILogger<RetentionService> _log;

    public RetentionService(IMatchRepository repo, TimeProvider time, ILogger<RetentionService> log)
    {
        _repo = repo;
        _time = time;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), _time);
        try
        {
            do
            {
                int removed = _repo.PurgeExpired(_time.GetUtcNow());
                if (removed > 0) _log.LogInformation("Retention: removed {Count} expired match records", removed);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }
    }
}
