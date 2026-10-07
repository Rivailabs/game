using System.Globalization;
using System.Text;
using AstraKingdoms.Rules.Balance;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Sim;

/// <summary>
/// Side-by-side screening of a balance bundle against the AK-TR-1 baseline (ticket 24 tooling).
/// Both runs use the same seeds, pairings and seat swaps, so the only difference between two
/// paired matches is the pinned <see cref="RulesParameters"/>. The report is always labelled
/// PROPOSED: a simulation never adopts a bundle; only a release authority publishing it to a
/// <see cref="BalanceChannel"/> does.
/// </summary>
public sealed class BundleComparison
{
    private readonly BalanceBundle _bundle;
    private readonly BalanceReport _baseline;
    private readonly BalanceReport _proposed;
    private readonly List<MatchObservation> _baseObs;
    private readonly List<MatchObservation> _propObs;
    private readonly List<string> _runLines;
    private readonly List<string[]> _csv = new();

    public BundleComparison(BalanceBundle bundle, IEnumerable<MatchObservation> baseline, IEnumerable<MatchObservation> proposed,
        IEnumerable<string> runLines)
    {
        _bundle = bundle ?? throw new ArgumentNullException(nameof(bundle));
        _baseObs = baseline.ToList();
        _propObs = proposed.ToList();
        _runLines = runLines.ToList();
        _baseline = new BalanceReport(new ReportContext(), _baseObs);
        _proposed = new BalanceReport(new ReportContext(), _propObs);
    }

    /// <summary>Screened comparisons that are FLAG in the baseline run.</summary>
    public IReadOnlyList<ScreenedComparison> BaselineFlags => _baseline.Screened().Where(s => s.Status == "FLAG").ToList();

    /// <summary>Screened comparisons that are FLAG under the bundle.</summary>
    public IReadOnlyList<ScreenedComparison> ProposedFlags => _proposed.Screened().Where(s => s.Status == "FLAG").ToList();

    public string Markdown()
    {
        _csv.Clear();
        var sb = new StringBuilder();
        sb.Append("# PROPOSED balance bundle `").Append(_bundle.BundleId).Append("` versus `").Append(_bundle.BaseRulesVersion).Append("`\n\n");
        sb.Append("> **PROPOSED - NOT ADOPTED.** This is a bot-policy screening comparison of a candidate balance release. ")
          .Append("Nothing here is live: the bundle changes outcomes only for matches pinned to it, and it is pinned only after a release ")
          .Append("authority publishes it to a balance channel (ticket 24). Bot results are screening evidence, not balance proof; ")
          .Append("human playtests decide.\n\n");

        Bundle(sb);
        sb.Append("## Run\n\n");
        foreach (string line in _runLines) sb.Append("- ").Append(line).Append('\n');
        sb.Append("- Both columns replay the same seeds, pairings and seat swaps; paired matches differ only in their pinned parameters.\n");
        sb.Append("- Win shares are among decisive outcomes; intervals are Wilson 95%; status uses the same Bonferroni family rule as the screening report.\n\n");

        Headline(sb);
        Screening(sb);
        Notes(sb);
        return sb.ToString();
    }

    public string Csv()
    {
        if (_csv.Count == 0) Markdown();
        var sb = new StringBuilder("section,kind,key,baseline_n,baseline_share,baseline_status,proposed_n,proposed_share,proposed_status,delta_pp\n");
        foreach (string[] row in _csv) sb.Append(string.Join(",", row.Select(Escape))).Append('\n');
        return sb.ToString();
    }

