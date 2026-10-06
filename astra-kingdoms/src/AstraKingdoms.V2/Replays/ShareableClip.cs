using System;
using System.Collections.Generic;
using System.Linq;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;
using AstraKingdoms.V2.Common;

namespace AstraKingdoms.V2.Replays
{
    /// <summary>
    /// Whether the authority has published a match record for player-facing use. Internal V1
    /// diagnostic records (30-day support retention) are a separate purpose and never exportable.
    /// </summary>
    public enum RecordPublication : byte
    {
        /// <summary>Diagnostic or in-progress record: not exportable.</summary>
        Unpublished = 0,
        /// <summary>Terminal and released to the players of the match.</summary>
        PublishedToParticipants = 1,
    }

    public enum ClipCompatibility : byte
    {
        /// <summary>Current rules family: full animated re-render.</summary>
        FullRender = 0,
        /// <summary>An older or unknown rules version: export a static result card instead of a re-render.</summary>
        SummaryCardOnly = 1,
        /// <summary>Not a terminal record or an unreadable format.</summary>
        Unsupported = 2,
    }

    public static class ReplayCompatibility
    {
        /// <summary>
        /// Records of the current rules family (AK-TR-1 and its balance bundles "AK-TR-1.bN") replay
        /// exactly and re-render fully; any other version gets the summary card, because re-rendering
        /// it with today's engine could show a different fight.
        /// </summary>
        public static ClipCompatibility Check(MatchRecord record)
        {
            if (record == null || record.Result == null || record.Rounds == null) return ClipCompatibility.Unsupported;
            string v = record.RulesVersion ?? string.Empty;
            if (v == RulesConstants.RulesVersion || v.StartsWith(RulesConstants.RulesVersion + ".", StringComparison.Ordinal)) return ClipCompatibility.FullRender;
            return ClipCompatibility.SummaryCardOnly;
        }
    }

    public sealed class ClipVolley
    {
        public int Volley { get; }
        public int WeaponA { get; }
        public int WeaponB { get; }
        public int HpA { get; }
        public int HpB { get; }

        public ClipVolley(int volley, int weaponA, int weaponB, int hpA, int hpB)
        {
            Volley = volley;
            WeaponA = weaponA;
            WeaponB = weaponB;
            HpA = hpA;
            HpB = hpB;
        }
    }

    public sealed class ClipRound
    {
        public int Round { get; }
        public PlayerSide Attacker { get; }
        public TerrainType Terrain { get; }
        public IReadOnlyList<ClipVolley> Volleys { get; }
        public int CellsTransferred { get; }
        public int CellsA { get; }
        public int CellsB { get; }

        public ClipRound(int round, PlayerSide attacker, TerrainType terrain, IReadOnlyList<ClipVolley> volleys, int cellsTransferred, int cellsA, int cellsB)
        {
            Round = round;
            Attacker = attacker;
            Terrain = terrain;
            Volleys = volleys;
            CellsTransferred = cellsTransferred;
            CellsA = cellsA;
            CellsB = cellsB;
        }
    }

    /// <summary>
    /// The privacy-filtered content of a shared clip: what the renderer draws and the only data that
    /// can end up in the video. It carries display aliases, the public course of the highlighted rounds
    /// and the final result. It never carries account ids, the server match id, the seed or its
    /// commitment, the command log, state/log hashes, stream counters, chat or diagnostics.
    /// </summary>
    public sealed class ShareClipManifest
    {
        public const string Format = "AK-CLIP/1";

        public string ClipId { get; }
        public string RulesVersion { get; }
        public ClipCompatibility Compatibility { get; }
        public string AliasA { get; }
        public string AliasB { get; }
        public int WindowStartMs { get; }
        public int WindowEndMs { get; }
        public IReadOnlyList<ClipRound> Rounds { get; }
        /// <summary>Winning side, or null for a draw.</summary>
        public PlayerSide? Winner { get; }
        public TerminalReason Reason { get; }
        public int FinalCellsA { get; }
        public int FinalCellsB { get; }

        public ShareClipManifest(string clipId, string rulesVersion, ClipCompatibility compatibility, string aliasA, string aliasB, int startMs, int endMs,
            IReadOnlyList<ClipRound> rounds, PlayerSide? winner, TerminalReason reason, int cellsA, int cellsB)
        {
            ClipId = clipId;
            RulesVersion = rulesVersion;
            Compatibility = compatibility;
            AliasA = aliasA;
            AliasB = aliasB;
            WindowStartMs = startMs;
            WindowEndMs = endMs;
            Rounds = rounds;
            Winner = winner;
            Reason = reason;
            FinalCellsA = cellsA;
            FinalCellsB = cellsB;
        }

