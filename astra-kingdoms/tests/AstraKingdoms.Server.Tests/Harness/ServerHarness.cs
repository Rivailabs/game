using System.Collections.Concurrent;
using System.Net.WebSockets;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Server.Lobby;
using AstraKingdoms.Server.Matches;
using AstraKingdoms.Server.Ops;
using AstraKingdoms.Server.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AstraKingdoms.Server.Tests.Harness;

/// <summary>Captures every log line (message, exception and structured values) for redaction tests.</summary>
internal sealed class LogSink : ILoggerProvider
{
    public readonly ConcurrentQueue<string> Lines = new();

    public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);
    public void Dispose()
    {
    }

    public string All => string.Join("\n", Lines);

    private sealed class Sink : ILogger
    {
        private readonly LogSink _owner;
        private readonly string _category;

        public Sink(LogSink owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            string values = state is IEnumerable<KeyValuePair<string, object>> kv ? string.Join(",", kv.Select(p => p.Key + "=" + p.Value)) : string.Empty;
            _owner.Lines.Enqueue(logLevel + " " + _category + ": " + formatter(state, exception) + " {" + values + "} " + exception);
        }
    }
}

/// <summary>
/// The match service in-process on a fake clock. Every timer (phase deadlines, room expiry, the
/// bot offer, rate-limit refills) moves only when a test calls <see cref="Advance"/>.
/// </summary>
internal sealed class ServerHarness : IAsyncDisposable
{
    public static readonly DateTimeOffset Epoch = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);

    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly ServerHarness _h;
        public Factory(ServerHarness h) => _h = h;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(_h._environment);
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(_h._settings));
            builder.ConfigureLogging(l => l.AddProvider(_h.Logs));
            builder.ConfigureTestServices(s =>
            {
                s.AddSingleton<TimeProvider>(_h.Time);
                _h._services?.Invoke(s);
            });
        }
    }

    private readonly Dictionary<string, string> _settings;
    private readonly Action<IServiceCollection> _services;
    private readonly string _environment;
    private readonly Factory _factory;

    public FakeTimeProvider Time { get; }
    public string DbPath { get; }
    public LogSink Logs { get; } = new();

    private ServerHarness(Dictionary<string, string> settings, string dbPath, FakeTimeProvider time, Action<IServiceCollection> services, string environment)
    {
        Time = time ?? new FakeTimeProvider(Epoch);
        DbPath = dbPath ?? Path.Combine(Path.GetTempPath(), "ak-server-tests", Guid.NewGuid().ToString("N") + ".db");
        _environment = environment;
        _services = services;
        _settings = new Dictionary<string, string>
        {
            ["AstraServer:Auth:Mode"] = "Dev",
            ["AstraServer:Storage:SqlitePath"] = DbPath,
            ["AstraServer:Timings:BotThinkMs"] = "0",
        };
        if (settings != null)
            foreach (KeyValuePair<string, string> kv in settings) _settings[kv.Key] = kv.Value;
        _factory = new Factory(this);
        _ = _factory.Server; // start now
    }

    public static ServerHarness Start(Dictionary<string, string> settings = null, string dbPath = null, FakeTimeProvider time = null,
        Action<IServiceCollection> services = null, string environment = "Development") =>
        new(settings, dbPath, time, services, environment);

    public TestServer Server => _factory.Server;
    public T Get<T>() => _factory.Services.GetRequiredService<T>();
    public MatchRegistry Matches => Get<MatchRegistry>();
    public LobbyService Lobby => Get<LobbyService>();
    public SqliteStore Store => Get<SqliteStore>();

    public HttpClient Http(string token = null)
    {
        HttpClient c = _factory.CreateClient();
        if (token != null) c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    /// <summary>Opens a raw socket with the given headers (null token = no Authorization header).</summary>
    public Task<WebSocket> RawSocketAsync(string token, CancellationToken ct = default)
    {
        WebSocketClient client = Server.CreateWebSocketClient();
        client.ConfigureRequest = r =>
        {
            if (token != null) r.Headers.Authorization = "Bearer " + token;
        };
        return client.ConnectAsync(new Uri("ws://localhost/v1/ws"), ct);
    }

    public void Advance(TimeSpan by) => Time.Advance(by);
    public void AdvanceMs(long ms) => Time.Advance(TimeSpan.FromMilliseconds(ms));

    /// <summary>Moves the fake clock exactly to the match's open deadline (firing it).</summary>
    public void AdvanceToDeadline(MatchHost host)
    {
        DateTimeOffset? d = host.Deadline;
        if (d == null) return;
        TimeSpan by = d.Value - Time.GetUtcNow();
        Time.Advance(by > TimeSpan.Zero ? by : TimeSpan.FromMilliseconds(1));
    }

    /// <summary>Runs the shutdown sequence (as the host's StopAsync would).</summary>
    public void Drain() => Get<LifecycleService>().DrainNow();

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    /// <summary>Polls (real time) until <paramref name="condition"/> holds.</summary>
    public static async Task Until(Func<bool> condition, string what, int timeoutMs = 10000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) Assert.Fail("Timed out waiting for " + what);
            await Task.Delay(2);
        }
    }

    /// <summary>The server-side private view text of a seat (for exact comparisons with a client).</summary>
    public static string ServerViewText(MatchHost host, Rules.Core.PlayerSide side) => host.Engine.GetView(side).ToCanonicalText();

    public StoredMatch Stored(string matchId) => Store.Get(matchId);
    public IReadOnlyList<AuditEntry> Audit(string matchId) => Store.Read(matchId);
    public IReadOnlyList<RewardGrant> Grants(string resultId) => Store.GrantsForResult(resultId);

    public static bool HasWork(PlayerView v) => v.Phase switch
    {
        MatchPhase.Setup => v.OwnLoadout == null,
        MatchPhase.Selection => v.OwnLock == null,
        MatchPhase.CardAndCut => v.IsCutTurn,
        _ => false,
    };
}
