using System.Text;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Sim;

/// <summary>Run metadata printed at the top of a report.</summary>
public sealed class ReportContext
{
    public string Title { get; init; } = "Astra Kingdoms balance review";
    /// <summary>"bot-simulation", "playtest" or a mix; decides the disclaimer.</summary>
    public string SourceDescription { get; init; } = "";
    public List<string> RunLines { get; init; } = new();
    public List<IngestExclusion> Excluded { get; init; } = new();
    public List<string> Errors { get; init; } = new();
}

/// <summary>One screened comparison against its provisional band, with its screening status.</summary>
public sealed record ScreenedComparison(string Kind, string Key, Tally Tally, double BandLo, double BandHi,
    (double Lo, double Hi) Wilson95, (double Lo, double Hi) Family, string Status)
{
    /// <summary>Stable identity used to line up the same comparison across two runs.</summary>
    public string Id => Kind + " | " + Key;
}

/// <summary>
/// The stratified human/bot balance review (ticket 25). It runs identically over simulated bot
/// matches and verified human playtest records and makes these inspectable: policy pairings,
/// first-player effects, unlock cohorts, comeback behaviour, element matchups, terrain, and weapon
/// results both usage-conditioned and loadout-contained, each with Wilson 95% intervals, a
/// Bonferroni family interval for screening flags, and an explicit "insufficient data" state.
/// </summary>
public sealed class BalanceReport
{
    /// <summary>Provisional screening bands from the plan (hypotheses, not acceptance criteria).</summary>
    public const double WeaponBandLo = 0.45, WeaponBandHi = 0.55, ElementBandLo = 0.47, ElementBandHi = 0.53;

    private readonly ReportContext _ctx;
    private readonly List<MatchObservation> _all;
    private readonly List<MatchObservation> _mirror;
    private readonly List<string[]> _csv = new();
    private readonly List<(string Kind, string Key, Tally T, double Lo, double Hi)> _screen = new();

    public BalanceReport(ReportContext ctx, IEnumerable<MatchObservation> observations)
    {
        _ctx = ctx;
        _all = observations.ToList();
        _mirror = _all.Where(m => m.Mirror).ToList();
    }

    public IReadOnlyList<string[]> CsvRows => _csv;

    private const string TableHead = "| Key | n | W/L/D | Win share (decisive) | Wilson 95% |\n|---|---:|---:|---:|---|\n";

    private void Row(StringBuilder sb, string metric, string group, string key, Tally t)
    {
        var ci = t.Wilson();
        string share = t.Decisive < Stats.MinDecisive && t.Decisive > 0 ? Stats.Pct(t.Rate) + " (insufficient)" : Stats.Pct(t.Rate);
        sb.Append("| ").Append(key).Append(" | ").Append(t.Total).Append(" | ").Append(t.Wins).Append('/').Append(t.Losses).Append('/').Append(t.Draws)
          .Append(" | ").Append(share).Append(" | ").Append(Stats.Interval(ci)).Append(" |\n");
        Csv(metric, group, key, t);
    }

    private void Csv(string metric, string group, string key, Tally t)
    {
        var ci = t.Wilson();
        _csv.Add(new[] { metric, group, key, Stats.I(t.Total), Stats.I(t.Wins), Stats.I(t.Losses), Stats.I(t.Draws), Stats.F(t.Rate), Stats.F(ci.Lo), Stats.F(ci.Hi) });
    }

    private static Tally Get<TK>(Dictionary<TK, Tally> d, TK k) where TK : notnull
    {
        if (!d.TryGetValue(k, out Tally? t)) d[k] = t = new Tally();
        return t;
    }

    private static int Sign(int v) => v > 0 ? 1 : v < 0 ? -1 : 0;

    public string Markdown()
    {
        _csv.Clear();
        _screen.Clear();
        var sb = new StringBuilder();
        Header(sb);
        if (_all.Count == 0)
        {
            sb.Append("No usable matches. Nothing to report.\n");
            return sb.ToString();
        }
        Length(sb);
        Pairings(sb);
        FirstAttacker(sb);
        Cohorts(sb);
        Elements(sb);
        Weapons(sb);
        Terrain(sb);
        Comebacks(sb);
        Screening(sb);
        Notes(sb);
        return sb.ToString();
    }

