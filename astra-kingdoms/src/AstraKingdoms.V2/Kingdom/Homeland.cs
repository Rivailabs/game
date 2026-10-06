using System;
using System.Collections.Generic;
using System.Linq;
using AstraKingdoms.Meta.Common;

namespace AstraKingdoms.V2.Kingdom
{
    /// <summary>
    /// Homeland constants (plan: "Persistent kingdom and protected homeland", PROPOSED baseline).
    /// There are deliberately no construction timers, repair costs, decay or paid accelerators:
    /// nothing in this namespace takes a duration, and placing a decoration takes effect at once.
    /// All plots are protected: no API lets another account attack, occupy or confiscate them.
    /// </summary>
    public sealed class HomelandRules
    {
        public const string CurrentVersion = "AK-HOME-1";

        public string Version { get; }
        public int PlotCount { get; }
        public int InitialPlots { get; }

        public HomelandRules(string version, int plotCount, int initialPlots)
        {
            if (plotCount < 1 || initialPlots < 1 || initialPlots > plotCount) throw new ArgumentException("invalid plot counts");
            Version = version;
            PlotCount = plotCount;
            InitialPlots = initialPlots;
        }

        /// <summary>PROPOSED: twelve permanent plots, three available from the start.</summary>
        public static readonly HomelandRules Default = new HomelandRules(CurrentVersion, 12, 3);
    }

    /// <summary>Play statistics that published milestones can use. All come from authoritative records.</summary>
    public enum PlayStat : byte
    {
        /// <summary>Eligible normally completed matches (Meta reward-ledger match entries, corrections excluded).</summary>
        MatchesCompleted = 0,
        /// <summary>Distinct weapons the player has fired in eligible matches.</summary>
        DistinctWeaponsUsed = 1,
        /// <summary>Ranked seasons in which the player completed placement.</summary>
        SeasonsPlaced = 2,
    }

    /// <summary>A published play milestone. Reaching it is a permanent, non-spendable achievement.</summary>
    public sealed class PlayMilestone
    {
        public string Id { get; }
        public PlayStat Stat { get; }
        public int Threshold { get; }
        /// <summary>Zero-based plot this milestone opens, or -1 for an achievement without a plot.</summary>
        public int OpensPlot { get; }

        public PlayMilestone(string id, PlayStat stat, int threshold, int opensPlot)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Stat = stat;
            Threshold = threshold;
            OpensPlot = opensPlot;
        }

