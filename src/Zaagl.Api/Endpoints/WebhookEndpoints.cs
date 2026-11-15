using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Zaagl.Api.Infrastructure;
using Zaagl.Modules.Billing.Application;
using Zaagl.Modules.DeveloperTools.Application;
using Zaagl.Modules.DeveloperTools.Contracts;
using Zaagl.Modules.Links.Application;
using Zaagl.SharedKernel;
using Zaagl.SharedKernel.Net;
using LinkOptions = Zaagl.Modules.Links.Application.LinkOptions;

namespace Zaagl.Api.Endpoints;

/// <summary>openapi HookCreateRequest / HookUpdateRequest (docs/phase2/webhook-catcher.md).</summary>
[System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.Strict)]
public sealed record HookCreateRequest(string? Label, int? ResponseStatus, int? LifetimeHours);

[System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.Strict)]
public sealed record HookUpdateRequest(string? Label, int? ResponseStatus, bool ClearLabel = false);

/// <summary>
/// Phase 2 E2.1 webhook catcher. Ingest <c>zaa.gl/hook/{token}[/{**path}]</c> lives on the short host (host routing sends any
/// <c>/hook/*</c> request on another host a 421); the inspector lives on <c>api.zaa.gl</c>, owner only.
/// </summary>
public static class WebhookEndpoints
{
    public static readonly string[] IngestMethods = ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Open live streams per user on this instance.</summary>
    private static readonly ConcurrentDictionary<Guid, int> OpenStreams = new();

    public static void MapWebhooks(this IEndpointRouteBuilder app)
    {
        // ---- Ingest (short host) ----
        app.MapMethods("/hook/{token}", IngestMethods, (string token, HttpContext http, CancellationToken ct) => IngestAsync(token, "", http, ct))
            .RequireRateLimiting("hook-ingest").ExcludeFromDescription();
        app.MapMethods("/hook/{token}/{**rest}", IngestMethods, (string token, string? rest, HttpContext http, CancellationToken ct) => IngestAsync(token, rest ?? "", http, ct))
            .RequireRateLimiting("hook-ingest").ExcludeFromDescription();

        // ---- Owner API ----
        var hooks = app.MapGroup("/api/hooks").WithTags("Webhook catcher");

        hooks.MapPost("", async (HookCreateRequest request, ClaimsPrincipal user, HttpContext http, HookService service, IOptions<LinkOptions> links, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                var created = await service.CreateAsync(user.GetUserId(), new CreateHookCommand(request.Label, request.ResponseStatus, request.LifetimeHours), ct);
                return ApiResults.From(created, row => Results.Created($"/api/hooks/{row.Id}", service.ToDto(row, links.Value.BaseUrl)));
            })
            .RequireScope(ApiScopes.ToolsWrite)
            .RequireRateLimiting("content-write")
            .WithSummary("Create a webhook catcher (accounts only). The response carries the ingest URL zaa.gl/hook/<token>.")
            .Produces<HookDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status402PaymentRequired)
            .ProducesProblem(StatusCodes.Status409Conflict);

        hooks.MapGet("", async (ClaimsPrincipal user, HttpContext http, HookService service, IOptions<LinkOptions> links, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                return Results.Ok((await service.ListAsync(user.GetUserId(), ct)).Select(h => service.ToDto(h, links.Value.BaseUrl)).ToList());
            })
            .RequireScope(ApiScopes.ResourcesRead)
            .WithSummary("The caller's catchers (live and read-only).")
            .Produces<List<HookDto>>();

