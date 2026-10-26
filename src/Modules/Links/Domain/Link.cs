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
    public Guid OwnerId { get; set; }

    /// <summary>Slug as the user sees it (case preserved).</summary>
    public string Slug { get; set; } = "";

    /// <summary>Lower-cased slug; unique, so slugs differing only by case cannot coexist.</summary>
    public string SlugKey { get; set; } = "";

    public string DestinationUrl { get; set; } = "";
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
