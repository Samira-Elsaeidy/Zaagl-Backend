using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using Npgsql;
using Zaagl.Modules.Billing.Contracts;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Links.Domain;
using Zaagl.Modules.Links.Infrastructure;
using Zaagl.SharedKernel;
using Zaagl.SharedKernel.ShortCodes;

namespace Zaagl.Modules.Links.Application;

public sealed class LinkOptions
{
    public const string Section = "ShortLinks";

    /// <summary>Public origin of short links; its host is blocked as a destination to prevent redirect loops.</summary>
    public string BaseUrl { get; set; } = "https://zaa.gl";
}

public static class LinkCache
{
    public static string Key(string slugKey) => $"link:{slugKey}";
}

public sealed class LinkService(
    LinksDbContext db,
    IPlanProvider plans,
    ShortCodeGenerator codes,
    IUrlReputationChecker reputation,
    HybridCache cache,
    IValidator<LinkInput> validator,
    IOptions<LinkOptions> options)
{
    private const int MaxSlugAttempts = 5;

    private readonly IReadOnlyCollection<string> _ownHosts = OwnHosts(options.Value.BaseUrl);

    private sealed record Normalized(
        string Destination,
        string? Fallback,
        string? OgImage,
        List<RuleInput> Rules,
        string[] Tags);

    // ---- Create -----------------------------------------------------------

    public async Task<Result<LinkDto>> CreateAsync(Guid userId, LinkInput input, CancellationToken ct)
    {
        var limits = await plans.GetLimitsAsync(userId, ct);

        if (limits.MonthlyLinks is { } cap)
        {
            var now = DateTime.UtcNow;
            var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var used = await db.Links.CountAsync(l => l.OwnerId == userId && l.CreatedAt >= monthStart, ct);
            if (used >= cap)
                return Error.PlanRequired($"The Free plan allows {cap} links per month. Upgrade to Starter for unlimited links.");
        }

        var prepared = await PrepareAsync(userId, input, limits, isCreate: true, ct);
        if (prepared.Error is not null) return prepared.Error;
        var n = prepared.Value!;

        if (input.Policy?.ExpiresAt is { } expires && expires <= DateTime.UtcNow)
            return Error.Validation("The expiry date must be in the future.");

        var customSlug = string.IsNullOrWhiteSpace(input.CustomSlug) ? null : input.CustomSlug.Trim();

        for (var attempt = 0; attempt < (customSlug is null ? MaxSlugAttempts : 1); attempt++)
        {
            var slug = customSlug ?? await NextGeneratedSlugAsync(ct);
            if (slug is null) continue;

            var link = new Link
            {
                OwnerId = userId,
                Slug = slug,
                SlugKey = SlugRules.Key(slug),
            };
            ApplyInput(link, input, n);
            db.Links.Add(link);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.ChangeTracker.Clear();
                if (customSlug is not null) return Error.Conflict("This slug is already taken.");
                continue; // generated slug collided case-insensitively; take the next number
            }

            await cache.RemoveAsync(LinkCache.Key(link.SlugKey), ct);
            return Result<LinkDto>.Ok(ToDto(link));
        }

        return Error.Conflict("Could not allocate a unique slug. Please try again.");
    }

    // ---- Update -----------------------------------------------------------

    public async Task<Result<LinkDto>> UpdateAsync(Guid userId, Guid id, LinkInput input, CancellationToken ct)
    {
        var link = await db.Links.Include(l => l.Policy).Include(l => l.Rules)
            .FirstOrDefaultAsync(l => l.Id == id && l.OwnerId == userId, ct);
        if (link is null) return Error.NotFound("Link not found.");

        var limits = await plans.GetLimitsAsync(userId, ct);
        var prepared = await PrepareAsync(userId, input with { CustomSlug = null }, limits, isCreate: false, ct);
        if (prepared.Error is not null) return prepared.Error;

        if (input.Rules is not null) db.Destinations.RemoveRange(link.Rules);
        ApplyInput(link, input, prepared.Value!);
        link.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await cache.RemoveAsync(LinkCache.Key(link.SlugKey), ct);
        return Result<LinkDto>.Ok(ToDto(link));
    }

    // ---- Read / delete ----------------------------------------------------

    public async Task<LinkDto?> GetAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var link = await db.Links.AsNoTracking().Include(l => l.Policy).Include(l => l.Rules)
            .FirstOrDefaultAsync(l => l.Id == id && l.OwnerId == userId, ct);
        return link is null ? null : ToDto(link);
    }

    public async Task<PagedResult<LinkDto>> ListAsync(Guid userId, ListLinksQuery query, CancellationToken ct)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var q = db.Links.AsNoTracking().Where(l => l.OwnerId == userId);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{EscapeLike(query.Search.Trim())}%";
            q = q.Where(l => EF.Functions.ILike(l.Slug, pattern)
                || EF.Functions.ILike(l.DestinationUrl, pattern)
                || (l.Title != null && EF.Functions.ILike(l.Title, pattern)));
        }
        if (query.FolderId is { } folderId) q = q.Where(l => l.FolderId == folderId);
        if (!string.IsNullOrWhiteSpace(query.Tag))
        {
            var tag = query.Tag.Trim();
            q = q.Where(l => l.Tags.Contains(tag));
        }

        var total = await q.CountAsync(ct);
        var links = await q.OrderByDescending(l => l.CreatedAt).ThenBy(l => l.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Include(l => l.Policy).Include(l => l.Rules)
            .ToListAsync(ct);

        return new(links.Select(ToDto).ToList(), total, page, pageSize);
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var link = await db.Links.FirstOrDefaultAsync(l => l.Id == id && l.OwnerId == userId, ct);
        if (link is null) return false;

        db.Links.Remove(link); // policy and rules cascade
        await db.SaveChangesAsync(ct);
        await cache.RemoveAsync(LinkCache.Key(link.SlugKey), ct);
        return true;
    }

    public async Task<IReadOnlyList<string>> ListTagsAsync(Guid userId, CancellationToken ct)
    {
        var all = await db.Links.AsNoTracking().Where(l => l.OwnerId == userId && l.Tags.Length > 0)
            .Select(l => l.Tags).ToListAsync(ct);
        return all.SelectMany(t => t).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<SlugAvailabilityDto> CheckSlugAsync(string? slug, CancellationToken ct)
    {
        if (!SlugRules.IsValidFormat(slug))
            return new(false, "Slug must be 3-64 characters: letters, digits, '-' or '_'.");
        if (SlugRules.IsReserved(slug!)) return new(false, "This slug is reserved.");
        var taken = await db.Links.AnyAsync(l => l.SlugKey == SlugRules.Key(slug!), ct);
        return taken ? new(false, "This slug is already taken.") : new(true, null);
    }

    // ---- Internals --------------------------------------------------------

    private async Task<Result<Normalized>> PrepareAsync(
        Guid userId, LinkInput input, PlanLimits limits, bool isCreate, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(input, ct);
        if (!validation.IsValid)
            return Error.Validation(string.Join(" ", validation.Errors.Select(e => e.ErrorMessage).Distinct()));

        if (CheckPlan(input, limits) is { } planError) return planError;

        var normalized = Normalize(input);
        if (normalized.Error is not null) return normalized.Error;
        var n = normalized.Value!;

        if (input.FolderId is { } folderId
            && !await db.Folders.AnyAsync(f => f.Id == folderId && f.OwnerId == userId, ct))
            return Error.Validation("Folder not found.");

        var urls = n.Rules.Select(r => r.Url).Append(n.Destination).Distinct();
        foreach (var url in urls)
        {
            var result = await reputation.CheckAsync(url, ct);
            if (!result.IsSafe)
                return Error.Validation($"This URL was flagged as unsafe ({FriendlyThreat(result.Threat)}) and cannot be shortened.");
        }

        return Result<Normalized>.Ok(n);
    }

    private static Error? CheckPlan(LinkInput input, PlanLimits limits)
    {
        if (!string.IsNullOrWhiteSpace(input.CustomSlug) && !limits.CustomSlugs)
            return Error.PlanRequired("Custom slugs need the Starter plan.");

        if (limits.AdvancedGates) return null;
        if (!string.IsNullOrEmpty(input.Policy?.Password))
            return Error.PlanRequired("Password protection needs the Starter plan.");
        if (input.Policy?.MaxClicks is not null)
            return Error.PlanRequired("Click limits need the Starter plan.");
        if (input.Rules is { Length: > 0 })
            return Error.PlanRequired("Device, country and A/B routing need the Starter plan.");
        return null;
    }

    private Result<Normalized> Normalize(LinkInput input)
    {
        if (!UrlSafety.TryNormalize(input.DestinationUrl, _ownHosts, out var destination, out var error))
            return Error.Validation($"Destination URL: {error}");

        string? fallback = null;
        if (!string.IsNullOrWhiteSpace(input.Policy?.FallbackUrl))
        {
            if (!UrlSafety.TryNormalize(input.Policy.FallbackUrl, _ownHosts, out fallback, out error))
                return Error.Validation($"Fallback URL: {error}");
        }

        string? ogImage = null;
        if (!string.IsNullOrWhiteSpace(input.OpenGraph?.ImageUrl))
        {
            if (!UrlSafety.TryNormalize(input.OpenGraph.ImageUrl, [], out ogImage, out error))
                return Error.Validation($"Preview image URL: {error}");
        }

        var rules = new List<RuleInput>();
        foreach (var rule in input.Rules ?? [])
        {
            if (!UrlSafety.TryNormalize(rule.Url, _ownHosts, out var url, out error))
                return Error.Validation($"Routing rule URL: {error}");

            var kind = rule.Kind.ToLowerInvariant();
            var match = kind switch
            {
                RuleKinds.Device => rule.Match?.ToLowerInvariant(),
                RuleKinds.Country => rule.Match?.ToUpperInvariant(),
                _ => null,
            };
            rules.Add(new RuleInput(kind, match, url, rule.Weight, rule.Priority));
        }
        if (rules.Count(r => r.Kind == RuleKinds.Split) == 1)
            return Error.Validation("A/B split testing needs at least two variants.");

        var tags = (input.Tags ?? [])
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Result<Normalized>.Ok(new(destination, fallback, ogImage, rules, tags));
    }

    private static void ApplyInput(Link link, LinkInput input, Normalized n)
    {
        link.DestinationUrl = n.Destination;
        link.Title = Clean(input.Title);
        link.Description = Clean(input.Description);
        link.FolderId = input.FolderId;
        link.PublicStats = input.PublicStats;
        link.IsActive = input.IsActive;
        if (input.Tags is not null) link.Tags = n.Tags;

        if (input.OpenGraph is { } og)
        {
            link.OgTitle = Clean(og.Title);
            link.OgDescription = Clean(og.Description);
            link.OgImageUrl = n.OgImage;
        }

        if (input.Policy is { } p)
        {
            var policy = link.Policy ??= new AccessPolicy { LinkId = link.Id };
            policy.ExpiresAt = p.ExpiresAt?.ToUniversalTime();
            policy.MaxClicks = p.MaxClicks;
            policy.FallbackUrl = n.Fallback;
            if (!string.IsNullOrEmpty(p.Password)) policy.PasswordHash = BCrypt.Net.BCrypt.HashPassword(p.Password);
            else if (p.ClearPassword) policy.PasswordHash = null;
        }

        if (input.Rules is not null)
        {
            link.Rules = n.Rules.Select(r => new LinkDestination
            {
                LinkId = link.Id,
                Kind = r.Kind,
                Match = r.Match,
                Url = r.Url,
                Weight = r.Weight,
                Priority = r.Priority,
            }).ToList();
        }
    }

    private async Task<string?> NextGeneratedSlugAsync(CancellationToken ct)
    {
        var next = await db.Database.SqlQuery<long>($"SELECT nextval('links.slug_seq') AS \"Value\"").SingleAsync(ct);
        var slug = codes.Encode(next);
        return SlugRules.IsReserved(slug) ? null : slug;
    }

    public static LinkDto ToDto(Link l) => new(
        l.Id,
        l.Slug,
        l.DestinationUrl,
        l.Title,
        l.Description,
        l.FolderId,
        l.Tags,
        l.IsActive,
        l.PublicStats,
        new OpenGraphDto(l.OgTitle, l.OgDescription, l.OgImageUrl),
        new PolicyDto(
            l.Policy?.PasswordHash is not null,
            l.Policy?.ExpiresAt,
            l.Policy?.MaxClicks,
            l.Policy?.ClicksUsed ?? 0,
            l.Policy?.FallbackUrl),
        l.Rules.OrderBy(r => r.Priority).ThenBy(r => r.Id)
            .Select(r => new RuleDto(r.Kind, r.Match, r.Url, r.Weight, r.Priority)).ToList(),
        l.CreatedAt,
        l.UpdatedAt);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static string FriendlyThreat(string? threat) => threat switch
    {
        "MALWARE" => "malware",
        "SOCIAL_ENGINEERING" => "phishing",
        "UNWANTED_SOFTWARE" => "unwanted software",
        _ => "malicious content",
    };

    private static IReadOnlyCollection<string> OwnHosts(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return [];
        var host = uri.IdnHost.ToLowerInvariant();
        return [host, host.StartsWith("www.") ? host[4..] : $"www.{host}"];
    }
}
