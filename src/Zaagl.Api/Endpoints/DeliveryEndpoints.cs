using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Zaagl.Api.Infrastructure;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.Modules.Snippets.Contracts;
using Zaagl.SharedKernel;

namespace Zaagl.Api.Endpoints;

/// <summary>
/// H4 (ARC-008 §4, ARC-010 §7): the authorize hop behind <c>usercontent.zaa.gl/d/&lt;ticket&gt;</c>. It is reachable only
/// with the user-content Host (HostRouting) and is anonymous: credentials and cookies are ignored. For a snippet the hop
/// answers itself from PostgreSQL (no X-Accel-Redirect): the UTF-8 bytes exactly as stored, as an attachment. The
/// addendum §4.4 headers are applied by <see cref="HostHeaderPolicy"/> (and Nginx in front).
/// </summary>
public static class DeliveryEndpoints
{
    public const string TextPlain = "text/plain; charset=utf-8";

    public static void MapDelivery(this IEndpointRouteBuilder app)
    {
        app.MapMethods($"{HostRoutingDecision.DeliveryPrefix}/{{ticket}}", ["GET", "HEAD"], ServeAsync)
            .WithTags("Delivery").AllowAnonymous().ExcludeFromDescription();

        // Development only (no Nginx): the ticket URL path /d/<ticket> on the user-content host, never elsewhere.
        if (app.ServiceProvider.GetRequiredService<IOptions<HostRoutingOptions>>().Value.RewriteDeliveryPath)
        {
            app.MapMethods("/d/{ticket}", ["GET", "HEAD"], (
                    string ticket, HttpContext http, IDownloadGrants grants, IResourceRegistry registry, ILinkSnapshots links,
                    ISnippetReader reader, ObjectDelivery objectsDelivery, IOptions<HostRoutingOptions> hosts, CancellationToken ct) =>
                    HostRoutingDecision.IsUserContentHost(http.Request.Host.Host, hosts.Value)
                        ? ServeAsync(ticket, http, grants, registry, links, reader, objectsDelivery, ct)
                        : Task.FromResult(ApiResults.NotFound()))
                .WithTags("Delivery").AllowAnonymous().ExcludeFromDescription();
        }
    }

    private static async Task<IResult> ServeAsync(
        string ticket, HttpContext http, IDownloadGrants grants, IResourceRegistry registry, ILinkSnapshots links,
        ISnippetReader reader, ObjectDelivery objectsDelivery, CancellationToken ct)
    {
                var isHead = HttpMethods.IsHead(http.Request.Method)
                    || string.Equals(http.Request.Headers["X-Zaagl-Method"].ToString(), "HEAD", StringComparison.OrdinalIgnoreCase);
                var decision = await grants.AuthorizeAsync(ticket, isHead, ct);

                switch (decision.Outcome)
                {
                    case DeliveryOutcome.TicketInvalid: return ApiResults.Problem(Error.Of(ErrorCodes.TicketInvalid, "The link is no longer valid."));
                    case DeliveryOutcome.Burned: return ApiResults.Problem(Error.Of(ErrorCodes.Burned, "This content is gone."));
                    case DeliveryOutcome.Gone: return ApiResults.Problem(Error.Of(ErrorCodes.Gone, "This content is no longer available."));
                    case DeliveryOutcome.Processing: return ApiResults.Problem(Error.Of(ErrorCodes.Processing, "This content is not ready."));
                    case DeliveryOutcome.Rejected: return ApiResults.Problem(Error.Of(ErrorCodes.ContentRejected, "This content is not available."));
                    case DeliveryOutcome.Busy: return ObjectDelivery.Refusal(DeliveryOutcome.Busy, http);
                }

                var resource = await registry.GetAsync(decision.ResourceId!.Value, ct);
                // Stored objects (media S1, files S2): checked against the stored row and the file, then X-Accel or direct.
                if (resource is { Kind: ResourceKind.Media or ResourceKind.File })
                    return await objectsDelivery.ServeAsync(resource, decision, http, isHead, ct);
                if (resource is null || resource.Kind != ResourceKind.Snippet || resource.LinkId is not { } linkId
                    || !TryParseVersion(decision.Derivative, out var version))
                    return ApiResults.Problem(Error.Of(ErrorCodes.TicketInvalid, "The link is no longer valid."));

                var text = await reader.GetRawAsync(resource.ResourceId, version, ct);
                if (!text.IsSuccess) return ApiResults.Problem(Error.Of(ErrorCodes.Gone, "This content is no longer available."));
                var slug = (await links.GetSnapshotByIdAsync(linkId, ct))?.Slug ?? resource.PublicId;

                // Byte for byte what was stored (strict UTF-8 text: no trimming, no BOM or line-ending change).
                var bytes = new UTF8Encoding(false).GetBytes(text.Value!);
                var etag = new EntityTagHeaderValue($"\"{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}\"");
                var fileName = $"{slug}.txt";

                if (isHead)
                {
                    var headers = http.Response.Headers;
                    headers.ContentType = TextPlain;
                    headers.ContentLength = bytes.Length;
                    headers.ETag = etag.ToString();
                    headers.AcceptRanges = "bytes";
                    headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = fileName }.ToString();
                    return Results.Empty;
                }
                // A snippet is at most 2 MiB, so a single Range is served as 206 and 416 range-required never applies.
                return Results.Bytes(bytes, TextPlain, fileName, enableRangeProcessing: true, entityTag: etag);
    }

    /// <summary>The ticket derivative of a snippet is <c>v&lt;n&gt;</c>.</summary>
    internal static bool TryParseVersion(string? derivative, out int version)
    {
        version = 0;
        return derivative is { Length: > 1 } && derivative[0] == 'v'
            && int.TryParse(derivative.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out version)
            && version >= 1;
    }
}
