namespace Zaagl.Modules.Links.Domain;

public static class RuleKinds
{
    public const string Device = "device";
    public const string Country = "country";
    public const string Split = "split";

    public static readonly string[] All = [Device, Country, Split];
    public static readonly string[] Devices = ["ios", "android", "desktop"];
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