        public string ToJson()
        {
            var root = Rules.Replay.JsonNode.Object()
                .Add("format", Format).Add("clip", ClipId).Add("rules", RulesVersion).Add("compat", Compatibility.ToString())
                .Add("aliasA", AliasA).Add("aliasB", AliasB).Add("startMs", WindowStartMs).Add("endMs", WindowEndMs)
                .Add("winner", Winner.HasValue ? Winner.Value.ToString() : "draw").Add("reason", Reason.ToString())
                .Add("cellsA", FinalCellsA).Add("cellsB", FinalCellsB);
            var rounds = Rules.Replay.JsonNode.Array();
            foreach (ClipRound r in Rounds)
            {
                var volleys = Rules.Replay.JsonNode.Array();
                foreach (ClipVolley v in r.Volleys)
                    volleys.Push(Rules.Replay.JsonNode.Object().Add("v", v.Volley).Add("wA", v.WeaponA).Add("wB", v.WeaponB).Add("hpA", v.HpA).Add("hpB", v.HpB));
                rounds.Push(Rules.Replay.JsonNode.Object().Add("round", r.Round).Add("attacker", r.Attacker.ToString()).Add("terrain", r.Terrain.ToString())
                    .Add("volleys", volleys).Add("cells", r.CellsTransferred).Add("cellsA", r.CellsA).Add("cellsB", r.CellsB));
            }
            root.Add("rounds", rounds);
            return root.ToCanonicalString();
        }
    }

    public enum ClipBuildStatus : byte
    {
        Ok = 0,
        NotTerminal = 1,
        NotPublished = 2,
        Unsupported = 3,
    }

    /// <summary>Builds <see cref="ShareClipManifest"/>s: the only path from a match record to a shareable clip.</summary>
    public static class ReplayPrivacyFilter
    {
        /// <summary>Default aliases when the player has not chosen display names for the clip.</summary>
        public const string DefaultAliasA = "Archer A";
        public const string DefaultAliasB = "Archer B";

        /// <param name="exportNonce">Random per export, so two exports of one match have unlinkable clip ids.</param>
        public static ClipBuildStatus Build(MatchRecord record, RecordPublication publication, string aliasA, string aliasB, HighlightWindow window,
            string exportNonce, out ShareClipManifest manifest)
        {
            manifest = null;
            if (record == null || record.Result == null) return ClipBuildStatus.NotTerminal;
            if (publication != RecordPublication.PublishedToParticipants) return ClipBuildStatus.NotPublished;
            ClipCompatibility compat = ReplayCompatibility.Check(record);
            if (compat == ClipCompatibility.Unsupported) return ClipBuildStatus.Unsupported;
            var roundsInWindow = new HashSet<int>(window.Rounds);
            IReadOnlyList<ClipRound> rounds = compat == ClipCompatibility.SummaryCardOnly
                ? Array.Empty<ClipRound>()
                : record.Rounds.Where(r => roundsInWindow.Contains(r.Round)).OrderBy(r => r.Round)
                    .Select(r => new ClipRound(r.Round, r.Attacker, r.Terrain,
                        r.Volleys.OrderBy(v => v.Volley).Select(v => new ClipVolley(v.Volley, v.WeaponA, v.WeaponB, v.HpA, v.HpB)).ToArray(),
                        r.CellsTransferred, r.CellsA, r.CellsB))
                    .ToArray();
            string clipId = StableHash.Sha256Hex("clip|" + (exportNonce ?? string.Empty) + "|" + record.MatchId).Substring(0, 16);
            manifest = new ShareClipManifest(clipId, record.RulesVersion, compat, SafeAlias(aliasA, DefaultAliasA), SafeAlias(aliasB, DefaultAliasB),
                window.StartMs, window.EndMs, rounds, record.Result.Winner, record.Result.Reason, record.Result.CellsA, record.Result.CellsB);
            return ClipBuildStatus.Ok;
        }

        /// <summary>Aliases are short display names; anything else (empty, overlong, control characters) falls back to the default.</summary>
        public static string SafeAlias(string alias, string fallback)
        {
            if (string.IsNullOrWhiteSpace(alias)) return fallback;
            string t = alias.Trim();
            if (t.Length > 16 || t.Any(char.IsControl) || t.Contains("@")) return fallback;
            return t;
        }
    }
}
