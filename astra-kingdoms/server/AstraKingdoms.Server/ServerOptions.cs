using AstraKingdoms.Client.Online.Protocol;
using AstraKingdoms.Rules.Bots;

namespace AstraKingdoms.Server;

/// <summary>
/// All service settings, bound from configuration section "AstraServer" (appsettings.json, then
/// environment variables such as <c>AstraServer__Auth__Mode=Firebase</c>). Defaults are the plan's
/// online values; anything marked "proposed" is a service policy the plan leaves open.
/// </summary>
public sealed class ServerOptions
{
    public const string Section = "AstraServer";

    public AuthOptions Auth { get; set; } = new();
    public TimingOptions Timings { get; set; } = new();
    public RoomOptions Rooms { get; set; } = new();
    public QueueOptions Queue { get; set; } = new();
    public RateLimitOptions RateLimits { get; set; } = new();
    public StorageOptions Storage { get; set; } = new();
    public LifecycleOptions Lifecycle { get; set; } = new();
    public ClientPolicyOptions Clients { get; set; } = new();
    public GrievanceOptions Grievance { get; set; } = new();
    public ConnectionOptions Connections { get; set; } = new();
}

public enum AuthMode
{
    /// <summary>Firebase Authentication ID tokens (RS256, Google signing keys). Production.</summary>
    Firebase = 0,
    /// <summary>"dev:&lt;name&gt;" tokens for local play and tests. Refused outside the Development environment unless explicitly allowed.</summary>
    Dev = 1,
}

public sealed class AuthOptions
{
    public AuthMode Mode { get; set; } = AuthMode.Firebase;
    /// <summary>Firebase project ID: the required audience, and the issuer suffix.</summary>
    public string FirebaseProjectId { get; set; } = "";
    /// <summary>Google's x509 key document for Firebase ID tokens.</summary>
    public string SigningKeysUrl { get; set; } = "https://www.googleapis.com/robot/v1/metadata/x509/securetoken@system.gserviceaccount.com";
    /// <summary>Clock skew tolerated on exp/iat/auth_time (seconds).</summary>
    public int ClockSkewSeconds { get; set; } = 60;
    /// <summary>Allows Dev mode outside the Development environment (load tests on a private host only).</summary>
    public bool AllowDevOutsideDevelopment { get; set; }
}

/// <summary>Online phase clock (plan: 2 s terrain, 12 s concurrent choice, 2.5 s replay, 12 s card and cut).</summary>
public sealed class TimingOptions
{
    public int AnnounceMs { get; set; } = 2000;
    public int ChoiceMs { get; set; } = 12000;
    public int ReplayMs { get; set; } = 2500;
    public int CutMs { get; set; } = 12000;
    /// <summary>Proposed: private loadout window before round one. Expiry is a technical void (no reward, no loss).</summary>
    public int SetupMs { get; set; } = 60000;
    /// <summary>Visible delay before a server bot acts (0 = immediately).</summary>
    public int BotThinkMs { get; set; } = 1200;

    public PhaseTimings ToWire() => new()
    {
        AnnounceMs = AnnounceMs,
        ChoiceMs = ChoiceMs,
        ReplayMs = ReplayMs,
        CutMs = CutMs,
        SetupMs = SetupMs,
    };
}

public sealed class RoomOptions
{
    /// <summary>Characters in a friend-room code (alphabet without 0/O, 1/I/L).</summary>
    public int CodeLength { get; set; } = 6;
    /// <summary>Proposed: a room code expires this long after creation unless its match has started.</summary>
    public int ExpirySeconds { get; set; } = 600;
    /// <summary>Proposed: a disconnected host keeps the room this long before it closes.</summary>
    public int HostGraceSeconds { get; set; } = 30;
}

public sealed class QueueOptions
{
    /// <summary>Plan (proposed): after this wait, offer a clearly labelled unranked bot match.</summary>
    public int BotOfferAfterSeconds { get; set; } = 20;
    public BotDifficulty BotDifficulty { get; set; } = BotDifficulty.Normal;
}

/// <summary>Token bucket per authenticated identity (and per IP for anonymous endpoints).</summary>
public sealed class RateLimitOptions
{
    public int Capacity { get; set; } = 40;
    public double RefillPerSecond { get; set; } = 20;
    /// <summary>Consecutive limited messages after which the connection is closed (policy violation).</summary>
    public int CloseAfterViolations { get; set; } = 50;
    /// <summary>Anonymous HTTP requests (grievance intake) per IP: capacity and refill per minute.</summary>
    public int AnonymousCapacity { get; set; } = 10;
    public double AnonymousRefillPerMinute { get; set; } = 10;
}

public sealed class StorageOptions
{
    /// <summary>SQLite database file. Contains seeds and choices: restrict access (see RUNBOOK.md).</summary>
    public string SqlitePath { get; set; } = "data/astra-server.db";
    /// <summary>Plan default: game diagnostic match records are kept 30 days.</summary>
    public int MatchRecordRetentionDays { get; set; } = 30;
}

public enum ShutdownMatchPolicy
{
    /// <summary>Suspend active matches with their remaining phase time and resume them after restart.</summary>
    Preserve = 0,
    /// <summary>Settle active matches as technical voids (no reward, no loss).</summary>
    Void = 1,
}

public sealed class LifecycleOptions
{
    public ShutdownMatchPolicy ShutdownPolicy { get; set; } = ShutdownMatchPolicy.Preserve;
    /// <summary>A suspended match older than this at startup is settled as a technical void instead of resumed.</summary>
    public int MaxSuspendMinutes { get; set; } = 10;
}

public sealed class ClientPolicyOptions
{
    /// <summary>Lowest client build accepted (controlled minimum-version policy; dotted numeric).</summary>
    public string MinimumVersion { get; set; } = "0.0.0";
}

/// <summary>
/// Grievance contact (India Online Gaming Rules, Rule 20: a functional grievance mechanism).
/// Values are placeholders until the owner names the responsible operator.
/// </summary>
public sealed class GrievanceOptions
{
    public string OperatorName { get; set; } = "Grievance officer (to be named by the owner)";
    public string Email { get; set; } = "grievance@example.invalid";
    public string Url { get; set; } = "https://example.invalid/grievance";
    public int AcknowledgeWithinHours { get; set; } = 24;
    public int ResolveWithinDays { get; set; } = 15;
}

public sealed class ConnectionOptions
{
    /// <summary>Seconds a new socket has to send hello.</summary>
    public int HelloTimeoutSeconds { get; set; } = 10;
    /// <summary>Server version string reported in welcome.</summary>
    public string ServerVersion { get; set; } = "1.0.0";
}
