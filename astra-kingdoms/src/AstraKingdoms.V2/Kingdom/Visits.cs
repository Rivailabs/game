using System;
using System.Collections.Generic;
using AstraKingdoms.V2.Common;

namespace AstraKingdoms.V2.Kingdom
{
    public enum VisitDenial : byte
    {
        None = 0,
        /// <summary>The owner turned visits off.</summary>
        VisitsDisabled = 1,
        /// <summary>The visitor is not in the owner's chosen audience (friends by default).</summary>
        NotInAudience = 2,
        /// <summary>A block exists in either direction. Reported to the visitor exactly like NotInAudience.</summary>
        Blocked = 3,
        /// <summary>Moderation suspended visiting for the visitor.</summary>
        Suspended = 4,
    }

    /// <summary>
    /// A read-only copy of someone else's homeland. It carries the owner's alias, never an account
    /// id, and exposes no method that could change the owner's kingdom.
    /// </summary>
    public sealed class KingdomSnapshot
    {
        public string OwnerAlias { get; }
        public string OwnerAvatarId { get; }
        public IReadOnlyList<PlotView> Plots { get; }
        public DateTimeOffset TakenAt { get; }

        public KingdomSnapshot(string ownerAlias, string ownerAvatarId, IReadOnlyList<PlotView> plots, DateTimeOffset takenAt)
        {
            OwnerAlias = ownerAlias;
            OwnerAvatarId = ownerAvatarId;
            Plots = plots;
            TakenAt = takenAt;
        }
    }

    public sealed class VisitResult
    {
        public KingdomSnapshot Snapshot { get; }
        public VisitDenial Denial { get; }
        public bool Allowed => Snapshot != null;

        /// <summary>
        /// What the visitor's client is told. A block is reported as "not available", identical to a
        /// visitor outside the audience, so the blocked account cannot learn that it was blocked.
        /// </summary>
        public VisitDenial VisibleDenial => Denial == VisitDenial.Blocked ? VisitDenial.NotInAudience : Denial;

        public VisitResult(KingdomSnapshot snapshot, VisitDenial denial)
        {
            Snapshot = snapshot;
            Denial = denial;
        }
    }

    /// <summary>Profile facts a visit displays (alias and avatar id), supplied by the profile/avatars service.</summary>
    public interface IPublicProfiles
    {
        string Alias(string playerId);
        string AvatarId(string playerId);
    }

    /// <summary>
    /// Read-only kingdom visits (plan: "Visits are read-only and friends-only by default; owners can
    /// disable visits"). Checks, in order: owner setting, block (either direction), moderation
    /// suspension, audience.
    /// </summary>
    public sealed class VisitService
    {
        private readonly KingdomService _kingdoms;
        private readonly ISocialRelations _relations;
        private readonly IFeatureGate _gate;
        private readonly IPublicProfiles _profiles;
        private readonly Meta.Common.IClock _clock;

        public VisitService(KingdomService kingdoms, ISocialRelations relations, IFeatureGate gate, IPublicProfiles profiles, Meta.Common.IClock clock)
        {
            _kingdoms = kingdoms ?? throw new ArgumentNullException(nameof(kingdoms));
            _relations = relations ?? throw new ArgumentNullException(nameof(relations));
            _gate = gate ?? OpenFeatureGate.Instance;
            _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public VisitResult Visit(string visitorId, string ownerId)
        {
            if (visitorId == ownerId) return new VisitResult(Snapshot(ownerId), VisitDenial.None);
            VisitAudience audience = _kingdoms.GetVisitAudience(ownerId);
            if (audience == VisitAudience.Nobody) return new VisitResult(null, VisitDenial.VisitsDisabled);
            if (_relations.IsBlockedEitherWay(visitorId, ownerId)) return new VisitResult(null, VisitDenial.Blocked);
            if (_gate.IsSuspended(visitorId, SocialFeature.KingdomVisits, _clock.UtcNow)) return new VisitResult(null, VisitDenial.Suspended);
            bool inAudience = _relations.AreFriends(visitorId, ownerId) ||
                              (audience == VisitAudience.FriendsAndClanmates && _relations.AreClanmates(visitorId, ownerId));
            return inAudience ? new VisitResult(Snapshot(ownerId), VisitDenial.None) : new VisitResult(null, VisitDenial.NotInAudience);
        }

        private KingdomSnapshot Snapshot(string ownerId)
        {
            LayoutLoadResult load = _kingdoms.LoadLayout(ownerId);
            return new KingdomSnapshot(_profiles.Alias(ownerId), _profiles.AvatarId(ownerId),
                _kingdoms.BuildPlots(load.Layout, _kingdoms.OpenPlots(ownerId)), _clock.UtcNow);
        }
    }
}
