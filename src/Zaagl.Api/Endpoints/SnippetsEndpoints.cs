using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Zaagl.Api.Infrastructure;
using Zaagl.Modules.Identity.Contracts;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Links.Infrastructure;
using Zaagl.Modules.Billing.Contracts;
using Microsoft.Net.Http.Headers;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.Modules.Snippets.Application;
using Zaagl.Modules.Snippets.Contracts;
using Zaagl.SharedKernel;
using Zaagl.SharedKernel.Net;

namespace Zaagl.Api.Endpoints;

/// <summary>openapi SnippetCreateRequest.</summary>
public sealed record SnippetCreateRequest(
    string? Content,
    string? Language,
    SnippetRenderMode? RenderMode,
    string? Title,
    DateTime? ExpiresAt,
    string? Password,
    string? CustomSlug,
    bool Public = false);

/// <summary>openapi SnippetUpdateRequest.</summary>
public sealed record SnippetUpdateRequest(string? Content, string? Language, SnippetRenderMode? RenderMode, int BaseVersion);

/// <summary>openapi OwnerSnippetDto: ResourceDetail plus the current version. <c>current</c> and <c>rawUrl</c> are null
/// unless the state is <c>ready</c>.</summary>
public sealed record OwnerSnippetDto(
    Guid Id,
    string Kind,
    string? Title,
    string Code,
    string ShortUrl,
    string State,
    string ProcessingStatus,
    long SizeBytes,
    DateTime ExpiresAt,
    DateTime? PurgeAfter,
    bool BurnAfterDownload,
    bool IsPublic,
    long DownloadCount,
    DateTime CreatedAt,
    string? RejectedReason,
    bool HasPassword,
    DateTime? BurnClaimedAt,
    string? LinkKey,
    SnippetVersionDto? Snippet,
    int VersionCount,
    SnippetViewDto? Current,
    string? RawUrl);

public static class SnippetsEndpoints
{
    /// <summary>JSON escaping can make a text up to 6 times larger than its UTF-8 size; the precise cap (plan, in UTF-8
    /// bytes of the decoded text) is checked by the service. 2 MiB x 6.</summary>
    private const long MaxJsonBodyBytes = 12 * 1024 * 1024;

