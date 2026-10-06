using System;
using System.Collections.Generic;
using AstraKingdoms.World.Challenges;

namespace AstraKingdoms.World.Season
{
    /// <summary>What one season boundary did across every shard.</summary>
    public sealed class SeasonBoundaryReport
    {
        public string ClosedSeasonId { get; internal set; }
        public WorldSeason NextSeason { get; internal set; }
        public IReadOnlyList<SeasonSettlement> Settlements { get; internal set; }
        public IReadOnlyList<MigrationResult> Migrations { get; internal set; }
    }

    /// <summary>
    /// Runs the season boundary in the plan's order: stop challenges and settle or cancel every
    /// reservation on every shard, snapshot final ownership and grant rewards once (per shard), then
    /// — only with every shard reconciled — apply queued migrations and rebuild each shard's borders
    /// for the next season from the directory's (post-migration) membership.
    /// </summary>
    public static class SeasonOperator
    {
        public static SeasonBoundaryReport CloseAndRebuild(ShardDirectory directory, IReadOnlyList<WorldShard> shards, long nowMs,
            Func<Challenge, KeyValuePair<string, EncounterOutcome>?> settle = null)
        {
            if (directory == null) throw new ArgumentNullException(nameof(directory));
            if (shards == null || shards.Count == 0) throw new ArgumentException("No shards.", nameof(shards));
            WorldSeason season = shards[0].Season;
            foreach (WorldShard s in shards)
                if (s.Season.SeasonId != season.SeasonId) throw new InvalidOperationException("Shards are in different seasons.");

            var settlements = new List<SeasonSettlement>();
            foreach (WorldShard s in shards) settlements.Add(s.Close(nowMs, settle));

            SeasonBoundary boundary = directory.BoundaryFrom(season.SeasonId, settlements);
            if (boundary == null) throw new InvalidOperationException("Not every shard reconciled; migrations and rebuild are held.");
            IReadOnlyList<MigrationResult> migrations = directory.ApplyMigrations(boundary);

            WorldSeason next = season.Next();
            foreach (WorldShard s in shards)
            {
                string problem = s.RebuildForNextSeason(next, directory.Members(s.Info.ShardId));
                if (problem != null) throw new InvalidOperationException("Rebuild of " + s.Info.ShardId + " failed: " + problem);
            }
            return new SeasonBoundaryReport { ClosedSeasonId = season.SeasonId, NextSeason = next, Settlements = settlements, Migrations = migrations };
        }
    }
}
