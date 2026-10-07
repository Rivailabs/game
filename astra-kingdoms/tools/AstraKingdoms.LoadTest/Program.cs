using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using AstraKingdoms.Client.Online;
using AstraKingdoms.Client.Online.Protocol;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;

namespace AstraKingdoms.LoadTest;

/// <summary>
/// Load and fault check for the online match service (ticket 56).
/// <para>
/// <b>Declared initial capacity scenario.</b> One service instance (2 vCPU, 2 GB, SQLite on local
/// SSD) carries 200 concurrent matches (400 connected players) at production timers. Pass: every
/// match settles, well-behaved clients see no rejected command, command receipt latency p95 ≤ 250 ms
/// and p99 ≤ 500 ms, every player receives exactly one match.end, and players whose link was cut
/// recover through reconnection. These are proposed V1 numbers to validate on the real host; a run
/// on a developer machine checks the tool and the service, not the deployment.
/// </para>
/// Usage: dotnet run -c Release --project tools/AstraKingdoms.LoadTest -- --url ws://localhost:8080
///        [--matches 200] [--drop-percent 10] [--timeout-minutes 20] [--mode room|queue] [--policy fast|bot] [--report path]
/// The server must run with Auth:Mode=Dev (and AllowDevOutsideDevelopment on a private load host).
/// </summary>
public static class Program
{
    private sealed class Options
    {
        public Uri Url = new("ws://localhost:8080");
        public int Matches = 200;
        public int DropPercent = 10;
        public int TimeoutMinutes = 20;
        public string Mode = "room";
        public string Report;
        /// <summary>"fast": cheap legal choices so the client machine measures the server; "bot": the full rules bot.</summary>
        public string Policy = "fast";
        public double P95LimitMs = 250;
        public double P99LimitMs = 500;
    }

    private sealed class Stats
    {
        public readonly ConcurrentBag<double> ReceiptMs = new();
        public readonly ConcurrentDictionary<string, ConcurrentBag<double>> ByKind = new();
        public int Settled;
        public int Rejections;
        public int Errors;
        public int ConnectFailures;
        public int Drops;
        public int Recovered;
        public int DuplicateEnds;
        public readonly ConcurrentDictionary<string, int> Outcomes = new();
    }

    public static async Task<int> Main(string[] args)
    {
        Options o = Parse(args);
        var stats = new Stats();
        string run = DateTime.UtcNow.ToString("HHmmss", CultureInfo.InvariantCulture);
        Console.WriteLine($"Load test: {o.Matches} matches ({o.Matches * 2} players) against {o.Url}, mode {o.Mode}, link drops {o.DropPercent}%");
        var sw = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(o.TimeoutMinutes));
        var tasks = new List<Task>();
        for (int i = 0; i < o.Matches; i++)
        {
            int index = i;
            tasks.Add(Task.Run(() => RunPair(o, stats, run, index, timeout.Token)));
            if (i % 20 == 19) await Task.Delay(100); // ramp up instead of a thundering herd
        }
        await Task.WhenAll(tasks);
        sw.Stop();

