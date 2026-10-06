namespace AstraKingdoms.Server.MetaHost;

/// <summary>Which Play purchase verifier the service uses.</summary>
public enum PurchaseVerifierMode
{
    /// <summary>Purchases are refused with a configuration error (no verifier configured).</summary>
    Disabled = 0,
    /// <summary>In-memory stand-in (Development only, unless explicitly allowed). Proves nothing about Google Play.</summary>
    Fake = 1,
    /// <summary>Google Play Developer API with a service-account key (production).</summary>
    GooglePlay = 2,
}

/// <summary>
/// Settings for the hosted meta services (V1 tickets 57-64), configuration section
/// <c>AstraServer:Meta</c>. Secrets (the service-account key file and the account-id salt) come
/// from the deployment's secret store through environment variables, never from the repository.
/// </summary>
public sealed class MetaOptions
{
    public MetaBillingOptions Billing { get; set; } = new();
    public MetaAdOptions Ads { get; set; } = new();
    public MetaPrivacyOptions Privacy { get; set; } = new();
    public MetaAnalyticsOptions Analytics { get; set; } = new();
    public MetaJobOptions Jobs { get; set; } = new();
}

public sealed class MetaBillingOptions
{
    public PurchaseVerifierMode Verifier { get; set; } = PurchaseVerifierMode.Disabled;
    /// <summary>Allows the fake verifier outside Development (private test hosts only).</summary>
    public bool AllowFakeOutsideDevelopment { get; set; }
    /// <summary>Android package name the purchases belong to.</summary>
    public string PackageName { get; set; } = "com.rivailabs.astrakingdoms";
    /// <summary>Path of the Google service-account JSON key (secret; GooglePlay mode).</summary>
    public string ServiceAccountKeyPath { get; set; } = "";
    /// <summary>Salt of the obfuscated account id passed to the billing flow (secret; required unless Disabled).</summary>
    public string AccountIdSalt { get; set; } = "";
    /// <summary>Accept licence-tester purchases (internal/closed tracks).</summary>
    public bool AcceptTestPurchases { get; set; } = true;
}

public sealed class MetaAdOptions
{
    public bool RewardedAdsEnabled { get; set; } = true;
    /// <summary>Owner confirmation that the ad SDK stack is Families-certified (children/unknown age get offers only then).</summary>
    public bool FamiliesCertifiedSdkConfirmed { get; set; }
    /// <summary>The ad network's SSV verifier-key document.</summary>
    public string SsvKeysUrl { get; set; } = AstraKingdoms.Meta.Server.HttpAdVerifierKeyProvider.AdMobKeysUrl;
}

public sealed class MetaPrivacyOptions
{
    /// <summary>Published completion window for deletion requests (proposed: 30 days).</summary>
    public int DeletionCompletionDays { get; set; } = 30;
    public string AccountDeletionUrl { get; set; } = "https://example.invalid/astra-kingdoms/delete-account";
    public string PrivacyPolicyUrl { get; set; } = "https://example.invalid/astra-kingdoms/privacy";
}

public sealed class MetaAnalyticsOptions
{
    /// <summary>Accept product analytics for children/unknown age (only after the owner's assessment approves it).</summary>
    public bool ChildProductAnalyticsApproved { get; set; }
    public bool ChildCrashReportsApproved { get; set; }
    /// <summary>Largest accepted batch (the client sends at most 50).</summary>
    public int MaxBatch { get; set; } = 50;
    /// <summary>Events older than this when uploaded are rejected as stale.</summary>
    public int MaxEventAgeDays { get; set; } = 30;
}

public sealed class MetaJobOptions
{
    public int AcknowledgementRetryMinutes { get; set; } = 5;
    public int VoidedPurchasePollHours { get; set; } = 12;
    public int DeletionProcessingMinutes { get; set; } = 60;
    public int RetentionSweepHours { get; set; } = 24;
    public int ReconciliationMinutes { get; set; } = 60;
}
