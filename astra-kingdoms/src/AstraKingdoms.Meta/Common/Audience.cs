namespace AstraKingdoms.Meta.Common
{
    /// <summary>
    /// The age group the product treats a player as belonging to (plan: "Audience children photos and
    /// data"). Families policy distinguishes children and users of unknown age from adults; the
    /// applicable child threshold is a legal decision (DPDP uses under 18 once its child obligations
    /// apply), so the threshold itself lives in <see cref="AudiencePolicy"/>, not in this enum.
    /// </summary>
    public enum AgeGroup : byte
    {
        /// <summary>No neutral age screen answered yet. Treated like a child for ads and purchases.</summary>
        Unknown = 0,
        /// <summary>Below the configured child threshold.</summary>
        Child = 1,
        /// <summary>At or above the configured threshold.</summary>
        Adult = 2,
    }

    /// <summary>Verifiable parental consent state (needed before covered processing for children).</summary>
    public enum ParentalConsent : byte
    {
        NotRequested = 0,
        Pending = 1,
        Verified = 2,
        Refused = 3,
    }

    /// <summary>What the product knows about a player's audience status. Immutable.</summary>
    public sealed class AudienceProfile
    {
        public AgeGroup AgeGroup { get; }
        public ParentalConsent ParentalConsent { get; }

        public AudienceProfile(AgeGroup ageGroup, ParentalConsent parentalConsent = ParentalConsent.NotRequested)
        {
            AgeGroup = ageGroup;
            ParentalConsent = parentalConsent;
        }

        public static readonly AudienceProfile Unknown = new AudienceProfile(AgeGroup.Unknown);
        public static readonly AudienceProfile Adult = new AudienceProfile(AgeGroup.Adult);
        public static AudienceProfile Child(ParentalConsent consent = ParentalConsent.NotRequested) => new AudienceProfile(AgeGroup.Child, consent);

        /// <summary>Families rule of thumb: children and users of unknown age get the child-safe treatment.</summary>
        public bool NeedsChildSafeTreatment => AgeGroup != AgeGroup.Adult;

        public override string ToString() => AgeGroup + "/" + ParentalConsent;
    }

    /// <summary>
    /// Audience configuration chosen by the owner before selecting SDKs. Proposed defaults; the child
    /// threshold and whether children are in the target audience need legal review.
    /// </summary>
    public sealed class AudiencePolicy
    {
        /// <summary>Age below which a player is a child (18 matches DPDP's definition when it applies).</summary>
        public int ChildBelowAge { get; set; } = 18;

        /// <summary>True when children are part of the Play target audience (Families requirements apply).</summary>
        public bool ChildrenInTargetAudience { get; set; } = true;

        /// <summary>Maps a self-declared age from a neutral age screen to a group. Null means not answered.</summary>
        public AgeGroup Classify(int? declaredAge)
        {
            if (!declaredAge.HasValue || declaredAge.Value <= 0 || declaredAge.Value > 120) return AgeGroup.Unknown;
            return declaredAge.Value < ChildBelowAge ? AgeGroup.Child : AgeGroup.Adult;
        }
    }
}
