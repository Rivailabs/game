using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Rules.Balance
{
    /// <summary>
    /// The balance release a match was created with. Immutable: a later publication or rollback
    /// never changes a pinned snapshot ("an ongoing match continues using the version with which it
    /// started").
    /// </summary>
    public sealed class BalanceSnapshot
    {
        public string MatchId { get; }
        public BalanceBundle Bundle { get; }
        public string BundleId => Bundle.BundleId;
        public string ContentHashHex { get; }
        public string EffectiveRulesHashHex { get; }
        /// <summary>Channel history position that was active when the match was pinned.</summary>
        public int PublicationIndex { get; }

        /// <summary>The engine parameters the pinned match runs with (immutable, shared).</summary>
        public RulesParameters Parameters => Bundle.ToParameters();

        /// <summary>
        /// The room configuration pinned to this snapshot: <paramref name="room"/> with
        /// <see cref="Parameters"/> applied (see <see cref="MatchConfig.WithParameters"/>).
        /// </summary>
        public MatchConfig Pin(MatchConfig room)
        {
            if (room == null) throw new ArgumentNullException(nameof(room));
            return room.WithParameters(Parameters);
        }

        internal BalanceSnapshot(string matchId, BalanceBundle bundle, int publicationIndex)
        {
            MatchId = matchId;
            Bundle = bundle;
            ContentHashHex = bundle.ContentHashHex;
            EffectiveRulesHashHex = bundle.EffectiveRulesHashHex;
            PublicationIndex = publicationIndex;
        }
    }

    public enum PublicationAction : byte
    {
        Initial = 0,
        Publish = 1,
        Rollback = 2,
    }

    /// <summary>One append-only entry of the release log.</summary>
    public sealed class PublicationEntry
    {
        public int Index { get; }
        public PublicationAction Action { get; }
        public string BundleId { get; }
        public string ContentHashHex { get; }
        public string Actor { get; }
        public long AtUnixMs { get; }
        public string Reason { get; }

        internal PublicationEntry(int index, PublicationAction action, BalanceBundle bundle, string actor, long atUnixMs, string reason)
        {
            Index = index;
            Action = action;
            BundleId = bundle.BundleId;
            ContentHashHex = bundle.ContentHashHex;
            Actor = actor;
            AtUnixMs = atUnixMs;
            Reason = reason;
        }

        public override string ToString() => "#" + Index + " " + Action + " " + BundleId + " by " + Actor + " at " + AtUnixMs + (Reason != null ? " (" + Reason + ")" : string.Empty);
    }

    /// <summary>Outcome of a publish or rollback request; nothing changes unless <see cref="Accepted"/>.</summary>
    public sealed class PublicationResult
    {
        public bool Accepted { get; }
        public IReadOnlyList<BalanceIssue> Issues { get; }
        public PublicationEntry Entry { get; }

        internal PublicationResult(bool accepted, IReadOnlyList<BalanceIssue> issues, PublicationEntry entry)
        {
            Accepted = accepted;
            Issues = issues ?? Array.Empty<BalanceIssue>();
            Entry = entry;
        }

        public override string ToString() => Accepted ? "Accepted " + Entry : "Rejected: " + string.Join("; ", Issues);
    }

    /// <summary>
    /// Versioned balance publication and rollback (ticket 24) for one deployment channel. It keeps an
    /// append-only release log and the active bundle; <see cref="PinForNewMatch"/> gives every new
    /// match the active bundle and never changes that pin again, so publishing or rolling back only
    /// affects matches created afterwards. A bundle ID is bound to its content forever: republishing
    /// the same ID with different content is refused.
    /// <para>
    /// <b>Executability.</b> The AK-TR-1 engine reads every schema tunable from the
    /// <see cref="RulesParameters"/> pinned in its <see cref="MatchConfig"/>, so any valid bundle on
    /// the AK-TR-1 base is executable (<see cref="ParameterizedEngine"/>, the default). A
    /// deployment can still pass a stricter <c>isExecutable</c> check (for example
    /// <see cref="CompiledEngineOnly"/> while some clients cannot yet display tuned values); a
    /// bundle it rejects is refused with <c>NOT_EXECUTABLE</c>.
    /// </para>
    /// Thread-safe.
    /// </summary>
    public sealed class BalanceChannel
    {
        private readonly object _gate = new object();
        private readonly Func<BalanceBundle, bool> _isExecutable;
        private readonly List<PublicationEntry> _log = new List<PublicationEntry>();
        private readonly Dictionary<string, BalanceBundle> _known = new Dictionary<string, BalanceBundle>(StringComparer.Ordinal);
        /// <summary>Stack of activations; rollback pops to the previous active bundle.</summary>
        private readonly List<BalanceBundle> _activations = new List<BalanceBundle>();
        private readonly Dictionary<string, BalanceSnapshot> _pins = new Dictionary<string, BalanceSnapshot>(StringComparer.Ordinal);

        public string Name { get; }

        public BalanceChannel(string name, BalanceBundle initial, string actor, long atUnixMs, Func<BalanceBundle, bool> isExecutable = null)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            if (initial == null) throw new ArgumentNullException(nameof(initial));
            _isExecutable = isExecutable ?? ParameterizedEngine;
            var issues = BalanceValidator.Validate(initial);
            if (issues.Count > 0) throw new ArgumentException("Initial bundle is invalid: " + string.Join("; ", issues), nameof(initial));
            if (!_isExecutable(initial)) throw new ArgumentException("Initial bundle " + initial.BundleId + " is not executable by this engine.", nameof(initial));
            _known.Add(initial.BundleId, initial);
            _activations.Add(initial);
            _log.Add(new PublicationEntry(0, PublicationAction.Initial, initial, actor, atUnixMs, null));
        }

        /// <summary>Strict executability: only the AK-TR-1 baseline (the compiled rules hash).</summary>
        public static bool CompiledEngineOnly(BalanceBundle b) => b != null && b.EffectiveRulesHashHex == RulesBundle.HashHex;

        /// <summary>
        /// Default executability (ticket 24): the parameterized AK-TR-1 engine runs any bundle that
        /// validates on the AK-TR-1 base, because every tunable it may override is read from the
        /// match's pinned <see cref="RulesParameters"/>.
        /// </summary>
        public static bool ParameterizedEngine(BalanceBundle b)
        {
            if (b == null || b.BaseRulesVersion != RulesConstants.RulesVersion || !BalanceValidator.IsValid(b)) return false;
            try
            {
                return b.ToParameters().RulesHashHex == b.EffectiveRulesHashHex;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        public BalanceBundle Active
        {
            get { lock (_gate) return _activations[_activations.Count - 1]; }
        }

        public IReadOnlyList<PublicationEntry> History
        {
            get { lock (_gate) return _log.ToArray(); }
        }

        /// <summary>Validates and activates a bundle for matches created from now on.</summary>
        public PublicationResult Publish(BalanceBundle bundle, string actor, long atUnixMs)
        {
            lock (_gate)
            {
                var issues = new List<BalanceIssue>(BalanceValidator.Validate(bundle));
                if (bundle != null && issues.Count == 0)
                {
                    BalanceBundle active = _activations[_activations.Count - 1];
                    if (_known.TryGetValue(bundle.BundleId, out BalanceBundle existing))
                    {
                        if (existing.ContentHashHex != bundle.ContentHashHex)
                            issues.Add(new BalanceIssue("ID_REUSED", "Bundle ID " + bundle.BundleId + " was already published with different content; choose a new ID."));
                        else if (existing.BundleId == active.BundleId)
                            issues.Add(new BalanceIssue("ALREADY_ACTIVE", bundle.BundleId + " is already active."));
                    }
                    if (!bundle.IsBaseline && bundle.PreviousBundleId != null && !_known.ContainsKey(bundle.PreviousBundleId))
                        issues.Add(new BalanceIssue("LINEAGE", "Predecessor " + bundle.PreviousBundleId + " was never published on " + Name + "."));
                    if (issues.Count == 0 && !_isExecutable(bundle))
                        issues.Add(new BalanceIssue("NOT_EXECUTABLE",
                            "No engine in this deployment resolves effective rules hash " + bundle.EffectiveRulesHashHex + "."));
                }
                if (issues.Count > 0) return new PublicationResult(false, issues, null);

                if (!_known.ContainsKey(bundle.BundleId)) _known.Add(bundle.BundleId, bundle);
                _activations.Add(_known[bundle.BundleId]);
                var entry = new PublicationEntry(_log.Count, PublicationAction.Publish, bundle, actor, atUnixMs, null);
                _log.Add(entry);
                return new PublicationResult(true, null, entry);
            }
        }

        /// <summary>
        /// Re-activates the bundle that was active before the current one. Matches already pinned to
        /// the rolled-back bundle keep it; only new matches use the restored bundle.
        /// </summary>
        public PublicationResult Rollback(string actor, long atUnixMs, string reason)
        {
            lock (_gate)
            {
                if (_activations.Count < 2)
                    return new PublicationResult(false, new[] { new BalanceIssue("NOTHING_TO_ROLL_BACK", "Only the initial bundle has been active.") }, null);
                if (string.IsNullOrWhiteSpace(reason))
                    return new PublicationResult(false, new[] { new BalanceIssue("REASON", "A rollback needs a recorded reason.") }, null);
                _activations.RemoveAt(_activations.Count - 1);
                BalanceBundle restored = _activations[_activations.Count - 1];
                var entry = new PublicationEntry(_log.Count, PublicationAction.Rollback, restored, actor, atUnixMs, reason);
                _log.Add(entry);
                return new PublicationResult(true, null, entry);
            }
        }

        /// <summary>
        /// Pins the active bundle to a new match. Idempotent per match ID: a retried create returns the
        /// original pin even if the active bundle changed in between.
        /// </summary>
        public BalanceSnapshot PinForNewMatch(string matchId)
        {
            if (string.IsNullOrEmpty(matchId)) throw new ArgumentException("A match ID is required.", nameof(matchId));
            lock (_gate)
            {
                if (_pins.TryGetValue(matchId, out BalanceSnapshot existing)) return existing;
                var snapshot = new BalanceSnapshot(matchId, _activations[_activations.Count - 1], _log.Count - 1);
                _pins.Add(matchId, snapshot);
                return snapshot;
            }
        }

        /// <summary>The pinned snapshot of an existing match, or null.</summary>
        public BalanceSnapshot PinnedFor(string matchId)
        {
            lock (_gate) return matchId != null && _pins.TryGetValue(matchId, out BalanceSnapshot s) ? s : null;
        }

        /// <summary>
        /// A published bundle by ID (for replaying a pinned match), or null. Usable directly as the
        /// replayer's bundle resolver: <c>Replayer.Verify(record, channel.Find)</c>.
        /// </summary>
        public BalanceBundle Find(string bundleId)
        {
            lock (_gate) return bundleId != null && _known.TryGetValue(bundleId, out BalanceBundle b) ? b : null;
        }
    }
}
