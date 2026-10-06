using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Rules.Combat
{
    /// <summary>
    /// Duel lifecycle (ticket 16): up to three simultaneous volleys with persistent health. A sole
    /// survivor after a volley wins; both reaching zero in one update is a draw; otherwise the higher
    /// HP after volley three wins and equal HP draws. This is not best-of-three scoring.
    /// </summary>
    public sealed class Duel
    {
        private readonly List<VolleyResult> _volleys = new List<VolleyResult>();

        public DuelState State { get; private set; }

        public Duel(DuelState initial)
        {
            State = initial ?? throw new ArgumentNullException(nameof(initial));
        }

        /// <summary>Starts a fresh duel (see <see cref="DuelState.Start"/>).</summary>
        public static Duel Start(int roundIndex, TerrainType terrain, PlayerSide defender, Loadout loadoutA, Loadout loadoutB,
            bool brahmastraEnabled = false, bool brahmastraAvailableA = true, bool brahmastraAvailableB = true) =>
            new Duel(DuelState.Start(roundIndex, terrain, defender, loadoutA, loadoutB, brahmastraEnabled, brahmastraAvailableA, brahmastraAvailableB));

        public IReadOnlyList<VolleyResult> Volleys => _volleys;
        public int CurrentVolley => State.VolleyIndex;
        public bool IsOver => State.IsOver;
        public DuelResult Result => State.Result;

        /// <summary>The winner, or null while in progress or after a draw.</summary>
        public PlayerSide? Winner =>
            Result == DuelResult.AWins ? PlayerSide.A : Result == DuelResult.BWins ? PlayerSide.B : (PlayerSide?)null;

        public PlayerSide? Loser => Winner.HasValue ? CombatGeometry.Opponent(Winner.Value) : (PlayerSide?)null;

        public int HpUnits(PlayerSide side) => State[side].HpUnits;

        /// <summary>
        /// D = winner's final HP units minus loser's final HP units; 0 for a draw. Used by the land
        /// allowance formula. Throws while the duel is still in progress.
        /// </summary>
        public int HpDifferenceUnits
        {
            get
            {
                if (!IsOver) throw new InvalidOperationException("The duel is still in progress.");
                if (!Winner.HasValue) return 0;
                return HpUnits(Winner.Value) - HpUnits(Loser.Value);
            }
        }

        /// <summary>Validates a LockInput for <paramref name="side"/> against the open volley.</summary>
        public void ValidateLock(PlayerSide side, LockInput input) => InputValidator.ValidateLock(State, side, input);

        /// <summary>
        /// Resolves the open volley from both locks. A null lock is a deadline timeout and becomes the
        /// server-created Pass. Each non-null lock is validated first.
        /// </summary>
        public VolleyResult ResolveLocks(LockInput lockA, LockInput lockB)
        {
            if (lockA != null) ValidateLock(PlayerSide.A, lockA);
            if (lockB != null) ValidateLock(PlayerSide.B, lockB);
            return Resolve(lockA?.Choice ?? VolleyInput.Pass(), lockB?.Choice ?? VolleyInput.Pass());
        }

        /// <summary>Resolves the open volley from two already-accepted choices (Pass allowed).</summary>
        public VolleyResult Resolve(VolleyInput inputA, VolleyInput inputB)
        {
            VolleyResult result = VolleyResolver.Resolve(State, inputA, inputB);
            Commit(result);
            return result;
        }

        /// <summary>Resolves with scripted geometric contacts (fixtures and tutorials).</summary>
        public VolleyResult ResolveWithScriptedContacts(VolleyInput inputA, VolleyInput inputB, IReadOnlyList<GeometricContact> contacts)
        {
            VolleyResult result = VolleyResolver.ResolveWithScriptedContacts(State, inputA, inputB, contacts);
            Commit(result);
            return result;
        }

        private void Commit(VolleyResult result)
        {
            _volleys.Add(result);
            State = result.NewState;
        }
    }
}