        hooks.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, HttpContext http, HookService service, IOptions<LinkOptions> links, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                return ApiResults.From(await service.GetOwnedAsync(id, user.GetUserId(), ct), h => Results.Ok(service.ToDto(h, links.Value.BaseUrl)));
            })
            .RequireScope(ApiScopes.ResourcesRead)
            .Produces<HookDto>().ProducesProblem(StatusCodes.Status404NotFound);

        hooks.MapPatch("/{id:guid}", async (Guid id, HookUpdateRequest request, ClaimsPrincipal user, HttpContext http, HookService service, IOptions<LinkOptions> links, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                var command = new UpdateHookCommand(request.ClearLabel ? null : request.Label, request.ClearLabel || request.Label is not null, request.ResponseStatus);
                return ApiResults.From(await service.UpdateAsync(id, user.GetUserId(), command, ct), h => Results.Ok(service.ToDto(h, links.Value.BaseUrl)));
            })
            .RequireScope(ApiScopes.ToolsWrite)
            .WithSummary("Change the label or the canned response status (200-299, 400-599).")
            .Produces<HookDto>().ProducesProblem(StatusCodes.Status404NotFound);

        hooks.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, HttpContext http, HookService service, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                var deleted = await service.DeleteAsync(id, user.GetUserId(), ct);
                return deleted.IsSuccess ? Results.NoContent() : ApiResults.Problem(deleted.Error!);
            })
            .RequireScope(ApiScopes.ToolsWrite)
            .WithSummary("Delete a catcher and every captured request now.")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);

        hooks.MapGet("/{id:guid}/requests", async (Guid id, long? before, int? limit, ClaimsPrincipal user, HttpContext http, HookService service, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                return ApiResults.From(await service.ListRequestsAsync(id, user.GetUserId(), before, limit ?? 50, ct));
            })
            .RequireScope(ApiScopes.ResourcesRead)
            .WithSummary("Captured requests, newest first, without bodies. Page with ?before=<nextBefore>.")
            .Produces<HookRequestPage>().ProducesProblem(StatusCodes.Status404NotFound);

        hooks.MapGet("/{id:guid}/requests/{requestId:long}", async (Guid id, long requestId, ClaimsPrincipal user, HttpContext http, HookService service, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                return ApiResults.From(await service.GetRequestAsync(id, user.GetUserId(), requestId, ct));
            })
            .RequireScope(ApiScopes.ResourcesRead)
            .WithSummary("One captured request with headers and body (base64, plus text when it is valid UTF-8). Data only, never rendered.")
            .Produces<HookRequestDto>().ProducesProblem(StatusCodes.Status404NotFound);

        hooks.MapDelete("/{id:guid}/requests/{requestId:long}", async (Guid id, long requestId, ClaimsPrincipal user, HttpContext http, HookService service, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                var deleted = await service.DeleteRequestAsync(id, user.GetUserId(), requestId, ct);
                return deleted.IsSuccess ? Results.NoContent() : ApiResults.Problem(deleted.Error!);
            })
            .RequireScope(ApiScopes.ToolsWrite)
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);

        hooks.MapDelete("/{id:guid}/requests", async (Guid id, ClaimsPrincipal user, HttpContext http, HookService service, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                var deleted = await service.DeleteRequestAsync(id, user.GetUserId(), null, ct);
                return deleted.IsSuccess ? Results.NoContent() : ApiResults.Problem(deleted.Error!);
            })
            .RequireScope(ApiScopes.ToolsWrite)
            .WithSummary("Clear every captured request of a catcher.")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);

        hooks.MapGet("/{id:guid}/stream", StreamAsync)
            .RequireScope(ApiScopes.ResourcesRead)
            .WithSummary("Server-Sent Events: one 'request' event per new capture (id = request id, Last-Event-ID resumes), ': ping' every 15 s, 'closed' at expiry or delete.")
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream").ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }

    // ---------------------------------------------------------------------------------------------------
    // Ingest
    // ---------------------------------------------------------------------------------------------------

    private static async Task IngestAsync(string token, string rest, HttpContext http, CancellationToken ct)
    {
        var response = http.Response;
        response.OnStarting(() =>
        {
            response.Headers.CacheControl = "no-store";
            response.Headers.XContentTypeOptions = "nosniff";
            response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
            response.Headers["X-Robots-Tag"] = "noindex";
            response.Headers["Referrer-Policy"] = "no-referrer";
            return Task.CompletedTask;
        });

        var service = http.RequestServices.GetRequiredService<HookService>();
        var hook = await service.FindLiveAsync(token, ct); // syntax first, no I/O for a malformed token
        if (hook is null) { await PlainAsync(http, StatusCodes.Status404NotFound); return; }

        const long ceiling = ToolCeilings.HookBodyBytes;
        if (http.Request.ContentLength is > ceiling) { await PlainAsync(http, StatusCodes.Status413PayloadTooLarge); return; }

        // Stream the body: keep at most the catcher's cap, count the rest, refuse beyond the ingest ceiling.
        var keep = new MemoryStream();
        long total = 0;
        var buffer = new byte[16 * 1024];
        try
        {
            int read;
            while ((read = await http.Request.Body.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > ceiling) { await PlainAsync(http, StatusCodes.Status413PayloadTooLarge); return; }
                var room = hook.MaxBodyBytes - (int)keep.Length;
                if (room > 0) keep.Write(buffer, 0, Math.Min(room, read));
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            await PlainAsync(http, StatusCodes.Status413PayloadTooLarge);
            return;
        }

        var guestKey = http.RequestServices.GetRequiredService<GuestKeyProvider>();
        var partition = IpPartition.Key(http.Connection.RemoteIpAddress);
        var fingerprint = Convert.ToHexStringLower(GuestIdentity.DeriveKey(guestKey.Key, "hook-sender:" + partition)[..8]);
        var query = http.Request.QueryString.HasValue ? http.Request.QueryString.Value![1..] : "";
        var headers = HookCapture.Headers(http.Request.Headers.SelectMany(h => h.Value.Select(v => new KeyValuePair<string, string>(h.Key, v ?? ""))));
        var captured = new CapturedRequest(
            HookCapture.Clean(http.Request.Method, HookCapture.MaxMethodLength),
            HookCapture.Clean("/" + rest, HookCapture.MaxPathLength),
            HookCapture.Clean(query, HookCapture.MaxQueryLength),
            headers,
            string.IsNullOrEmpty(http.Request.ContentType) ? null : HookCapture.Clean(http.Request.ContentType, 256),
            keep.ToArray(), total, total > keep.Length, fingerprint);

        var result = await service.IngestAsync(hook.Id, captured, ct);
        switch (result.Outcome)
        {
            case IngestOutcome.Stored:
                await PlainAsync(http, result.ResponseStatus);
                break;
            case IngestOutcome.RateLimited or IngestOutcome.Busy:
                response.Headers.RetryAfter = result.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await PlainAsync(http, result.Outcome == IngestOutcome.Busy ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status429TooManyRequests);
                break;
            default:
                await PlainAsync(http, StatusCodes.Status404NotFound);
                break;
        }
    }

    /// <summary>The fixed, inert answer: a status with an empty <c>text/plain</c> body. Never configurable (no zaa.gl hosting, no redirects).</summary>
    private static Task PlainAsync(HttpContext http, int status)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "text/plain; charset=utf-8";
        http.Response.ContentLength = 0;
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------------------------------------------
    // Live stream (SSE)
    // ---------------------------------------------------------------------------------------------------

    private static async Task StreamAsync(Guid id, ClaimsPrincipal user, HttpContext http, HookService service, TimeProvider time,
        IOptions<HookOptions> options, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "private, no-store";
        var userId = user.GetUserId();
        var owned = await service.GetOwnedAsync(id, userId, ct);
        if (!owned.IsSuccess) { await ApiResults.Problem(owned.Error!).ExecuteAsync(http); return; }

        var max = Math.Max(1, options.Value.MaxStreamsPerUser);
        if (OpenStreams.AddOrUpdate(userId, 1, (_, n) => n + 1) > max)
        {
            OpenStreams.AddOrUpdate(userId, 0, (_, n) => n - 1);
            await ApiResults.Problem(Error.TooManyRequests($"At most {max} live streams at a time.")).ExecuteAsync(http);
            return;
        }
        try
        {
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();

            long last = long.TryParse(http.Request.Headers["Last-Event-ID"].ToString(), out var resume) && resume >= 0
                ? resume
                : await service.LatestRequestIdAsync(id, ct);
            await WriteAsync(http, $"retry: 3000\n: stream open\n\n", ct);

            var started = time.GetUtcNow();
            var lastPing = started;
            var deadline = started.AddMinutes(Math.Max(1, options.Value.StreamMaxMinutes));
            while (!ct.IsCancellationRequested && time.GetUtcNow() < deadline)
            {
                var hook = await service.GetOwnedAsync(id, userId, ct);
                if (!hook.IsSuccess || hook.Value!.ExpiresAt <= time.GetUtcNow().UtcDateTime)
                {
                    await WriteAsync(http, "event: closed\ndata: {}\n\n", ct);
                    return;
                }
                foreach (var item in await service.RequestsAfterAsync(id, last, ct))
                {
                    last = item.Id;
                    await WriteAsync(http, $"id: {item.Id}\nevent: request\ndata: {JsonSerializer.Serialize(item, Json)}\n\n", ct);
                }
                if (time.GetUtcNow() - lastPing >= TimeSpan.FromSeconds(15))
                {
                    lastPing = time.GetUtcNow();
                    await WriteAsync(http, ": ping\n\n", ct);
                }
                await Task.Delay(TimeSpan.FromSeconds(1), time, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client went away.
        }
        finally
        {
            OpenStreams.AddOrUpdate(userId, 0, (_, n) => Math.Max(0, n - 1));
        }
    }

    private static async Task WriteAsync(HttpContext http, string text, CancellationToken ct)
    {
        await http.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
        await http.Response.Body.FlushAsync(ct);
    }
}
