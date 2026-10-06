using System;
using System.Collections.Generic;

namespace AstraKingdoms.Modes.FourPlayer
{
    /// <summary>One duel pair in a wave. <see cref="First"/> sits in duel seat A, <see cref="Second"/> in seat B.</summary>
    public readonly struct WavePair : IEquatable<WavePair>
    {
        public readonly Kingdom First;
        public readonly Kingdom Second;

        public WavePair(Kingdom first, Kingdom second)
        {
            if (first == second) throw new ArgumentException("A pair needs two different kingdoms.");
            First = first;
            Second = second;
        }

        public bool Contains(Kingdom k) => First == k || Second == k;

        public Kingdom OpponentOf(Kingdom k)
        {
            if (k == First) return Second;
            if (k == Second) return First;
            throw new ArgumentException(k + " is not in pair " + this + ".");
        }

        public bool Equals(WavePair o) => First == o.First && Second == o.Second;
        public override bool Equals(object obj) => obj is WavePair p && Equals(p);
        public override int GetHashCode() => ((int)First << 4) | (int)Second;
        public override string ToString() => First + "-" + Second;
    }

    /// <summary>The pairs and bye of one wave.</summary>
    public sealed class WavePlan
    {
        public int Wave { get; }
        public IReadOnlyList<WavePair> Pairs { get; }

        /// <summary>The survivor sitting out this wave (three survivors only), else null.</summary>
        public Kingdom? Bye { get; }

        public WavePlan(int wave, IReadOnlyList<WavePair> pairs, Kingdom? bye)
        {
            Wave = wave;
            Pairs = pairs;
            Bye = bye;
        }

        public override string ToString() => "wave " + Wave + ": " + string.Join("/", Pairs) + (Bye.HasValue ? " bye " + Bye.Value : string.Empty);
    }

    /// <summary>A survivor's pairing history, the input to the three-survivor rule.</summary>
    public readonly struct SurvivorHistory
    {
        public readonly Kingdom Kingdom;
        /// <summary>Duels completed so far (a draw completes participation, so it counts).</summary>
        public readonly int CompletedDuels;
        /// <summary>True when this survivor had the bye in the immediately preceding wave.</summary>
        public readonly bool HadByeLastWave;

        public SurvivorHistory(Kingdom kingdom, int completedDuels, bool hadByeLastWave)
        {
            Kingdom = kingdom;
            CompletedDuels = completedDuels;
            HadByeLastWave = hadByeLastWave;
        }
    }

    /// <summary>
    /// Candidate pairing rules (plan table rows "Pairing", "Three survivors", "Two survivors"):
    /// <list type="bullet">
    /// <item>Four survivors: waves cycle A-B/C-D, A-C/B-D, A-D/B-C (wave 4 repeats wave 1, ...).</item>
    /// <item>Three survivors: the two with the fewest completed duels play; equal counts are broken by
    /// a fixed order that rotates every wave (wave w ranks label k at (k − (w − 1)) mod 4). The
    /// remaining survivor gets the bye, except that a survivor who had the bye last wave is passed
    /// over whenever another survivor can take it, so consecutive byes are avoided when possible.</item>
    /// <item>Two survivors: they duel every remaining wave.</item>
    /// </list>
    /// </summary>
    public static class WavePairing
    {
        private static readonly WavePair[][] FourSchedule =
        {
            new[] { new WavePair(Kingdom.A, Kingdom.B), new WavePair(Kingdom.C, Kingdom.D) },
            new[] { new WavePair(Kingdom.A, Kingdom.C), new WavePair(Kingdom.B, Kingdom.D) },
            new[] { new WavePair(Kingdom.A, Kingdom.D), new WavePair(Kingdom.B, Kingdom.C) },
        };

        /// <summary>The rotating tiebreak rank of a label in a wave (0 = first).</summary>
        public static int RotatingRank(Kingdom k, int wave) => (((int)k - (wave - 1)) % 4 + 4) % 4;

        public static WavePlan Plan(int wave, IReadOnlyList<SurvivorHistory> survivors)
        {
            if (wave < 1 || wave > FourPlayerRules.MaxWaves) throw new ArgumentOutOfRangeException(nameof(wave));
            if (survivors == null) throw new ArgumentNullException(nameof(survivors));
            switch (survivors.Count)
            {
                case 4:
                    return new WavePlan(wave, FourSchedule[(wave - 1) % 3], null);
                case 3:
                    return PlanThree(wave, survivors);
                case 2:
                {
                    Kingdom a = survivors[0].Kingdom, b = survivors[1].Kingdom;
                    return new WavePlan(wave, new[] { a < b ? new WavePair(a, b) : new WavePair(b, a) }, null);
                }
                default:
                    throw new ArgumentException("Pairing needs two to four survivors.", nameof(survivors));
            }
        }

        private static WavePlan PlanThree(int wave, IReadOnlyList<SurvivorHistory> survivors)
        {
            var order = new List<SurvivorHistory>(survivors);
            order.Sort((x, y) =>
            {
                int c = x.CompletedDuels.CompareTo(y.CompletedDuels);
                return c != 0 ? c : RotatingRank(x.Kingdom, wave).CompareTo(RotatingRank(y.Kingdom, wave));
            });

            // The bye goes to the lowest-priority survivor who did not sit out the previous wave.
            int byeIndex = order.Count - 1;
            for (int i = order.Count - 1; i >= 0; i--)
            {
                if (!order[i].HadByeLastWave)
                {
                    byeIndex = i;
                    break;
                }
            }
            Kingdom bye = order[byeIndex].Kingdom;
            order.RemoveAt(byeIndex);
            Kingdom first = order[0].Kingdom, second = order[1].Kingdom;
            WavePair pair = first < second ? new WavePair(first, second) : new WavePair(second, first);
            return new WavePlan(wave, new[] { pair }, bye);
        }
    }
}
