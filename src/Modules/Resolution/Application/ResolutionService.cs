using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Zaagl.Modules.Access.Application;
using Zaagl.Modules.Analytics.Contracts;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Links.Contracts;
using Zaagl.SharedKernel;

namespace Zaagl.Modules.Resolution.Application;

public sealed class ResolutionOptions
{
    public const string Section = "Resolution";

    /// <summary>Secret mixed into the daily visitor hash. Set a long random value in production.</summary>
    public string VisitorSalt { get; set; } = "dev-only-visitor-salt";
}

public sealed record VisitorRequest(string? Ip, string? UserAgent, string? Referer, string? CountryHint);

public enum ResolveKind
{
    NotFound,
    Redirect,
    Locked,
    /// <summary>Quarantined: 302 to <see cref="ResolveResult.Url"/> (the owner's fallback URL) when set,
    /// otherwise the 410 "expired" page (PD-42).</summary>
    Expired,
    Preview,
    /// <summary>Spec §4.3 "deleted": the 410 page, never the fallback URL.</summary>
    Deleted,
    /// <summary>Spec §4.3 "disabled": the 410 page (acceptable-use wording for abuse/safety), never the fallback URL.</summary>
    Disabled,
    /// <summary>PD-43: a click-limited link with no clicks left: its own 410 page, never the fallback URL.</summary>
    ClickLimitReached,
    /// <summary>C1 / PD-02 (ARC-002 §7): a content link. <see cref="ResolveResult.Url"/> is the viewer path
    /// <c>/{p|i|f}/&lt;code&gt;</c> on the app host; the endpoint prefixes Frontend:BaseUrl. The redirect never checks
    /// the password or the link key (the viewer does).</summary>
    Content,
}

public sealed record LinkPreview(string Slug, string? Title, string? Description, string? ImageUrl, string Destination);

/// <param name="Url">Destination for Redirect, or the fallback URL for Expired (null when none is configured).</param>
/// <param name="DisabledKind">For Disabled: the link's disabled_kind (admin, user-disabled, abuse, safety), or null.</param>
public sealed record ResolveResult(ResolveKind Kind, string? Url = null, LinkPreview? Preview = null, string? DisabledKind = null);