        public string NameKey => "milestone." + Id;
    }

    /// <summary>The published milestone table (versioned; a new version may add milestones but never raises an old threshold).</summary>
    public sealed class MilestoneTable
    {
        public string Version { get; }
        public IReadOnlyList<PlayMilestone> Milestones { get; }

        public MilestoneTable(string version, IEnumerable<PlayMilestone> milestones)
        {
            Version = version;
            Milestones = milestones.ToArray();
        }

        public PlayMilestone Get(string id) => Milestones.FirstOrDefault(m => m.Id == id);

        /// <summary>Checks the table opens every non-initial plot exactly once and ids are unique.</summary>
        public IReadOnlyList<string> Validate(HomelandRules rules)
        {
            var errors = new List<string>();
            if (Milestones.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != Milestones.Count) errors.Add("duplicate milestone id");
            for (int plot = rules.InitialPlots; plot < rules.PlotCount; plot++)
            {
                int n = Milestones.Count(m => m.OpensPlot == plot);
                if (n != 1) errors.Add("plot " + plot + " is opened by " + n + " milestones (expected 1)");
            }
            foreach (PlayMilestone m in Milestones)
            {
                if (m.OpensPlot >= 0 && m.OpensPlot < rules.InitialPlots) errors.Add(m.Id + ": plot " + m.OpensPlot + " is already open initially");
                if (m.OpensPlot >= rules.PlotCount) errors.Add(m.Id + ": no such plot");
                if (m.Threshold <= 0) errors.Add(m.Id + ": threshold must be positive");
            }
            return errors;
        }

        /// <summary>
        /// PROPOSED published milestones: completed matches open seven plots, trying distinct weapons
        /// opens two. Every one is reachable through ordinary play of any mode; none needs ranked play,
        /// a purchase, an ad or consecutive daily attendance.
        /// </summary>
        public static readonly MilestoneTable Default = new MilestoneTable("AK-MILESTONES-1", new[]
        {
            new PlayMilestone("matches-3", PlayStat.MatchesCompleted, 3, 3),
            new PlayMilestone("matches-10", PlayStat.MatchesCompleted, 10, 4),
            new PlayMilestone("weapons-5", PlayStat.DistinctWeaponsUsed, 5, 5),
            new PlayMilestone("matches-25", PlayStat.MatchesCompleted, 25, 6),
            new PlayMilestone("matches-50", PlayStat.MatchesCompleted, 50, 7),
            new PlayMilestone("weapons-12", PlayStat.DistinctWeaponsUsed, 12, 8),
            new PlayMilestone("matches-100", PlayStat.MatchesCompleted, 100, 9),
            new PlayMilestone("matches-200", PlayStat.MatchesCompleted, 200, 10),
            new PlayMilestone("matches-350", PlayStat.MatchesCompleted, 350, 11),
            new PlayMilestone("placed-1", PlayStat.SeasonsPlaced, 1, -1),
        });
    }

    /// <summary>A player's play statistics, folded from authoritative records.</summary>
    public sealed class PlayStats
    {
        public int MatchesCompleted { get; }
        public int DistinctWeaponsUsed { get; }
        public int SeasonsPlaced { get; }

        public PlayStats(int matchesCompleted, int distinctWeaponsUsed, int seasonsPlaced)
        {
            MatchesCompleted = matchesCompleted;
            DistinctWeaponsUsed = distinctWeaponsUsed;
            SeasonsPlaced = seasonsPlaced;
        }

        public int Value(PlayStat stat)
        {
            switch (stat)
            {
                case PlayStat.MatchesCompleted: return MatchesCompleted;
                case PlayStat.DistinctWeaponsUsed: return DistinctWeaponsUsed;
                default: return SeasonsPlaced;
            }
        }

        /// <summary>
        /// Reads Meta's earned reward ledger: one Match entry exists per eligible match result (the
        /// ledger's idempotency already prevents double counting); entries reversed by a correction
        /// are excluded.
        /// </summary>
        public static PlayStats FromLedger(IRewardLedgerStore ledger, string playerId, int seasonsPlaced = 0)
        {
            IReadOnlyList<RewardLedgerEntry> entries = ledger.Entries(playerId);
            var reversed = new HashSet<string>(entries.Where(e => e.Source == LedgerSource.Correction && e.CorrectsKey != null).Select(e => e.CorrectsKey),
                StringComparer.Ordinal);
            int matches = entries.Count(e => e.Source == LedgerSource.Match && !reversed.Contains(e.IdempotencyKey));
            PlayerTotals totals = ledger.Totals(playerId);
            return new PlayStats(matches, totals.WeaponUses.Count, seasonsPlaced);
        }
    }

    /// <summary>One decoration placed in a plot slot.</summary>
    public sealed class Placement : IEquatable<Placement>
    {
        public int Plot { get; }
        public PlotSlot Slot { get; }
        public string DecorationId { get; }
        /// <summary>Quarter turns, 0-3.</summary>
        public int Rotation { get; }

        public Placement(int plot, PlotSlot slot, string decorationId, int rotation = 0)
        {
            Plot = plot;
            Slot = slot;
            DecorationId = decorationId ?? throw new ArgumentNullException(nameof(decorationId));
            Rotation = rotation;
        }

        public bool Equals(Placement other) =>
            other != null && Plot == other.Plot && Slot == other.Slot && DecorationId == other.DecorationId && Rotation == other.Rotation;

        public override bool Equals(object obj) => Equals(obj as Placement);
        public override int GetHashCode() => (Plot * 397) ^ ((int)Slot * 31) ^ DecorationId.GetHashCode() ^ Rotation;
        public override string ToString() => Plot + "/" + Slot + "=" + DecorationId + "@" + Rotation;
    }

    /// <summary>
    /// A versioned homeland layout. <see cref="Revision"/> increases on every accepted save and is the
    /// optimistic-concurrency token; <see cref="CatalogVersion"/> records the decoration catalogue the
    /// layout was written against (newer ids resolve safely on older clients).
    /// </summary>
    public sealed class HomelandLayout
    {
        public const int SchemaVersion = 2;

        public long Revision { get; }
        public int CatalogVersion { get; }
        public IReadOnlyList<Placement> Placements { get; }

        public HomelandLayout(long revision, int catalogVersion, IEnumerable<Placement> placements)
        {
            Revision = revision;
            CatalogVersion = catalogVersion;
            Placements = (placements ?? Array.Empty<Placement>()).OrderBy(p => p.Plot).ThenBy(p => p.Slot).ToArray();
        }

        public static HomelandLayout Empty(int catalogVersion) => new HomelandLayout(0, catalogVersion, null);

        public Placement At(int plot, PlotSlot slot) => Placements.FirstOrDefault(p => p.Plot == plot && p.Slot == slot);
    }

    /// <summary>One plot as shown: open or locked, and what each slot displays after retirement resolution.</summary>
    public sealed class PlotView
    {
        public int Index { get; }
        public bool Open { get; }
        /// <summary>The milestone that opens a locked plot (null when open).</summary>
        public PlayMilestone OpensWith { get; }
        public DecorationItem Main { get; }
        public DecorationItem Accent { get; }
        public int MainRotation { get; }
        public int AccentRotation { get; }
        /// <summary>True when a stored placement was replaced because its asset was retired or unknown.</summary>
        public bool UsedFallback { get; }
        /// <summary>Always true: every homeland plot is protected (plan). Exposed so screens can say so.</summary>
        public bool Protected => true;

        public PlotView(int index, bool open, PlayMilestone opensWith, DecorationItem main, DecorationItem accent, int mainRotation, int accentRotation, bool usedFallback)
        {
            Index = index;
            Open = open;
            OpensWith = opensWith;
            Main = main;
            Accent = accent;
            MainRotation = mainRotation;
            AccentRotation = accentRotation;
            UsedFallback = usedFallback;
        }
    }
}