        double[] lat = stats.ReceiptMs.OrderBy(x => x).ToArray();
        double P(double q) => lat.Length == 0 ? double.NaN : lat[Math.Min(lat.Length - 1, (int)Math.Ceiling(q * lat.Length) - 1)];
        bool pass = stats.Settled == o.Matches && stats.Rejections == 0 && stats.Errors == 0 && stats.ConnectFailures == 0 &&
                    stats.DuplicateEnds == 0 && stats.Recovered == stats.Drops && P(0.95) <= o.P95LimitMs && P(0.99) <= o.P99LimitMs;
        var report = new StringBuilder();
        report.AppendLine("# Online load test");
        report.AppendLine();
        report.AppendLine($"- Target: {o.Url} ({o.Mode}, client policy {o.Policy})");
        report.AppendLine($"- Matches: {o.Matches} ({o.Matches * 2} players), settled {stats.Settled}");
        report.AppendLine($"- Outcomes: {string.Join(", ", stats.Outcomes.Select(kv => kv.Key + " " + kv.Value))}");
        report.AppendLine($"- Wall time: {sw.Elapsed.TotalSeconds:0.0} s");
        report.AppendLine($"- Commands: {lat.Length}; receipt latency p50 {P(0.5):0.0} ms, p95 {P(0.95):0.0} ms, p99 {P(0.99):0.0} ms, max {(lat.Length == 0 ? 0 : lat[^1]):0.0} ms");
        foreach (KeyValuePair<string, ConcurrentBag<double>> kv in stats.ByKind.OrderBy(k => k.Key))
        {
            double[] k = kv.Value.OrderBy(x => x).ToArray();
            report.AppendLine($"  - {kv.Key}: {k.Length} commands, p50 {k[k.Length / 2]:0.0} ms, p95 {k[Math.Min(k.Length - 1, (int)Math.Ceiling(0.95 * k.Length) - 1)]:0.0} ms");
        }
        report.AppendLine($"- Service-side command handling (from /readyz): {await ServiceTimings(o.Url)}");
        report.AppendLine($"- Link drops injected {stats.Drops}, recovered {stats.Recovered}");
        report.AppendLine($"- Rejected commands {stats.Rejections}, service errors {stats.Errors}, connect failures {stats.ConnectFailures}, duplicate match.end {stats.DuplicateEnds}");
        report.AppendLine($"- Declared scenario limits: p95 <= {o.P95LimitMs} ms, p99 <= {o.P99LimitMs} ms, all settled, no rejections");
        report.AppendLine($"- Result: **{(pass ? "PASS" : "FAIL")}**");
        Console.WriteLine(report.ToString());
        if (o.Report != null) File.WriteAllText(o.Report, report.ToString());
        return pass ? 0 : 1;
    }

    private static async Task RunPair(Options o, Stats stats, string run, int index, CancellationToken ct)
    {
        OnlineClient a = null, b = null;
        try
        {
            a = await Connect(o, $"load{run}-{index}-a", stats, ct);
            b = await Connect(o, $"load{run}-{index}-b", stats, ct);
            if (a == null || b == null) return;
            var settled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int ends = 0;
            void OnEnd(MatchEndMessage end)
            {
                stats.Outcomes.AddOrUpdate(end.Outcome, 1, (_, n) => n + 1);
                if (Interlocked.Increment(ref ends) == 2) settled.TrySetResult(true);
            }
            a.MatchEnded += OnEnd;
            b.MatchEnded += OnEnd;
            Autopilot(a, stats, (ulong)index * 2 + 1, o.Policy == "fast");
            Autopilot(b, stats, (ulong)index * 2 + 2, o.Policy == "fast");

            if (o.Mode == "queue")
            {
                a.JoinQueue(CatalogPreset.Starter);
                b.JoinQueue(CatalogPreset.Starter);
            }
            else
            {
                var room = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                a.RoomChanged += r => room.TrySetResult(r.Code);
                a.CreateRoom(CatalogPreset.Starter);
                string code = await room.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
                // Both must have seen the full room (its latest revision) before confirming it.
                var fullA = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var fullB = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                a.RoomChanged += r =>
                {
                    if (r.Members == 2) fullA.TrySetResult(true);
                };
                b.RoomChanged += r =>
                {
                    if (r.Members == 2) fullB.TrySetResult(true);
                };
                b.JoinRoom(code);
                await Task.WhenAll(fullA.Task, fullB.Task).WaitAsync(TimeSpan.FromSeconds(30), ct);
                a.ConfirmRoom();
                b.ConfirmRoom();
            }

            // Fault injection: cut one player's link once, mid-match.
            if (o.DropPercent > 0 && index % 100 < o.DropPercent)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(5 + index % 10), ct);
                    Interlocked.Increment(ref stats.Drops);
                    int before = b.Connection.ConnectCount;
                    b.Connection.DropLink();
                    for (int i = 0; i < 600 && b.Connection.ConnectCount == before; i++) await Task.Delay(100, ct);
                    if (b.Connection.ConnectCount > before) Interlocked.Increment(ref stats.Recovered);
                }, ct);
            }

            await settled.Task.WaitAsync(ct);
            Interlocked.Increment(ref stats.Settled);
            await Task.Delay(500, CancellationToken.None); // late duplicates would arrive here
            if (ends != 2) Interlocked.Increment(ref stats.DuplicateEnds);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine($"pair {index}: timed out");
        }
        catch (Exception e)
        {
            Interlocked.Increment(ref stats.Errors);
            Console.Error.WriteLine($"pair {index}: {e.Message}");
        }
        finally
        {
            if (a != null) await a.CloseAsync();
            if (b != null) await b.CloseAsync();
        }
    }

    private static async Task<OnlineClient> Connect(Options o, string name, Stats stats, CancellationToken ct)
    {
        var client = new OnlineClient(new OnlineClientOptions
        {
            ServerUri = o.Url,
            ClientVersion = "loadtest",
            TokenProvider = _ => Task.FromResult("dev:" + name),
            DispatchOnReceiveThread = true,
        });
        client.ErrorReceived += e =>
        {
            Interlocked.Increment(ref stats.Errors);
            Console.Error.WriteLine($"{name}: {e.Code} {e.Message}");
        };
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(30));
            await client.ConnectAsync(linked.Token);
            return client;
        }
        catch (Exception e)
        {
            Interlocked.Increment(ref stats.ConnectFailures);
            Console.Error.WriteLine($"{name}: connect failed: {e.Message}");
            client.Dispose();
            return null;
        }
    }

    /// <summary>Plays with the rules bot on this player's own view and records receipt latency.</summary>
    private static void Autopilot(OnlineClient client, Stats stats, ulong seed, bool fast)
    {
        var sent = new ConcurrentDictionary<string, long>();
        client.Connection.MessageReceived += n =>
        {
            if (n.Type() != MessageTypes.MatchReceipt) return;
            string id = n.OptString("request_id");
            if (id != null && sent.TryRemove(id, out long t0))
            {
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                stats.ReceiptMs.Add(ms);
                stats.ByKind.GetOrAdd(n.OptString("kind") ?? "?", _ => new ConcurrentBag<double>()).Add(ms);
            }
        };
        client.MatchStarted += session =>
        {
            var bot = new BotPlayer(session.LocalSide, new BotPolicy(BotDifficulty.Normal, new BotRng(seed)), new BotRng(seed * 31 + 7));
            string lastKey = null;
            object gate = new();
            session.CommandRejected += code =>
            {
                Interlocked.Increment(ref stats.Rejections);
                Console.Error.WriteLine($"{session.MatchId}: rejected {code}");
            };
            session.ViewChanged += view =>
            {
                lock (gate)
                {
                    bool work = view.Phase switch
                    {
                        MatchPhase.Setup => view.OwnLoadout == null,
                        MatchPhase.Selection => view.OwnLock == null,
                        MatchPhase.CardAndCut => view.IsCutTurn,
                        _ => false,
                    };
                    string key = view.StateRevision + "/" + view.Phase + "/" + (view.OwnLoadout != null) + "/" + (view.OwnLock != null);
                    if (!work || key == lastKey) return;
                    lastKey = key;
                    MatchCommand cmd = fast ? FastDecide(view, seed) : bot.Decide(view);
                    if (cmd == null) return;
                    sent[cmd.Header.RequestId] = Stopwatch.GetTimestamp();
                    session.SendCommand(cmd);
                }
            };
        };
    }

    /// <summary>
    /// Legal, cheap decisions (the selection screen's defaults and a one-candidate Auto Cut), so a
    /// load run spends its client CPU on traffic rather than on bot search. The server's work per
    /// command is the same as for any other legal command.
    /// </summary>
    private static MatchCommand FastDecide(PlayerView v, ulong seed)
    {
        string id = Guid.NewGuid().ToString("D");
        switch (v.Phase)
        {
            case MatchPhase.Setup:
                return new SubmitLoadoutCommand(v.NewHeader(id), new[] { 1, 2, 3 });
            case MatchPhase.Selection:
                WeaponDefinition w = WeaponCatalog.Get(v.OwnLoadout.Weapons[(int)(seed % (ulong)v.OwnLoadout.Weapons.Count)]);
                int pitch = AstraKingdoms.Rules.Combat.LaunchProfiles.CentralPitchRange(w).Clamp(20 * RulesConstants.QuarterDegreesPerDegree);
                return new LockInputCommand(v.NewHeader(id), v.VolleyIndex, w.Id, pitch, 0, RulesConstants.MaxPowerPercent, Dodge.None);
            case MatchPhase.CardAndCut:
                CutPlan plan = CutPlanner.Plan(v, new CutSearchBudget { Anchors = 1, Rotations = 1, ScalePercent = 60 }, new BotRng(seed));
                return plan == null ? null : new SubmitCutCommand(v.NewHeader(id), v.MapRevision, plan.Card, plan.AnchorCellId, plan.Pose.CenterX,
                    plan.Pose.CenterY, plan.Pose.Rotation, plan.Pose.ScaleQuarters, CutMode.Auto);
            default:
                return null;
        }
    }

    /// <summary>The service's own command handling quantiles, read from /readyz on the same host.</summary>
    private static async Task<string> ServiceTimings(Uri ws)
    {
        try
        {
            Uri http = new UriBuilder(ws) { Scheme = ws.Scheme == "wss" ? "https" : "http", Path = "/readyz" }.Uri;
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var doc = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync(http));
            System.Text.Json.JsonElement r = doc.RootElement;
            return $"p50 {r.GetProperty("command_ms_p50")} ms, p95 {r.GetProperty("command_ms_p95")} ms, p99 {r.GetProperty("command_ms_p99")} ms (last {Math.Min(8192, r.GetProperty("commands").GetInt64())} commands)";
        }
        catch (Exception e)
        {
            return "unavailable (" + e.Message + ")";
        }
    }

    private static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("Missing value for " + args[i]);
            switch (args[i])
            {
                case "--url": o.Url = new Uri(Next()); break;
                case "--matches": o.Matches = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--drop-percent": o.DropPercent = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--timeout-minutes": o.TimeoutMinutes = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--mode": o.Mode = Next(); break;
                case "--report": o.Report = Next(); break;
                case "--policy": o.Policy = Next(); break;
                case "--p95-ms": o.P95LimitMs = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--p99-ms": o.P99LimitMs = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                default: throw new ArgumentException("Unknown option " + args[i]);
            }
        }
        return o;
    }
}