    public static void MapSnippets(this IEndpointRouteBuilder app)
    {
        var snippets = app.MapGroup("/api/snippets").WithTags("Snippets");

        // POST /api/snippets: J, K(snippets:write) or a guest (no credential). ARC-010 §4.1 order of refusals:
        // failing credential 401 -> guest CAPTCHA 401 -> 400 -> idempotency 422/409 -> 413/402/400 -> 429 -> 403 -> 409.
        snippets.MapPost("", async (
                SnippetCreateRequest request, ClaimsPrincipal user, HttpContext http, SnippetService service,
                GuestKeyProvider guestKey, GuestRateLimiter guestLimiter, IOptions<GuestOptions> guestOptions,
                ICaptchaVerifier captcha, IConfiguration config, CancellationToken ct) =>
            {
                var idempotencyKey = IdempotencyKeyFrom(http.Request);
                if (idempotencyKey is null)
                    return ApiResults.Problem(Error.Validation("The Idempotency-Key header is required (1-128 printable ASCII characters)."));

                SnippetActor actor;
                if (user.Identity?.IsAuthenticated == true)
                {
                    actor = new SnippetActor(user.GetUserId(), null, CreatedViaFor(user));
                }
                else
                {
                    var opts = guestOptions.Value;
                    if (!opts.Enabled) return ApiResults.Problem(Error.ServiceUnavailable("Guest snippet creation is currently disabled."));

                    var captchaToken = http.Request.Headers["Captcha-Token"].ToString();
                    var frontendHost = Uri.TryCreate(config["Frontend:BaseUrl"], UriKind.Absolute, out var frontendUri) ? frontendUri.Host : "";
                    var verified = await captcha.VerifyAsync(string.IsNullOrEmpty(captchaToken) ? null : captchaToken, "guest-snippet", frontendHost, ct);
                    if (!verified.IsSuccess) return ApiResults.Problem(verified.Error!);

                    // The in-memory brakes of spec §7.12. The PD-14 counters are below.
                    var partition = IpPartition.Key(http.Connection.RemoteIpAddress);
                    if (!guestLimiter.TryGlobalCeiling(partition, opts.GlobalPerHour))
                        return ApiResults.Problem(new Error(ErrorCodes.GuestCreationPaused, "Guest creation is temporarily paused for this network. Try again shortly."));
                    if (!guestLimiter.TryPerMinute(partition, opts.PerMinute))
                        return ApiResults.Problem(Error.TooManyRequests("Too many requests. Try again in a minute."));

                    // PD-14 counters (BE-026): 30 per hour and 60 per 24 h per IP partition, shared with uploads.
                    // Counted by the service after the plan checks, refunded when the creation then fails.
                    var linksDb = http.RequestServices.GetRequiredService<LinksDbContext>();
                    var clock = http.RequestServices.GetRequiredService<TimeProvider>();
                    var counterKey = GuestContentQuota.PrefixKey(guestKey.Key, partition);
                    DateTime consumedAt = default;
                    var admission = new GuestAdmission(
                        async token =>
                        {
                            consumedAt = clock.GetUtcNow().UtcDateTime;
                            return await GuestContentQuota.TryConsumeAsync(linksDb, counterKey, opts.ContentPerHour, opts.ContentPerDay, consumedAt, token);
                        },
                        () => GuestContentQuota.RefundAsync(linksDb, counterKey, consumedAt));
                    actor = new SnippetActor(null, GuestIdentity.DeriveKey(guestKey.Key, partition), CreatedVia.Web, admission);
                }

                var command = new CreateSnippetCommand(
                    request.Content ?? "", request.Language, request.RenderMode ?? SnippetRenderMode.Code, request.Title,
                    request.ExpiresAt, request.Password, request.CustomSlug, request.Public);
                var created = await service.CreateAsync(actor, command, idempotencyKey, ct);
                return ApiResults.From(created, dto => Results.Created($"/api/snippets/{dto.ResourceId}", dto));
            })
            .AllowAnonymous()
            .RejectFailedCredentials() // PD-36: a failing credential is a 401, never a guest snippet
            .RequireScopeWhenAuthenticated(ApiScopes.SnippetsWrite)
            .RequireRateLimiting("content-write")
            .WithRequestBodyLimit(MaxJsonBodyBytes);

        snippets.MapGet("/{id:guid}", async (
                Guid id, ClaimsPrincipal user, HttpContext http, SnippetService service, ISnippetReader reader,
                IDownloadGrants grants, IOptions<Zaagl.Modules.Links.Application.LinkOptions> linkOptions, IResourceRegistry registry, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                var owned = await service.ResolveOwnedAsync(id, user.GetUserId(), ct);
                if (!owned.IsSuccess) return ApiResults.Problem(owned.Error!);
                var o = owned.Value!;

                var current = await service.GetCurrentVersionAsync(id, ct);
                SnippetViewDto? view = null;
                string? rawUrl = null;
                if (o.State == ResourceState.Ready && current is not null)
                {
                    view = await reader.GetViewAsync(id, null, ct);
                    var ticket = await grants.MintAsync(new MintTicketRequest(id, DeliveryPurpose.Owner, $"v{current.Version}"), ct);
                    rawUrl = ticket.IsSuccess ? ticket.Value!.Url : null;
                }

                var r = o.Resource;
                var slug = o.Link?.Slug ?? "";
                var dto = new OwnerSnippetDto(
                    r.ResourceId, "snippet", r.Title, slug, $"{linkOptions.Value.BaseUrl.TrimEnd('/')}/{slug}",
                    Lower(o.State), Lower(r.Processing), r.SizeBytes, o.Link?.ExpiresAt ?? r.ExpiresAt, o.Link?.PurgeAfter ?? r.PurgeAfter,
                    false, o.Link is { LinkKeyHash: null }, r.DownloadCount, r.CreatedAt, r.RejectedReason,
                    o.Link?.HasPassword ?? false, r.BurnClaimedAt, null, current, await service.VersionCountAsync(id, ct), view, rawUrl);
                return Results.Ok(dto);
            })
            .RequireScope(ApiScopes.ResourcesRead);

        snippets.MapPut("/{id:guid}", async (
                Guid id, SnippetUpdateRequest request, ClaimsPrincipal user, HttpContext http, SnippetService service, CancellationToken ct) =>
            {
                var key = IdempotencyKeyFrom(http.Request);
                if (http.Request.Headers.ContainsKey("Idempotency-Key") && key is null)
                    return ApiResults.Problem(Error.Validation("The Idempotency-Key header must be 1-128 printable ASCII characters."));
                var updated = await service.UpdateAsync(id, user.GetUserId(),
                    new SnippetUpdateCommand(request.Content ?? "", request.Language, request.RenderMode ?? SnippetRenderMode.Code, request.BaseVersion),
                    key, CreatedViaFor(user), ct);
                return ApiResults.From(updated);
            })
            .RequireScope(ApiScopes.SnippetsWrite)
            .RequireRateLimiting("content-write")
            .WithRequestBodyLimit(MaxJsonBodyBytes);

        snippets.MapGet("/{id:guid}/versions", async (Guid id, ClaimsPrincipal user, HttpContext http, SnippetService service, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                var owned = await service.ResolveOwnedAsync(id, user.GetUserId(), ct);
                if (!owned.IsSuccess) return ApiResults.Problem(owned.Error!);
                return Results.Ok(await service.ListVersionsAsync(id, ct));
            })
            .RequireScope(ApiScopes.ResourcesRead);

        // BE-009 (ARC-010 §10): owner diff of two versions, text only. Ownership first (PD-40), then the state, then input.
        snippets.MapGet("/{id:guid}/diff", async (
                Guid id, [Microsoft.AspNetCore.Mvc.FromQuery(Name = "from")] int? fromVersion, [Microsoft.AspNetCore.Mvc.FromQuery(Name = "to")] int? toVersion,
                bool? ignoreWhitespace, ClaimsPrincipal user, HttpContext http, SnippetService service, CancellationToken ct) =>
            {
                var owned = await service.ResolveOwnedAsync(id, user.GetUserId(), ct);
                if (!owned.IsSuccess) return ApiResults.Problem(owned.Error!);
                if (SnippetService.StateError(owned.Value!) is { } stateError) return ApiResults.Problem(stateError);
                if (fromVersion is null || toVersion is null) return ApiResults.Problem(Error.Validation("Both 'from' and 'to' are required."));

                var ws = ignoreWhitespace == true ? 1 : 0;
                var etag = $"\"d-{fromVersion}-{toVersion}-{ws}\"";
                var diff = await service.DiffAsync(id, fromVersion.Value, toVersion.Value, ws == 1, ct);
                if (!diff.IsSuccess) return ApiResults.Problem(diff.Error!);

                // Versions are immutable, so the diff of two of them is too.
                http.Response.Headers.ETag = etag;
                http.Response.Headers.CacheControl = "private, max-age=3600";
                if (http.Request.Headers.IfNoneMatch.ToString() == etag) return Results.StatusCode(StatusCodes.Status304NotModified);
                return Results.Ok(diff.Value);
            })
            .RequireScope(ApiScopes.ResourcesRead);

        // DELETE /api/snippets/{id}: the link becomes 410, text and quota are kept until purge (PD-55). Idempotent.
        snippets.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, SnippetService service, CancellationToken ct) =>
                ApiResults.From(await service.DeleteAsync(id, user.GetUserId(), ct), _ => Results.NoContent()))
            .RequireScope(ApiScopes.SnippetsWrite)
            .RequireRateLimiting("content-write");

        // BE-010 (ARC-010 §4.4): CLI text ingest. No guest path. UTF-8, or UTF-16 (declared or by BOM). The body is read
        // in a counting loop that stops one byte past the limit, never buffering more.
        snippets.MapPost("/raw", async (
                ClaimsPrincipal user, HttpContext http, SnippetService service, IContentPlanProvider plans, CancellationToken ct) =>
            {
                var request = http.Request;
                if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
                    || !string.Equals(mediaType.MediaType.Value, "text/plain", StringComparison.OrdinalIgnoreCase))
                    return ApiResults.Problem(Error.Of(ErrorCodes.UnsupportedMediaType, "Send the text as Content-Type: text/plain (UTF-8 or UTF-16)."));

                var key = IdempotencyKeyFrom(request);
                if (request.Headers.ContainsKey("Idempotency-Key") && key is null)
                    return ApiResults.Problem(Error.Validation("The Idempotency-Key header must be 1-128 printable ASCII characters."));

                var query = request.Query;
                var renderMode = SnippetRenderMode.Code;
                if (query.TryGetValue("renderMode", out var rm) && !Enum.TryParse(rm.ToString(), ignoreCase: true, out renderMode))
                    return ApiResults.Problem(Error.Validation("Unknown render mode."));
                var ansi = query.TryGetValue("ansi", out var ansiValue) ? ansiValue.ToString().ToLowerInvariant() : "strip";
                if (ansi is not ("keep" or "strip")) return ApiResults.Problem(Error.Validation("ansi must be keep or strip."));
                DateTime? expiresAt = null;
                if (query.TryGetValue("expiresAt", out var exp))
                {
                    if (!DateTime.TryParse(exp.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                        return ApiResults.Problem(Error.Validation("expiresAt is not a valid date-time."));
                    expiresAt = parsed;
                }
                var isPublic = false;
                if (query.TryGetValue("public", out var pub) && !bool.TryParse(pub.ToString(), out isPublic))
                    return ApiResults.Problem(Error.Validation("public must be true or false."));

                // Received bytes may be up to twice the UTF-8 cap (UTF-16); the exact cap applies to the decoded UTF-8 size.
                var plan = await plans.GetForUserAsync(user.GetUserId(), ct);
                var cap = plan.Limits.MaxSnippetBytes;
                var readLimit = 2L * cap + 2;
                if (request.ContentLength is { } declared && declared > readLimit)
                    return ApiResults.Problem(Error.Of(ErrorCodes.SnippetTooLarge, $"A snippet can be at most {cap} bytes on your plan."));

                using var buffer = new MemoryStream();
                var chunk = new byte[16 * 1024];
                int read;
                while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
                {
                    if (buffer.Length + read > readLimit)
                        return ApiResults.Problem(Error.Of(ErrorCodes.SnippetTooLarge, $"A snippet can be at most {cap} bytes on your plan."));
                    buffer.Write(chunk, 0, read);
                }

                var decoded = SnippetText.DecodeIngest(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), mediaType.Charset.Value);
                if (!decoded.IsSuccess) return ApiResults.Problem(decoded.Error!);
                var text = decoded.Value!;
                if (renderMode == SnippetRenderMode.Terminal && ansi == "strip") text = SnippetText.StripAnsi(text);

                var command = new CreateSnippetCommand(
                    text, query["lang"].FirstOrDefault(), renderMode, query["title"].FirstOrDefault(), expiresAt, null,
                    query["customSlug"].FirstOrDefault(), isPublic);
                var actor = new SnippetActor(user.GetUserId(), null, CreatedViaFor(user));
                var created = await service.CreateAsync(actor, command, key ?? $"raw-{Guid.NewGuid():N}", ct);
                if (!created.IsSuccess) return ApiResults.Problem(created.Error!);

                var dto = created.Value!;
                var accept = request.Headers.Accept.ToString();
                if (accept.Contains("text/plain", StringComparison.OrdinalIgnoreCase) && !accept.Contains("json", StringComparison.OrdinalIgnoreCase))
                {
                    // Exactly one line, so a pipe into a clipboard tool works. The key is a secret: never cached.
                    http.Response.Headers.CacheControl = "private, no-store";
                    return Results.Text(dto.ShortUrl + (dto.LinkKey is null ? "" : "#k=" + dto.LinkKey) + "\n", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status201Created);
                }
                return Results.Created($"/api/snippets/{dto.ResourceId}", dto);
            })
            .RequireScope(ApiScopes.SnippetsWrite)
            .RequireRateLimiting("content-write")
            .WithRequestBodyLimit(4L * 1024 * 1024 + 16);

        snippets.MapGet("/{id:guid}/versions/{n:int}", async (
                Guid id, int n, ClaimsPrincipal user, HttpContext http, SnippetService service, ISnippetReader reader, CancellationToken ct) =>
            {
                var owned = await service.ResolveOwnedAsync(id, user.GetUserId(), ct);
                if (!owned.IsSuccess) return ApiResults.Problem(owned.Error!);
                if (SnippetService.StateError(owned.Value!) is { } stateError) return ApiResults.Problem(stateError);
                var view = await reader.GetViewAsync(id, n, ct);
                if (view is null) return ApiResults.NotFound();

                // A version is immutable: the ETag is the hex SHA-256 of its UTF-8 text and clients may cache it.
                http.Response.Headers.ETag = $"\"{Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(view.Content))).ToLowerInvariant()}\"";
                http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
                return Results.Ok(view);
            })
            .RequireScope(ApiScopes.ResourcesRead);
    }

    /// <summary>1-128 printable ASCII (spec §7.2); null when missing or malformed.</summary>
    internal static string? IdempotencyKeyFrom(HttpRequest request)
    {
        var key = request.Headers["Idempotency-Key"].ToString();
        return key.Length is >= 1 and <= 128 && key.All(c => c is >= '!' and <= '~') ? key : null;
    }

    /// <summary>ARC-010 §4.1: derived from the credential, never sent by clients.</summary>
    internal static CreatedVia CreatedViaFor(ClaimsPrincipal user)
    {
        if (ApiScopes.IsApiKey(user)) return CreatedVia.Api;
        foreach (var clientId in user.FindAll(NativeClientClaims.ClientId).Select(c => c.Value))
        {
            if (clientId == NativeClientIds.Cli) return CreatedVia.Cli;
            if (clientId == NativeClientIds.Desktop) return CreatedVia.Desktop;
        }
        return CreatedVia.Web;
    }

    internal static string Lower<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();
}
