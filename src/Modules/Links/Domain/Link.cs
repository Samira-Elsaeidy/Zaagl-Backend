namespace Zaagl.Modules.Links.Domain;

public static class RuleKinds
{
    public const string Device = "device";
    public const string Country = "country";
    public const string Split = "split";

    public static readonly string[] All = [Device, Country, Split];
    public static readonly string[] Devices = ["ios", "android", "desktop"];
}

public static class DisabledKinds
{
    public const string Admin = "admin";
    public const string UserDisabled = "user-disabled";
    public const string Abuse = "abuse";
    public const string Safety = "safety";
}

public sealed class Link
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Null for a guest link (spec §7.12): guests are identified only by <see cref="GuestCreatorKey"/>,
    /// never by an account, and a guest link cannot be managed by anyone (no renewal, edit, delete, stats).</summary>
    public Guid? OwnerId { get; set; }

    /// <summary>Guest links only: HMAC-SHA256(GUEST_KEY, ip-partition-tag) truncated to 16 bytes. Doubles as
    /// the idempotency scope for guest creation.</summary>
    public byte[]? GuestCreatorKey { get; set; }

    /// <summary>Guest links only (spec §7.12): the only statistic a guest link ever gets, an aggregate
    /// counter for abuse detection. Owned links get the full analytics pipeline instead (never this).</summary>
    public long ClickCount { get; set; }

    /// <summary>Slug as the user sees it (case preserved).</summary>
    public string Slug { get; set; } = "";

    /// <summary>Lower-cased slug; unique, so slugs differing only by case cannot coexist.</summary>
    public string SlugKey { get; set; } = "";

    public string DestinationUrl { get; set; } = "";

    /// <summary>Normalised host of DestinationUrl (punycode, lower-case, no trailing dot). Spec §7.6:
    /// used by the destination blocklist, takedowns and re-checks so they never re-parse the URL.</summary>
    public string TargetHost { get; set; } = "";

    /// <summary>Spec §7.15: null means never checked (outage, budget exhausted, or just created and not
    /// yet picked up) - fail-open, the link still redirects. Reset to null when a re-check lookup errors.</summary>
    public DateTime? SafetyCheckedAt { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public Guid? FolderId { get; set; }
    public string[] Tags { get; set; } = [];
    public bool IsActive { get; set; } = true;
    public bool PublicStats { get; set; }

    public string? OgTitle { get; set; }
    public string? OgDescription { get; set; }
    public string? OgImageUrl { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // ---- Spec §4.3: generated-vs-custom, state and lifecycle -----------------

    /// <summary>False for a user-chosen alias, true for a generator-produced slug.</summary>
    public bool IsCustom { get; set; }

    /// <summary>Generated links only: the leased number's base-62 length (5-10).</summary>
    public int? CodeLength { get; set; }

    /// <summary>Generated links only: the leased number before Feistel scrambling.</summary>
    public long? CodeNumber { get; set; }

    /// <summary>Generated links only: which scramble key version produced this code. Always 1 in this build.</summary>
    public short? KeyVersion { get; set; }

    /// <summary>expires_at + Links:QuarantineDays for owned links; recomputed on every renewal. Null = never expires.</summary>
    public DateTime? PurgeAfter { get; set; }

    public DateTime? DeletedAt { get; set; }

    /// <summary>One of <see cref="DisabledKinds"/>, or null when not disabled.</summary>
    public string? DisabledKind { get; set; }
    public string? DisabledReason { get; set; }
    public DateTime? DisabledAt { get; set; }

    public AccessPolicy? Policy { get; set; }
    public List<LinkDestination> Rules { get; set; } = [];
}

public sealed class AccessPolicy
{
    public Guid LinkId { get; set; }
    public string? PasswordHash { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public int? MaxClicks { get; set; }

    /// <summary>Authoritative click counter, only incremented for links that have a MaxClicks limit.</summary>
    public int ClicksUsed { get; set; }

    public string? FallbackUrl { get; set; }
}

/// <summary>A routing rule: device/OS or country match, or one variant of an A/B split.</summary>
public sealed class LinkDestination
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid LinkId { get; set; }
    public string Kind { get; set; } = "";
    public string? Match { get; set; }
    public string Url { get; set; } = "";
    public int Weight { get; set; } = 1;
    public int Priority { get; set; }
}

public static class BlockedDestinationKinds
{
    public const string Host = "host";
    public const string HostSuffix = "host-suffix";
    public const string UrlPrefix = "url-prefix";
}

/// <summary>Spec §7.15: checked in Phase B, in PATCH and by takedowns. Seeded with Safety:DenyRedirectorHosts.</summary>
public sealed class BlockedDestination
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Kind { get; set; } = "";
    public string Pattern { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Spec §7.15: a custom alias taken down for abuse or by the safety check is never released,
/// even after purge, unless the takedown is reversed with the audited reinstate.</summary>
public sealed class BlockedAlias
{
    public string SlugKey { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Spec §7.15: evidence kept for Abuse:EvidenceRetentionDays after a takedown, independent of
/// whether the link itself still exists.</summary>
public sealed class AbuseEvidence
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid LinkId { get; set; }
    public string Slug { get; set; } = "";
    public string TargetUrl { get; set; } = "";
    public string DisabledKind { get; set; } = "";
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime KeepUntil { get; set; }
}

public static class ReportStatuses
{
    public const string Open = "open";
    public const string Actioned = "actioned";
    public const string Dismissed = "dismissed";
}

/// <summary>Spec §7.15: `POST /api/reports`, public, CAPTCHA-gated. Resolved by an admin.</summary>
public sealed class AbuseReport
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string? Code { get; set; }
    public string? Url { get; set; }
    public string Category { get; set; } = "";
    public string? Details { get; set; }
    public string? ReporterEmail { get; set; }
    public string Status { get; set; } = ReportStatuses.Open;
    public string? Resolution { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
}

/// <summary>Spec §4.5 rule 5: an admin-managed denylist on top of the static reserved-word list in
/// SlugRules - patterns an operator adds after the fact (e.g. a brand name someone keeps squatting).</summary>
public sealed class AliasDenylistEntry
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Pattern { get; set; } = "";
    /// <summary>"exact" or "prefix".</summary>
    public string Kind { get; set; } = "exact";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Spec §7.11: while active, no new alias can be claimed by anyone. This build has only the
/// operator-declared source (manual `declare-restore`); the automatic coordinator-driven sync job
/// (restore_detected_at -> freeze) is not built yet - see docs/TRACEABILITY.md.</summary>
public sealed class AliasFreeze
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public DateTime StartsAt { get; set; }
    public DateTime Until { get; set; }
    public string Source { get; set; } = "operator";
    public string? Reason { get; set; }
    public DateTime? EndedAt { get; set; }
}

public sealed class Folder
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OwnerId { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Spec §4.4/§7.2: one row per (scope, key). `scope` is "u:&lt;userId&gt;" for an account creator.
/// Guard against two different request bodies reusing the same key (`RequestHash` mismatch -&gt; 422) and,
/// once `Status` is "done", replay the exact stored response regardless of current plan/quota/clock.
/// </summary>
public sealed class IdempotencyKey
{
    public string Scope { get; set; } = "";
    public string Key { get; set; } = "";
    public byte[] RequestHash { get; set; } = [];
    public string Status { get; set; } = "pending"; // "pending" | "done"
    public int? StatusCode { get; set; }
    public string? ResponseBody { get; set; } // jsonb
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
