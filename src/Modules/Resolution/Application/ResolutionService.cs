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
    Expired,
    Preview,
}

public sealed record LinkPreview(string Slug, string? Title, string? Description, string? ImageUrl, string Destination);

/// <param name="Url">Destination for Redirect, or the fallback URL for Expired (null when none is configured).</param>
public sealed record ResolveResult(ResolveKind Kind, string? Url = null, LinkPreview? Preview = null);

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

    public async Task<ResolveResult> ResolveAsync(string slug, VisitorRequest request, CancellationToken ct)
    {
        var link = await links.GetSnapshotAsync(SlugRules.Key(slug), ct);
        if (link is null) return new(ResolveKind.NotFound);

        var client = classifier.Classify(request.UserAgent);
        var now = time.GetUtcNow();
        var outcome = gate.Evaluate(link, now);

        // Link-preview crawlers get the custom Open Graph tags instead of a redirect.
        if (client.IsPreviewCrawler && link.HasOpenGraph && outcome != AccessOutcome.Expired)
        {
            Record(link, client, request, now);
            return new(ResolveKind.Preview, Preview: new(link.Slug, link.OgTitle, link.OgDescription, link.OgImageUrl, link.DestinationUrl));
        }

        switch (outcome)
        {
            case AccessOutcome.Expired:
                return new(ResolveKind.Expired, link.FallbackUrl);
            case AccessOutcome.Locked:
                return new(ResolveKind.Locked);
        }

        return await PassAsync(link, client, request, now, ct);
    }

    /// <summary>Checks the password for a locked link and, when correct, returns the destination to send the visitor to.</summary>
    public async Task<Result<string>> UnlockAsync(string slug, string? password, VisitorRequest request, CancellationToken ct)
    {
        var link = await links.GetSnapshotAsync(SlugRules.Key(slug), ct);
        if (link is null) return Error.NotFound("Link not found.");

        var now = time.GetUtcNow();
        if (gate.Evaluate(link, now) == AccessOutcome.Expired) return new Error("expired", "This link has expired.");

        if (link.HasPassword && !gate.VerifyPassword(link, password))
            return Error.Unauthorized("Incorrect password.");

        var result = await PassAsync(link, classifier.Classify(request.UserAgent), request, now, ct);
        return result.Kind == ResolveKind.Redirect
            ? Result<string>.Ok(result.Url!)
            : new Error("expired", "This link has expired.");
    }

    private async Task<ResolveResult> PassAsync(
        LinkSnapshot link, ClientProfile client, VisitorRequest request, DateTimeOffset now, CancellationToken ct)
    {
        // Click limits are authoritative in PostgreSQL; bots never use up a visitor's click.
        if (link.MaxClicks is not null && !client.IsBot && !await links.TryConsumeClickAsync(link.Id, ct))
            return new(ResolveKind.Expired, link.FallbackUrl);

        var location = Locate(request);
        var destination = RoutingEvaluator.Choose(link, client.Platform, location.Country);
        Record(link, client, request, now, location);
        return new(ResolveKind.Redirect, destination);
    }

    private void Record(LinkSnapshot link, ClientProfile client, VisitorRequest request, DateTimeOffset now, GeoLocation? known = null)
    {
        var location = known ?? Locate(request);
        clicks.TryRecord(new ClickEvent(
            link.Id,
            now.UtcDateTime,
            VisitorHash(request, now),
            ReferrerHost(request.Referer),
            location.Country,
            location.City,
            client.Os,
            client.Browser,
            client.DeviceType,
            client.IsBot));
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