public sealed partial class ResolutionService(
    ILinkResolver links,
    AccessGate gate,
    ClientClassifier classifier,
    IGeoLookup geo,
    IClickRecorder clicks,
    IOptions<ResolutionOptions> options,
    TimeProvider time)
{
    [GeneratedRegex("^[A-Za-z]{2}$")]
    private static partial Regex CountryCode();

    /// <param name="countsAsClick">Spec §7.3 "HEAD never counts": false for HEAD, which then neither consumes a
    /// click nor records analytics. A link without a click limit answers as a GET would. PD-47: a click-limited
    /// link never reveals its destination to HEAD (the remaining budget lives only in PostgreSQL and is not
    /// peeked), so HEAD gets the click-limit outcome (410, no Location) whether or not clicks are left.</param>
    public async Task<ResolveResult> ResolveAsync(string slug, VisitorRequest request, CancellationToken ct, bool countsAsClick = true)
    {
        var link = await links.GetSnapshotAsync(SlugRules.Key(slug), ct);
        if (link is null) return new(ResolveKind.NotFound);

        var client = classifier.Classify(request.UserAgent);
        var now = time.GetUtcNow();
        var outcome = gate.Evaluate(link, now);

        // ARC-002 §7: content links. No fallback, no click limit, no Open Graph, no routing; password and key are
        // the viewer's job, so Allowed and Locked both go to the viewer.
        if (link.TargetKind != LinkTargetKind.Url)
            return ResolveContent(link, outcome, client, request, now, countsAsClick);

        // PD-47 (HEAD) and PD-48 (bots, link-preview crawlers): a click-limited link never reveals its destination
        // to them and they never consume a click. Checked before the crawler preview, whose page also carries
        // the destination. Links without a click limit keep the bot handling and chat previews below.
        if (link.MaxClicks is not null && (!countsAsClick || IsAutomated(client)) && !outcome.IsGone())
            return new(ResolveKind.ClickLimitReached);

        // Link-preview crawlers get the custom Open Graph tags instead of a redirect.
        if (client.IsPreviewCrawler && link.HasOpenGraph && !outcome.IsGone())
        {
            if (countsAsClick) Record(link, client, request, now);
            return new(ResolveKind.Preview, Preview: new(link.Slug, link.OgTitle, link.OgDescription, link.OgImageUrl, link.DestinationUrl!));
        }

        switch (outcome)
        {
            case AccessOutcome.Purgeable:
                return new(ResolveKind.NotFound);
            case AccessOutcome.Deleted:
                return new(ResolveKind.Deleted);
            case AccessOutcome.Disabled:
                return new(ResolveKind.Disabled, DisabledKind: link.DisabledKind);
            case AccessOutcome.Expired:
                return new(ResolveKind.Expired, link.FallbackUrl);
            case AccessOutcome.Locked:
                return new(ResolveKind.Locked);
        }

        // HEAD on a link without a click limit (PD-47 handled above): no analytics, answers like GET.
        if (!countsAsClick)
            return new(ResolveKind.Redirect, RoutingEvaluator.Choose(link, client.Platform, Locate(request).Country));

        return await PassAsync(link, client, request, now, ct);
    }

    private ResolveResult ResolveContent(
        LinkSnapshot link, AccessOutcome outcome, ClientProfile client, VisitorRequest request, DateTimeOffset now, bool countsAsClick)
    {
        switch (outcome)
        {
            case AccessOutcome.Purgeable: return new(ResolveKind.NotFound);
            case AccessOutcome.Deleted: return new(ResolveKind.Deleted);
            case AccessOutcome.Disabled: return new(ResolveKind.Disabled, DisabledKind: link.DisabledKind);
            case AccessOutcome.Expired: return new(ResolveKind.Expired); // never a fallback URL (addendum §17)
        }

        var segment = link.TargetKind switch
        {
            LinkTargetKind.Snippet => ViewerPathSegments.Snippet,
            LinkTargetKind.Media => ViewerPathSegments.Media,
            _ => ViewerPathSegments.File,
        };
        // GET records one Redirect event (owner analytics) or, for guest content, only the guest counter; HEAD nothing.
        if (countsAsClick) Record(link, client, request, now);
        return new(ResolveKind.Content, $"/{segment}/{Uri.EscapeDataString(link.Slug)}");
    }

    /// <summary>Checks the password for a locked link and, when correct, returns the destination to send the visitor to.</summary>
    public async Task<Result<string>> UnlockAsync(string slug, string? password, VisitorRequest request, CancellationToken ct)
    {
        var link = await links.GetSnapshotAsync(SlugRules.Key(slug), ct);
        // ARC-002 §6 row 17: content is unlocked through /api/public/content/{code}/unlock, never here.
        if (link is null || link.TargetKind != LinkTargetKind.Url) return Error.NotFound("Link not found.");

        var now = time.GetUtcNow();
        if (gate.Evaluate(link, now).IsGone())
            return new Error("expired", "This link has expired.");

        if (link.HasPassword && !gate.VerifyPassword(link, password))
            return Error.Unauthorized("Incorrect password.");

        var result = await PassAsync(link, classifier.Classify(request.UserAgent), request, now, ct);
        return result.Kind switch
        {
            ResolveKind.Redirect => Result<string>.Ok(result.Url!),
            ResolveKind.ClickLimitReached => new Error("expired", "This link has reached its click limit."),
            _ => new Error("expired", "This link has expired."),
        };
    }

    private async Task<ResolveResult> PassAsync(
        LinkSnapshot link, ClientProfile client, VisitorRequest request, DateTimeOffset now, CancellationToken ct)
    {
        // Click limits are authoritative in PostgreSQL and consumed atomically by real visitors only.
        // PD-48: bots and preview crawlers (e.g. via the unlock API) never get a click-limited destination.
        // PD-43: a used-up click limit has its own 410 page and never goes to the fallback URL.
        if (link.MaxClicks is not null
            && (IsAutomated(client) || !await links.TryConsumeClickAsync(link.Id, ct)))
            return new(ResolveKind.ClickLimitReached);

        var location = Locate(request);
        var destination = RoutingEvaluator.Choose(link, client.Platform, location.Country);
        Record(link, client, request, now, location, UtmTags.FromUrl(destination));
        return new(ResolveKind.Redirect, destination);
    }

    /// <summary>PD-48: requests classified as bots or link-preview crawlers.</summary>
    private static bool IsAutomated(ClientProfile client) => client.IsBot || client.IsPreviewCrawler;

    private void Record(
        LinkSnapshot link, ClientProfile client, VisitorRequest request, DateTimeOffset now,
        GeoLocation? known = null, UtmTags? utm = null)
    {
        // Spec §7.12: a guest link gets only its aggregate counter, never a raw click_events row or
        // daily stats - the owned-link analytics pipeline below is for account-owned links only.
        if (link.OwnerId is not { } ownerId)
        {
            _ = links.IncrementGuestClickCountAsync(link.Id, CancellationToken.None);
            return;
        }

        var location = known ?? Locate(request);
        clicks.TryRecord(new ClickEvent(
            link.Id,
            ownerId,
            now.UtcDateTime,
            VisitorHash(request, now),
            ReferrerHost(request.Referer),
            location.Country,
            location.City,
            client.Os,
            client.Browser,
            client.DeviceType,
            client.IsBot,
            utm));
    }

    private GeoLocation Locate(VisitorRequest request)
    {
        var lookup = geo.Lookup(request.Ip);
        var hint = request.CountryHint;
        // Cloudflare sets CF-IPCountry; "XX" = unknown and "T1" = Tor.
        var country = hint is not null && CountryCode().IsMatch(hint) && !hint.Equals("XX", StringComparison.OrdinalIgnoreCase)
            ? hint.ToUpperInvariant()
            : lookup.Country;
        return new(country, lookup.City);
    }

    /// <summary>Daily-rotating hash: counts unique visitors per day without storing an IP address.</summary>
    private string VisitorHash(VisitorRequest request, DateTimeOffset now)
    {
        var material = $"{request.Ip}|{request.UserAgent}|{now:yyyyMMdd}|{options.Value.VisitorSalt}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..32];
    }

    public static string? ReferrerHost(string? referer)
    {
        if (string.IsNullOrWhiteSpace(referer) || !Uri.TryCreate(referer, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        var host = uri.IdnHost.ToLowerInvariant();
        return host.StartsWith("www.") ? host[4..] : host;
    }
}