    public string Csv()
    {
        if (_csv.Count == 0) Markdown();
        var sb = new StringBuilder("metric,group,key,n,wins,losses,draws,win_share,wilson_lo,wilson_hi\n");
        foreach (var row in _csv) sb.Append(string.Join(",", row.Select(Escape))).Append('\n');
        return sb.ToString();
    }

    private static string Escape(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    // ------------------------------------------------------------------ sections

    private void Header(StringBuilder sb)
    {
        sb.Append("# ").Append(_ctx.Title).Append("\n\n");
        bool bots = _all.Any(o => o.Source == MatchObservation.BotSimulation);
        bool people = _all.Any(o => o.A.IsHuman || o.B.IsHuman);
        if (bots && !people)
            sb.Append("> **Bot-policy screening results, not balance proof.** Outcomes reflect scripted policies (Easy/Normal/Hard) with fixed heuristics; humans choose weapons, aim and cuts differently. Use them to choose human tests, not to tune constants on one aggregate.\n\n");
        if (people)
            sb.Append("> **Human playtest data.** Small samples: most strata will read \"insufficient\". Clarity and enjoyment are decided by observation notes, not by these tables. Every record was re-executed by the replay verifier before it was counted.\n\n");
        if (_all.Any(o => o.Synthetic))
            sb.Append("> **Contains synthetic example records** (bot-played, labelled as human only to demonstrate the format). Do not read them as human data.\n\n");

        sb.Append("## Data\n\n");
        foreach (string line in _ctx.RunLines) sb.Append("- ").Append(line).Append('\n');
        sb.Append("- Source: ").Append(_ctx.SourceDescription).Append('\n');
        sb.Append($"- Matches used: {_all.Count} ({_all.Count(o => o.A.IsHuman || o.B.IsHuman)} with a human seat, {_all.Count(o => o.Synthetic)} synthetic); like-for-like (mirror-label) matches: {_mirror.Count}\n");
        sb.Append("- Rules hashes: ").Append(string.Join(", ", _all.Select(o => "`" + o.RulesHashHex + "`").Distinct())).Append('\n');
        sb.Append("- Configs: ").Append(string.Join("; ", _all.Select(o => o.Config.ToString()).Distinct())).Append('\n');
        sb.Append($"- Win shares are among decisive outcomes (draws counted separately); intervals are Wilson 95%; strata with fewer than {Stats.MinDecisive} decisive outcomes are marked insufficient.\n");
        if (_ctx.Excluded.Count > 0)
        {
            sb.Append($"- Excluded files: {_ctx.Excluded.Count}\n\n| File | Reason |\n|---|---|\n");
            foreach (IngestExclusion e in _ctx.Excluded) sb.Append("| ").Append(e.File).Append(" | ").Append(e.Reason).Append(" |\n");
        }
        if (_ctx.Errors.Count > 0)
        {
            sb.Append("\n## Errors\n\n");
            foreach (string e in _ctx.Errors.Distinct().Take(10)) sb.Append("- ").Append(e).Append('\n');
        }
        sb.Append('\n');
    }

    private void Length(StringBuilder sb)
    {
        sb.Append("## Match length and terminal reasons\n\n| Reason | Matches | Share |\n|---|---:|---:|\n");
        foreach (TerminalReason reason in Enum.GetValues<TerminalReason>())
        {
            int n = _all.Count(m => m.Result.Reason == reason);
            if (n == 0) continue;
            sb.Append($"| {reason} | {n} | {Stats.Pct((double)n / _all.Count)} |\n");
            _csv.Add(new[] { "terminal_reason", "all", reason.ToString(), Stats.I(_all.Count), Stats.I(n), "", "", Stats.F((double)n / _all.Count), "", "" });
        }
        int reached8 = _all.Count(m => m.Result.RoundsPlayed == RulesConstants.MaxRounds);
        var ci8 = Stats.Wilson(reached8, _all.Count);
        sb.Append($"\nReached round 8: **{Stats.Pct((double)reached8 / _all.Count)}** ({reached8}/{_all.Count}, Wilson {Stats.Interval(ci8)}). ");
        sb.Append($"Match draws: {_all.Count(m => m.Result.IsDraw)}.\n\n");
        _csv.Add(new[] { "round8_frequency", "all", "reached_round_8", Stats.I(_all.Count), Stats.I(reached8), "", "", Stats.F((double)reached8 / _all.Count), Stats.F(ci8.Lo), Stats.F(ci8.Hi) });
    }

    private void Pairings(StringBuilder sb)
    {
        sb.Append("## Policy pairings\n\nRow label's match result against the column label, both seats pooled (bots by difficulty; people as \"human\").\n\n").Append(TableHead);
        var labels = _all.SelectMany(o => new[] { o.A.Label, o.B.Label }).Distinct().OrderBy(l => l, StringComparer.Ordinal).ToList();
        foreach (string x in labels)
            foreach (string y in labels)
            {
                if (string.CompareOrdinal(x, y) >= 0) continue;
                var t = new Tally();
                foreach (var m in _all)
                {
                    if (m.A.Label == x && m.B.Label == y) t.Add(m.Outcome(PlayerSide.A));
                    else if (m.B.Label == x && m.A.Label == y) t.Add(m.Outcome(PlayerSide.B));
                }
                if (t.Total > 0) Row(sb, "policy_pairing", "all", x + " v " + y, t);
            }
        sb.Append("\nMirror pairings played: ").Append(string.Join(", ", _mirror.GroupBy(m => m.PairingLabel).Select(g => g.Key + " (" + g.Count() + ")"))).Append("\n\n");
    }

    private void FirstAttacker(StringBuilder sb)
    {
        sb.Append("## First-attacker effect\n\nMatch result of the player who attacked in round 1.\n\n").Append(TableHead);
        var all = new Tally();
        foreach (var m in _all) all.Add(m.Outcome(m.FirstAttacker));
        Row(sb, "first_attacker", "all", "All matches", all);
        var mirror = new Tally();
        foreach (var m in _mirror) mirror.Add(m.Outcome(m.FirstAttacker));
        Row(sb, "first_attacker", "mirror", "Mirror labels", mirror);
        _screen.Add(("first attacker", "mirror labels", mirror, 0.45, 0.55));
        foreach (var g in _mirror.GroupBy(m => m.PairingLabel).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var t = new Tally();
            foreach (var m in g) t.Add(m.Outcome(m.FirstAttacker));
            Row(sb, "first_attacker", g.Key, "First attacker, " + g.Key, t);
        }
        foreach (var g in _all.GroupBy(m => m.Config.Catalog))
        {
            var t = new Tally();
            foreach (var m in g) t.Add(m.Outcome(m.FirstAttacker));
            Row(sb, "first_attacker", "catalog", "Catalog " + g.Key, t);
        }
        sb.Append('\n');
    }

    private void Cohorts(StringBuilder sb)
    {
        sb.Append("## Unlock cohorts\n\n");
        sb.Append("Account-level bands (levels 2-16 introduce the 15 non-starter weapons). Full rooms loan all twenty, so a band describes familiarity, never an unequal catalogue. ");
        sb.Append("For bot runs the band is a simulation assumption: the bot equips only weapons its level has unlocked.\n\n");
        var known = _all.Where(o => o.A.AccountLevel.HasValue || o.B.AccountLevel.HasValue).ToList();
        if (known.Count == 0)
        {
            sb.Append("No account levels recorded (run the simulator with `--cohorts`, or record `account_level` in playtest files).\n\n");
            return;
        }
        sb.Append("Row band's match result against the column band (seats pooled; mirror labels only, so policy strength is held equal).\n\n| Row \\ Col |");
        string[] bands = UnlockCohorts.Bands;
        foreach (string b in bands) sb.Append(' ').Append(b).Append(" |");
        sb.Append("\n|---|").Append(string.Concat(Enumerable.Repeat("---:|", bands.Length))).Append('\n');
        var matrix = new Dictionary<(string, string), Tally>();
        var byBand = new Dictionary<string, Tally>();
        var nonStarterUse = new Dictionary<string, (long Used, long Volleys)>();
        foreach (var m in _mirror)
        {
            foreach (PlayerSide side in new[] { PlayerSide.A, PlayerSide.B })
            {
                string own = UnlockCohorts.Band(m.Seat(side).AccountLevel), opp = UnlockCohorts.Band(m.Seat(Rules.Land.Board.Opponent(side)).AccountLevel);
                Get(matrix, (own, opp)).Add(m.Outcome(side));
                if (own != opp) Get(byBand, own).Add(m.Outcome(side));
                var (used, volleys) = nonStarterUse.TryGetValue(own, out var u) ? u : (0L, 0L);
                foreach (var v in m.Volleys)
                {
                    int w = side == PlayerSide.A ? v.WeaponA : v.WeaponB;
                    if (!WeaponCatalog.IsRegularId(w)) continue;
                    volleys++;
                    if (!WeaponCatalog.Get(w).IsStarter) used++;
                }
                nonStarterUse[own] = (used, volleys);
            }
        }
        foreach (string row in bands)
        {
            sb.Append("| ").Append(row).Append(" |");
            foreach (string col in bands)
            {
                Tally t = matrix.TryGetValue((row, col), out Tally? x) ? x : new Tally();
                sb.Append(' ').Append(t.Total == 0 ? "-" : Stats.Pct(t.Rate) + " (" + t.Decisive + ")").Append(" |");
                if (t.Total > 0) Csv("cohort_matchup", "mirror", row + " v " + col, t);
            }
            sb.Append('\n');
        }
        sb.Append("\nResult against other bands, and the share of volleys fired with non-starter weapons:\n\n| Band | n vs other bands | W/L/D | Win share | Wilson 95% | Non-starter volley share |\n|---|---:|---:|---:|---|---:|\n");
        foreach (string band in bands)
        {
            Tally t = byBand.TryGetValue(band, out Tally? x) ? x : new Tally();
            var (used, volleys) = nonStarterUse.TryGetValue(band, out var u) ? u : (0L, 0L);
            if (t.Total == 0 && volleys == 0) continue;
            sb.Append($"| {band} | {t.Total} | {t.Wins}/{t.Losses}/{t.Draws} | {Stats.Pct(t.Rate)} | {Stats.Interval(t.Wilson())} | {Stats.Pct(volleys == 0 ? double.NaN : (double)used / volleys)} |\n");
            Csv("cohort_vs_other_bands", "mirror", band, t);
        }
        sb.Append('\n');
    }

    private void Elements(StringBuilder sb)
    {
        var matrix = new Tally[6, 6];
        for (int a = 0; a < 6; a++) for (int b = 0; b < 6; b++) matrix[a, b] = new Tally();
        foreach (var m in _mirror)
            foreach (var v in m.Volleys)
            {
                if (!WeaponCatalog.IsRegularId(v.WeaponA) || !WeaponCatalog.IsRegularId(v.WeaponB)) continue;
                int sA = Sign(v.NetA - v.NetB);
                int ea = (int)WeaponCatalog.Get(v.WeaponA).Element, eb = (int)WeaponCatalog.Get(v.WeaponB).Element;
                matrix[ea, eb].Add(sA);
                matrix[eb, ea].Add(-sA);
            }
        sb.Append("## Element matchups (volley win share of row vs column, mirror labels)\n\n| Row \\ Col |");
        for (int b = 1; b <= 5; b++) sb.Append(' ').Append((Element)b).Append(" |");
        sb.Append("\n|---|").Append(string.Concat(Enumerable.Repeat("---:|", 5))).Append('\n');
        for (int a = 1; a <= 5; a++)
        {
            sb.Append("| ").Append((Element)a).Append(" |");
            for (int b = 1; b <= 5; b++)
            {
                Tally t = matrix[a, b];
                sb.Append(' ').Append(Stats.Pct(t.Rate)).Append(" (").Append(t.Decisive).Append(") |");
                Csv("element_matchup", "mirror", (Element)a + " v " + (Element)b, t);
            }
            sb.Append('\n');
        }
        sb.Append("\nElement totals (volleys against other elements):\n\n").Append(TableHead);
        for (int a = 1; a <= 5; a++)
        {
            var t = new Tally();
            for (int b = 1; b <= 5; b++) if (a != b) t.Add(matrix[a, b]);
            Row(sb, "element_total", "mirror", ((Element)a).ToString(), t);
            _screen.Add(("element", ((Element)a).ToString(), t, ElementBandLo, ElementBandHi));
        }
        sb.Append('\n');
    }

    private void Weapons(StringBuilder sb)
    {
        var volley = new Dictionary<int, Tally>();
        var duel = new Dictionary<int, Tally>();
        var contained = new Dictionary<int, Tally>();
        var equippedVolleys = new Dictionary<int, long>();
        var usedVolleys = new Dictionary<int, long>();
        long seats = 0;
        var equippedSeats = new Dictionary<int, long>();
        foreach (var m in _mirror)
        {
            foreach (var v in m.Volleys)
            {
                int sA = Sign(v.NetA - v.NetB);
                if (WeaponCatalog.IsRegularId(v.WeaponA)) Get(volley, v.WeaponA).Add(sA);
                if (WeaponCatalog.IsRegularId(v.WeaponB)) Get(volley, v.WeaponB).Add(-sA);
            }
            foreach (var d in m.Duels)
            {
                int sA = d.Result == DuelResult.AWins ? 1 : d.Result == DuelResult.BWins ? -1 : 0;
                foreach (int w in d.WeaponsA) Get(duel, w).Add(sA);
                foreach (int w in d.WeaponsB) Get(duel, w).Add(-sA);
            }
            foreach (PlayerSide side in new[] { PlayerSide.A, PlayerSide.B })
            {
                seats++;
                var set = m.Loadout(side).ToHashSet();
                if (m.Reserve(side) != 0) set.Add(m.Reserve(side));
                int seatVolleys = m.Volleys.Count;
                foreach (int w in set)
                {
                    Get(contained, w).Add(m.Outcome(side));
                    equippedSeats[w] = equippedSeats.GetValueOrDefault(w) + 1;
                    equippedVolleys[w] = equippedVolleys.GetValueOrDefault(w) + seatVolleys;
                }
                foreach (var v in m.Volleys)
                {
                    int w = side == PlayerSide.A ? v.WeaponA : v.WeaponB;
                    if (WeaponCatalog.IsRegularId(w)) usedVolleys[w] = usedVolleys.GetValueOrDefault(w) + 1;
                }
            }
        }
        sb.Append("## Weapons: usage-conditioned versus loadout-contained (mirror labels)\n\n");
        sb.Append("- *Volley*: the user's net HP change in a volley where they fired the weapon, against the opponent's.\n");
        sb.Append("- *Duel (used)*: duels in which the player fired the weapon at least once.\n");
        sb.Append("- *Match (in loadout)*: matches whose loadout contained the weapon, used or not. This mixes the weapon with everything else in the loadout; a large gap from the usage-conditioned columns means the weapon's effect depends on when it is chosen.\n");
        sb.Append("- *Equip rate*: share of seats that equipped it; *use rate*: volleys fired with it per volley in which it was equipped.\n\n");
        sb.Append("| Weapon | Volleys | Volley share | Wilson 95% | Duels used | Duel share (used) | Wilson 95% | Matches in loadout | Match share (in loadout) | Wilson 95% | Equip rate | Use rate |\n");
        sb.Append("|---|---:|---:|---|---:|---:|---|---:|---:|---|---:|---:|\n");
        var catalog = _all.Select(o => o.Config.Catalog).Distinct().Any(c => c == CatalogPreset.Full) ? CatalogPreset.Full : CatalogPreset.Starter;
        foreach (WeaponDefinition w in WeaponCatalog.ForPreset(catalog))
        {
            Tally tv = Get(volley, w.Id), td = Get(duel, w.Id), tc = Get(contained, w.Id);
            long eq = equippedSeats.GetValueOrDefault(w.Id), eqV = equippedVolleys.GetValueOrDefault(w.Id), used = usedVolleys.GetValueOrDefault(w.Id);
            sb.Append($"| {w.Id} {w.Name} ({w.Element}) | {tv.Total} | {Stats.Pct(tv.Rate)} | {Stats.Interval(tv.Wilson())} | {td.Total} | {Stats.Pct(td.Rate)} | {Stats.Interval(td.Wilson())} | ");
            sb.Append($"{tc.Total} | {Stats.Pct(tc.Rate)} | {Stats.Interval(tc.Wilson())} | {Stats.Pct(seats == 0 ? double.NaN : (double)eq / seats)} | {Stats.Pct(eqV == 0 ? double.NaN : (double)used / eqV)} |\n");
            Csv("weapon_volley", "mirror", w.Id + " " + w.Name, tv);
            Csv("weapon_duel_used", "mirror", w.Id + " " + w.Name, td);
            Csv("weapon_match_in_loadout", "mirror", w.Id + " " + w.Name, tc);
            _screen.Add(("weapon (duel, used)", w.Id + " " + w.Name, td, WeaponBandLo, WeaponBandHi));
        }
        sb.Append('\n');
    }

    private void Terrain(StringBuilder sb)
    {
        sb.Append("## Terrain (defender's duel result, mirror labels)\n\n").Append(TableHead);
        foreach (TerrainType terrain in Enum.GetValues<TerrainType>())
        {
            var t = new Tally();
            foreach (var m in _mirror)
                foreach (var d in m.Duels.Where(d => d.Terrain == terrain))
                {
                    int sA = d.Result == DuelResult.AWins ? 1 : d.Result == DuelResult.BWins ? -1 : 0;
                    t.Add(d.Defender == PlayerSide.A ? sA : -sA);
                }
            if (t.Total > 0)
            {
                Row(sb, "terrain_defender", "mirror", terrain.ToString(), t);
                _screen.Add(("terrain defender", terrain.ToString(), t, 0.45, 0.55));
            }
        }
        sb.Append('\n');
    }

    private void Comebacks(StringBuilder sb)
    {
        sb.Append("## Comeback behaviour (mirror labels)\n\n");
        sb.Append("Final match result of the player who trailed in cells after a checkpoint round, for matches that continued past it.\n\n");
        sb.Append("| Checkpoint | Trailing by | n | W/L/D | Win share | Wilson 95% |\n|---|---|---:|---:|---:|---|\n");
        var bands = new (string Name, double Lo, double Hi)[] { ("< 35% of board", 0, 0.35), ("35-45%", 0.35, 0.45), ("45-50%", 0.45, 0.5) };
        foreach (int round in new[] { 2, 4, 6 })
        {
            foreach (var band in bands)
            {
                var t = new Tally();
                foreach (var m in _mirror)
                {
                    RoundCells? c = m.CheckpointAfter(round);
                    if (c == null || c.Value.CellsA == c.Value.CellsB) continue;
                    PlayerSide trailing = c.Value.CellsA < c.Value.CellsB ? PlayerSide.A : PlayerSide.B;
                    double share = (double)Math.Min(c.Value.CellsA, c.Value.CellsB) / RulesConstants.ActiveCells;
                    if (share >= band.Lo && share < band.Hi) t.Add(m.Outcome(trailing));
                }
                var ci = t.Wilson();
                sb.Append($"| After round {round} | {band.Name} | {t.Total} | {t.Wins}/{t.Losses}/{t.Draws} | {Stats.Pct(t.Rate)} | {Stats.Interval(ci)} |\n");
                Csv("comeback_trailing", "after_round_" + round, band.Name, t);
            }
        }
        // Lead changes: how often the cell leader after a round differs from the previous leader.
        var changes = new Dictionary<int, int>();
        foreach (var m in _mirror)
        {
            int n = 0;
            int prev = 0;
            foreach (RoundCells c in m.Cells)
            {
                int leader = Math.Sign(c.CellsA - c.CellsB);
                if (leader != 0 && prev != 0 && leader != prev) n++;
                if (leader != 0) prev = leader;
            }
            changes[n] = changes.GetValueOrDefault(n) + 1;
        }
        sb.Append("\n| Lead changes per match | Matches |\n|---:|---:|\n");
        foreach (var kv in changes.OrderBy(k => k.Key)) sb.Append($"| {kv.Key} | {kv.Value} |\n");
        sb.Append('\n');
    }

    /// <summary>
    /// Every screened comparison (including those consistent with their band), in report order,
    /// with the same status rules as the "Screening flags" section. Used by bundle comparisons.
    /// </summary>
    public IReadOnlyList<ScreenedComparison> Screened()
    {
        if (_screen.Count == 0 && _all.Count > 0) Markdown();
        double zFamily = Stats.BonferroniZ(_screen.Count);
        return _screen.Select(s => Screen(s, zFamily)).ToList();
    }

    private static ScreenedComparison Screen((string Kind, string Key, Tally T, double Lo, double Hi) s, double zFamily)
    {
        var ci = s.T.Wilson();
        var fam = s.T.Wilson(zFamily);
        string status = s.T.Decisive < Stats.MinDecisive ? "insufficient"
            : fam.Hi < s.Lo || fam.Lo > s.Hi ? "FLAG"
            : ci.Hi < s.Lo || ci.Lo > s.Hi ? "watch" : "ok";
        return new ScreenedComparison(s.Kind, s.Key, s.T, s.Lo, s.Hi, ci, fam, status);
    }

    private void Screening(StringBuilder sb)
    {
        int k = _screen.Count;
        double zFamily = Stats.BonferroniZ(k);
        sb.Append("## Screening flags and uncertainty\n\n");
        sb.Append($"{k} comparisons are screened against provisional bands. **Flag**: the Bonferroni family interval (z = {zFamily:F2}, overall alpha 0.05) lies entirely outside the band. ");
        sb.Append("**Watch**: only the per-comparison 95% interval lies outside. **Insufficient**: fewer than ")
          .Append(Stats.MinDecisive).Append(" decisive outcomes. Everything else is consistent with the band at this sample size (which is not proof of balance).\n\n");
        sb.Append("| Kind | Key | n decisive | Win share | Band | Wilson 95% | Family interval | Status |\n|---|---|---:|---:|---|---|---|---|\n");
        foreach (var s in _screen)
        {
            ScreenedComparison screened = Screen(s, zFamily);
            var ci = screened.Wilson95;
            var fam = screened.Family;
            string status = screened.Status;
            if (status == "ok") continue;
            sb.Append($"| {s.Kind} | {s.Key} | {s.T.Decisive} | {Stats.Pct(s.T.Rate)} | {Stats.Pct(s.Lo)}-{Stats.Pct(s.Hi)} | {Stats.Interval(ci)} | {Stats.Interval(fam)} | {status} |\n");
            _csv.Add(new[] { "screening_" + status.ToLowerInvariant(), s.Kind, s.Key, Stats.I(s.T.Total), Stats.I(s.T.Wins), Stats.I(s.T.Losses), Stats.I(s.T.Draws), Stats.F(s.T.Rate), Stats.F(fam.Lo), Stats.F(fam.Hi) });
        }
        sb.Append('\n');
    }

    private static void Notes(StringBuilder sb)
    {
        sb.Append("## Reading notes\n\n");
        sb.Append("- Bands (weapon 45-55%, element 47-53%) are screening hypotheses from the plan, not acceptance criteria; a weapon need not match an aggregate win rate.\n");
        sb.Append("- Compare like-for-like strata (mirror labels, same catalogue). Bot weapon results mix the weapon's geometry with how each policy picks and aims it.\n");
        sb.Append("- Usage-conditioned and loadout-contained results answer different questions; neither is causal.\n");
        sb.Append("- Human tests still decide clarity and enjoyment. Re-run: `dotnet run -c Release --project tools/AstraKingdoms.Sim -- --matches 10000 --cohorts` (bots) or `-- --ingest <folder>` (playtest records).\n");
    }
}