    private static string Escape(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private void Bundle(StringBuilder sb)
    {
        sb.Append("## Candidate bundle\n\n");
        sb.Append($"- Bundle ID: `{_bundle.BundleId}` (derived from `{_bundle.PreviousBundleId}`)\n");
        sb.Append($"- Content hash: `{_bundle.ContentHashHex}`\n");
        sb.Append($"- Effective rules hash: `{_bundle.EffectiveRulesHashHex}` (baseline `{RulesBundle.HashHex}`)\n");
        sb.Append($"- Notes: {_bundle.Notes}\n\n");
        sb.Append("| Tunable | Baseline | Proposed | Change | Unit |\n|---|---:|---:|---:|---|\n");
        foreach (var kv in _bundle.Overrides)
        {
            TunableSchema.TryGet(kv.Key, out TunableDefinition? def);
            long baseline = def?.Baseline ?? 0;
            string change = baseline == 0 ? "n/a" : ((kv.Value - baseline) * 100.0 / baseline).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "%";
            sb.Append($"| `{kv.Key}` | {baseline} | {kv.Value} | {change} | {def?.Unit} |\n");
        }
        sb.Append('\n');
    }

    private void Headline(StringBuilder sb)
    {
        sb.Append("## Match shape\n\n| Metric | Baseline | Proposed | Delta |\n|---|---:|---:|---:|\n");
        void Share(string name, Func<List<MatchObservation>, (long K, long N)> f)
        {
            var (bk, bn) = f(_baseObs);
            var (pk, pn) = f(_propObs);
            double b = bn == 0 ? double.NaN : (double)bk / bn, p = pn == 0 ? double.NaN : (double)pk / pn;
            sb.Append($"| {name} | {Stats.Pct(b)} ({bk}/{bn}) | {Stats.Pct(p)} ({pk}/{pn}) | {Pp(p - b)} |\n");
            _csv.Add(new[] { "match_shape", "share", name, Stats.I(bn), Stats.F(b), "", Stats.I(pn), Stats.F(p), "", Stats.F((p - b) * 100) });
        }
        Share("Reached the final round", l => (l.Count(o => o.Result.RoundsPlayed >= o.Config.Parameters.MaxRounds), l.Count));
        Share("Ended by territory victory", l => (l.Count(o => o.Result.Reason == TerminalReason.Territory90), l.Count));
        Share("Duels drawn", l => (l.Sum(o => o.Duels.Count(d => d.Result == Rules.Combat.DuelResult.Draw)), l.Sum(o => o.Duels.Count)));
        Share("First attacker wins (mirror, decisive)", l =>
        {
            var t = new Tally();
            foreach (var m in l.Where(m => m.Mirror)) t.Add(m.Outcome(m.FirstAttacker));
            return (t.Wins, t.Decisive);
        });
        Share("Trailing < 35% after round 4 comes back (mirror, decisive)", l =>
        {
            var t = new Tally();
            foreach (var m in l.Where(m => m.Mirror))
            {
                RoundCells? c = m.CheckpointAfter(4);
                if (c == null || c.Value.CellsA == c.Value.CellsB) continue;
                PlayerSide trailing = c.Value.CellsA < c.Value.CellsB ? PlayerSide.A : PlayerSide.B;
                if ((double)Math.Min(c.Value.CellsA, c.Value.CellsB) / RulesConstants.ActiveCells < 0.35) t.Add(m.Outcome(trailing));
            }
            return (t.Wins, t.Decisive);
        });
        // Both runs list their matches in job order, so equal positions are the same seed and seats.
        // (Mirrored games share a match ID, hence the positional pairing.)
        int changed = 0, paired = 0;
        for (int i = 0; i < Math.Min(_baseObs.Count, _propObs.Count); i++)
        {
            MatchObservation b = _baseObs[i], p = _propObs[i];
            if (b.MatchId != p.MatchId || b.A.Label != p.A.Label || b.B.Label != p.B.Label) continue;
            paired++;
            if (b.Result.Winner != p.Result.Winner || b.Result.CellsA != p.Result.CellsA) changed++;
        }
        sb.Append($"\nPaired matches whose final result or cell count changed: **{changed}** of {paired}.\n\n");
    }

    private void Screening(StringBuilder sb)
    {
        IReadOnlyList<ScreenedComparison> b = _baseline.Screened();
        var p = _proposed.Screened().ToDictionary(s => s.Id);
        int baseFlags = b.Count(s => s.Status == "FLAG"), propFlags = p.Values.Count(s => s.Status == "FLAG");
        int resolved = b.Count(s => s.Status == "FLAG" && p.TryGetValue(s.Id, out var q) && q.Status != "FLAG");
        int introduced = b.Count(s => s.Status != "FLAG" && p.TryGetValue(s.Id, out var q) && q.Status == "FLAG");

        sb.Append("## Screening flags: baseline versus proposed\n\n");
        sb.Append($"FLAG count: **{baseFlags} → {propFlags}** ({resolved} resolved, {introduced} introduced). ");
        sb.Append("Rows show every comparison that is flagged or on watch in either run; the rest are consistent with their band in both.\n\n");
        sb.Append("| Kind | Key | Band | Baseline | Status | Proposed | Status | Delta |\n|---|---|---|---:|---|---:|---|---:|\n");
        foreach (ScreenedComparison s in b)
        {
            p.TryGetValue(s.Id, out ScreenedComparison? q);
            string qs = q?.Status ?? "n/a";
            _csv.Add(new[] { "screening", s.Kind, s.Key, Stats.I(s.Tally.Decisive), Stats.F(s.Tally.Rate), s.Status,
                Stats.I(q?.Tally.Decisive ?? 0), Stats.F(q?.Tally.Rate ?? double.NaN), qs, Stats.F(((q?.Tally.Rate ?? double.NaN) - s.Tally.Rate) * 100) });
            if (s.Status == "ok" && qs == "ok") continue;
            sb.Append($"| {s.Kind} | {s.Key} | {Stats.Pct(s.BandLo)}-{Stats.Pct(s.BandHi)} | {Stats.Pct(s.Tally.Rate)} ({s.Tally.Decisive}) | {s.Status} | ")
              .Append(q == null ? "n/a | n/a" : $"{Stats.Pct(q.Tally.Rate)} ({q.Tally.Decisive}) | {q.Status}")
              .Append($" | {Pp((q?.Tally.Rate ?? double.NaN) - s.Tally.Rate)} |\n");
        }
        sb.Append('\n');
    }

    private static void Notes(StringBuilder sb)
    {
        sb.Append("## Reading notes\n\n");
        sb.Append("- Element multipliers (150% / 50%) and abilities are structural in AK-TR-1 and cannot be changed by a balance bundle; ")
          .Append("element totals can only move through the damage of that element's weapons. Element flags that persist need a rules-version change, not a bundle.\n");
        sb.Append("- Bots aim from a solver and pick weapons by fixed heuristics; Hard bots read tuned damage from the pinned parameters, so usage shifts are part of the result.\n");
        sb.Append("- A bundle that clears bot flags still needs human playtests (ticket 25 ingest with `--bundle`) before any publication decision.\n");
        sb.Append("- Re-run: `dotnet run -c Release --project tools/AstraKingdoms.Sim -- --matches 2000 --cohorts --bundle <file> --compare`.\n");
    }

    private static string Pp(double delta) =>
        double.IsNaN(delta) ? "n/a" : (delta * 100).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " pp";
}
