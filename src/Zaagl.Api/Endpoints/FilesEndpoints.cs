using System.Globalization;
using System.Security.Claims;
using Zaagl.Api.Infrastructure;
using Zaagl.Modules.Files.Application;
using Zaagl.Modules.Files.Contracts;
using Zaagl.Modules.Files.Infrastructure;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.SharedKernel;

namespace Zaagl.Api.Endpoints;

/// <summary>
/// The tus 1.0.0 endpoints at <c>/api/uploads</c> (ARC-009 section 4, openapi Uploads): OPTIONS, POST (creation), HEAD, PATCH,
/// DELETE (termination) and <c>POST /{id}/complete</c>. Hand-written: the wire contract is the contract. Every response carries
/// <c>Tus-Resumable</c>. Slice 1: accounts only; no credential answers <c>503 guest_creation_paused</c> on create and 404 on
/// a session route (no guest can own one). Browsers reach these through the BFF; the session id is a capability and the
/// principal must match its creator.
/// </summary>
public static class FilesEndpoints
{
    public const string TusResumable = "1.0.0";
    private const string OffsetContentType = "application/offset+octet-stream";

    public static void MapFiles(this IEndpointRouteBuilder app)
    {
        var uploads = app.MapGroup("/api/uploads").WithTags("Uploads");

        // OPTIONS: capability discovery, anonymous.
        uploads.MapMethods("", ["OPTIONS"], async (ClaimsPrincipal user, HttpContext http, UploadService service, CancellationToken ct) =>
            {
                Tus(http);
                var userId = user.Identity?.IsAuthenticated == true ? user.GetUserId() : (Guid?)null;
                http.Response.Headers["Tus-Version"] = TusResumable;
                http.Response.Headers["Tus-Extension"] = "creation,termination,expiration";
                http.Response.Headers["Tus-Max-Size"] = (await service.MaxSizeForAsync(userId, ct)).ToString(CultureInfo.InvariantCulture);
                return Results.StatusCode(StatusCodes.Status204NoContent);
            })
            .AllowAnonymous();

        // POST: create. Size, plan, disk and quota are all checked before any byte is accepted.
        uploads.MapPost("", async (
                ClaimsPrincipal user, HttpContext http, UploadService service, DiskAdmission admission, CancellationToken ct) =>
            {
                Tus(http);
                if (TusVersionMismatch(http)) return PreconditionFailed();

                var request = http.Request;
                var lengthHeader = request.Headers["Upload-Length"].ToString();
                long? length = null;
                if (lengthHeader.Length > 0)
                {
                    if (!long.TryParse(lengthHeader, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                        return Problem(http, Error.Validation("Upload-Length must be a non-negative integer."), admission);
                    length = parsed;
                }
                var defer = request.Headers.ContainsKey("Upload-Defer-Length");

                string? idempotencyKey = null;
                if (request.Headers.ContainsKey("Idempotency-Key"))
                {
                    idempotencyKey = SnippetsEndpoints.IdempotencyKeyFrom(request);
                    if (idempotencyKey is null)
                        return Problem(http, Error.Validation("The Idempotency-Key header must be 1-128 printable ASCII characters."), admission);
                }

                var (actor, refusal) = await UploadActors.ResolveAsync(http, user, creating: true, ct);
                if (refusal is not null) return refusal;
                var created = await service.CreateAsync(
                    actor, length, defer, request.Headers["Upload-Metadata"].ToString() is { Length: > 0 } m ? m : null, idempotencyKey, ct);
                if (!created.IsSuccess) return Problem(http, created.Error!, admission);

                http.Response.Headers.Location = $"/api/uploads/{created.Value!.Id}";
                http.Response.Headers["Upload-Expires"] = HttpDate(created.Value.ExpiresAt);
                return Results.StatusCode(StatusCodes.Status201Created);
            })
            .AllowAnonymous()
            .RejectFailedCredentials() // PD-36: a failing credential is a 401, never a guest upload
            .RequireScopeWhenAuthenticated(ApiScopes.FilesWrite)
            .RequireRateLimiting("content-write");

        // HEAD: the row's offset (the file may be longer after a crash; the next PATCH cuts it back).
        uploads.MapMethods("/{id:guid}", ["HEAD"], async (
                Guid id, ClaimsPrincipal user, HttpContext http, UploadService service, DiskAdmission admission, CancellationToken ct) =>
            {
                Tus(http);
                if (TusVersionMismatch(http)) return PreconditionFailed();
                var info = await service.GetOffsetAsync((await UploadActors.ResolveAsync(http, user, false, ct)).Actor, id, ct);
                if (!info.IsSuccess) return Problem(http, info.Error!, admission);
                http.Response.Headers["Upload-Offset"] = info.Value!.Offset.ToString(CultureInfo.InvariantCulture);
                http.Response.Headers["Upload-Length"] = info.Value.Length.ToString(CultureInfo.InvariantCulture);
                http.Response.Headers["Upload-Expires"] = HttpDate(info.Value.ExpiresAt);
                http.Response.Headers.CacheControl = "no-store";
                return Results.StatusCode(StatusCodes.Status200OK);
            })
            .AllowAnonymous()
            .RejectFailedCredentials()
            .RequireScopeWhenAuthenticated(ApiScopes.FilesWrite);

        // PATCH: append one chunk of at most 90,000,000 bytes (Kestrel limit raised for this route only).
        uploads.MapPatch("/{id:guid}", async (
                Guid id, ClaimsPrincipal user, HttpContext http, UploadService service, DiskAdmission admission, CancellationToken ct) =>
            {
                Tus(http);
                if (TusVersionMismatch(http)) return PreconditionFailed();
                RaiseBodyLimits(http);
                var request = http.Request;
                if (request.ContentType is null || !request.ContentType.StartsWith(OffsetContentType, StringComparison.OrdinalIgnoreCase))
                    return Problem(http, Error.Of(ErrorCodes.UnsupportedMediaType, $"Send the chunk as Content-Type: {OffsetContentType}."), admission);
                if (!long.TryParse(request.Headers["Upload-Offset"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
                    return Problem(http, Error.Validation("Upload-Offset is required."), admission);

                try
                {
                    var result = await service.AppendAsync((await UploadActors.ResolveAsync(http, user, false, ct)).Actor, id, offset, request.ContentLength, request.Body, ct);
                    if (!result.IsSuccess) return Problem(http, result.Error!, admission);
                    http.Response.Headers["Upload-Offset"] = result.Value!.Offset.ToString(CultureInfo.InvariantCulture);
                    http.Response.Headers["Upload-Expires"] = HttpDate(result.Value.ExpiresAt);
                    return Results.StatusCode(StatusCodes.Status204NoContent);
                }
                catch (Microsoft.AspNetCore.Http.BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
                {
                    return Problem(http, Error.Of(ErrorCodes.ChunkTooLarge,
                        $"A chunk is at most {UploadProtocol.MaxChunkBytes} bytes."), admission);
                }
            })
            .AllowAnonymous()
            .RejectFailedCredentials()
            .RequireScopeWhenAuthenticated(ApiScopes.FilesWrite)
            .WithRequestBodyLimit(UploadProtocol.MaxChunkBytes);

        // DELETE: tus termination. Temp bytes deleted, reservation released.
        uploads.MapDelete("/{id:guid}", async (
                Guid id, ClaimsPrincipal user, HttpContext http, UploadService service, DiskAdmission admission, CancellationToken ct) =>
            {
                Tus(http);
                if (TusVersionMismatch(http)) return PreconditionFailed();
                var aborted = await service.AbortAsync((await UploadActors.ResolveAsync(http, user, false, ct)).Actor, id, ct);
                return aborted.IsSuccess ? Results.StatusCode(StatusCodes.Status204NoContent) : Problem(http, aborted.Error!, admission);
            })
            .AllowAnonymous()
            .RejectFailedCredentials()
            .RequireScopeWhenAuthenticated(ApiScopes.FilesWrite);

        // POST /{id}/complete: sniff, kind, Register (adopting the reservation), link, scan task. 202 processing.
        uploads.MapPost("/{id:guid}/complete", async (
                Guid id, CompleteUploadRequest? body, ClaimsPrincipal user, HttpContext http, UploadService service, DiskAdmission admission,
                CancellationToken ct) =>
            {
                Tus(http);
                var idempotencyKey = SnippetsEndpoints.IdempotencyKeyFrom(http.Request);
                if (idempotencyKey is null)
                    return Problem(http, Error.Validation("The Idempotency-Key header is required (1-128 printable ASCII characters)."), admission);
                var completed = await service.CompleteAsync((await UploadActors.ResolveAsync(http, user, false, ct)).Actor, id, body ?? new CompleteUploadRequest(null, null, false, null, null, false), idempotencyKey, ct);
                if (!completed.IsSuccess) return Problem(http, completed.Error!, admission);
                http.Response.Headers.CacheControl = "private, no-store"; // the answer may carry the one-time link key
                return Results.Json(completed.Value, statusCode: StatusCodes.Status202Accepted);
            })
            .AllowAnonymous()
            .RejectFailedCredentials()
            .RequireScopeWhenAuthenticated(ApiScopes.FilesWrite)
            .RequireRateLimiting("content-write");

        // POST /simple (ARC-009 section 4, openapi simpleUpload): one request, same rules as tus plus /complete. The body is streamed with
        // MultipartReader (never IFormFile / ReadFormAsync, which buffer). Parts in order: kind, optional metadata (JSON), then file last.
        // The reservation is taken from Content-Length (an upper bound, multipart framing included) before the file is read, then swapped
        // for the real size.
        uploads.MapPost("/simple", async (
                ClaimsPrincipal user, HttpContext http, UploadService service, DiskAdmission admission, CancellationToken ct) =>
            {
                RaiseBodyLimits(http);
                var request = http.Request;
                var idempotencyKey = SnippetsEndpoints.IdempotencyKeyFrom(request);
                if (idempotencyKey is null)
                    return Problem(http, Error.Validation("The Idempotency-Key header is required (1-128 printable ASCII characters)."), admission);
                if (request.ContentLength is not { } total || total < 1)
                    return Problem(http, Error.Of(ErrorCodes.LengthRequired, "Content-Length is required."), admission);
                if (!Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
                    || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
                    || Microsoft.Net.Http.Headers.HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value is not { Length: > 0 } boundary)
                    return Problem(http, Error.Of(ErrorCodes.UnsupportedMediaType, "Send multipart/form-data with a boundary."), admission);

                var (actor, refusal) = await UploadActors.ResolveAsync(http, user, creating: true, ct);
                if (refusal is not null) return refusal;
                var maxSize = await service.MaxSizeForAsync(actor.UserId, ct);

                string? kind = null;
                CompleteUploadRequest? metadata = null;
                var reader = new Microsoft.AspNetCore.WebUtilities.MultipartReader(boundary, request.Body) { BodyLengthLimit = UploadProtocol.MaxChunkBytes + 65536 };
                Guid sessionId = default;
                try
                {
                    while (await reader.ReadNextSectionAsync(ct) is { } section)
                    {
                        var disposition = Microsoft.Net.Http.Headers.ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var cd) ? cd : null;
                        var name = disposition?.Name.Value?.Trim('"');
                        if (name == "kind")
                        {
                            kind = await ReadSmallAsync(section.Body, 16, ct);
                        }
                        else if (name == "metadata")
                        {
                            var text = await ReadSmallAsync(section.Body, 8192, ct);
                            try { metadata = text is null ? null : System.Text.Json.JsonSerializer.Deserialize<CompleteUploadRequest>(text, MetadataJson); }
                            catch (System.Text.Json.JsonException) { return Problem(http, Error.Validation("metadata is not valid JSON."), admission); }
                        }
                        else if (name == "file")
                        {
                            if (kind is not ("file" or "media"))
                                return Problem(http, Error.Validation("The kind part (file or media) must come before the file part."), admission);
                            var filename = disposition?.FileName.Value ?? disposition?.FileNameStar.Value;
                            // PostgreSQL text cannot hold U+0000: refuse it here instead of a 500 at the insert.
                            if ((filename?.Contains('\0') ?? false) || (section.ContentType?.Contains('\0') ?? false))
                                return Problem(http, Error.Validation("The file name and content type cannot contain NUL (U+0000) characters."), admission);
                            var headerParts = new List<string> { $"kind {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(kind))}" };
                            if (!string.IsNullOrEmpty(filename)) headerParts.Add($"filename {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(filename.Trim('"')))}");
                            if (!string.IsNullOrEmpty(section.ContentType)) headerParts.Add($"filetype {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(section.ContentType))}");
                            if (metadata?.BurnAfterDownload == true) headerParts.Add($"{UploadProtocol.MetadataBurn} {Convert.ToBase64String("1"u8)}");

                            var created = await service.CreateAsync(actor, Math.Min(total, maxSize), false, string.Join(',', headerParts), null, ct);
                            if (!created.IsSuccess) return Problem(http, created.Error!, admission);
                            sessionId = created.Value!.Id;
                            var appended = await service.AppendAsync(actor, sessionId, 0, null, section.Body, ct);
                            if (!appended.IsSuccess)
                            {
                                await service.AbortAsync(actor, sessionId, CancellationToken.None);
                                return Problem(http, appended.Error!, admission);
                            }
                            break;
                        }
                    }
                }
                catch (Microsoft.AspNetCore.Http.BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
                {
                    if (sessionId != default) await service.AbortAsync(actor, sessionId, CancellationToken.None);
                    return Problem(http, Error.Of(ErrorCodes.FileTooLarge, "The upload is larger than your plan allows."), admission);
                }
                catch (InvalidDataException)
                {
                    if (sessionId != default) await service.AbortAsync(actor, sessionId, CancellationToken.None);
                    return Problem(http, Error.Validation("The multipart body is malformed."), admission);
                }
                if (sessionId == default) return Problem(http, Error.Validation("The file part is required."), admission);

                var sealedSession = await service.SealPartialAsync(actor, sessionId, ct);
                if (!sealedSession.IsSuccess) return Problem(http, sealedSession.Error!, admission);
                var completed = await service.CompleteAsync(actor, sessionId, metadata ?? new CompleteUploadRequest(null, null, false, null, null, false), idempotencyKey, ct);
                if (!completed.IsSuccess)
                {
                    await service.AbortAsync(actor, sessionId, CancellationToken.None);
                    return Problem(http, completed.Error!, admission);
                }
                http.Response.Headers.CacheControl = "private, no-store";
                return Results.Json(completed.Value, statusCode: StatusCodes.Status202Accepted);
            })
            .AllowAnonymous()
            .RejectFailedCredentials()
            .RequireScopeWhenAuthenticated(ApiScopes.FilesWrite)
            .RequireRateLimiting("content-write")
            .WithRequestBodyLimit(UploadProtocol.MaxChunkBytes + 65536);
    }

    internal static UploadActor ActorFor(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true
            ? new UploadActor(user.GetUserId(), null, SnippetsEndpoints.CreatedViaFor(user))
            : new UploadActor(null, null, CreatedVia.Web);

    /// <summary>ARC-009 4.3: only this route may carry a chunk of up to 90,000,000 bytes (Kestrel's global limit stays 2 MiB), and the slow-body floor is 240 bytes/s after a 10 s grace. Both features are absent under the test server.</summary>
    private static void RaiseBodyLimits(HttpContext http)
    {
        if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
            size.MaxRequestBodySize = UploadProtocol.MaxChunkBytes;
        if (http.Features.Get<Microsoft.AspNetCore.Server.Kestrel.Core.Features.IHttpMinRequestBodyDataRateFeature>() is { } rate)
            rate.MinDataRate = new Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate(240, TimeSpan.FromSeconds(10));
    }

    private static readonly System.Text.Json.JsonSerializerOptions MetadataJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>A small text part, at most <paramref name="maxBytes"/> bytes (null when longer: the part is then refused as malformed by the caller).</summary>
    private static async Task<string?> ReadSmallAsync(Stream body, int maxBytes, CancellationToken ct)
    {
        var buffer = new byte[maxBytes + 1];
        var read = 0;
        int n;
        while (read < buffer.Length && (n = await body.ReadAsync(buffer.AsMemory(read), ct)) > 0) read += n;
        return read > maxBytes ? null : System.Text.Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static void Tus(HttpContext http) => http.Response.Headers["Tus-Resumable"] = TusResumable;

    /// <summary>tus 1.0: a request for another protocol version is <c>412</c> with <c>Tus-Version</c>. A client that sends no
    /// <c>Tus-Resumable</c> at all (a plain HTTP client of the CLI) is served.</summary>
    private static bool TusVersionMismatch(HttpContext http) =>
        http.Request.Headers.TryGetValue("Tus-Resumable", out var v) && v.ToString() is { Length: > 0 } s && s != TusResumable;

    private static IResult PreconditionFailed() => new HeaderResult(
        Results.StatusCode(StatusCodes.Status412PreconditionFailed), h => h["Tus-Version"] = TusResumable);

    private static string HttpDate(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToString("r", CultureInfo.InvariantCulture);

    /// <summary>The problem for an error. <c>507</c> carries <c>Retry-After</c> (the configured admission seconds) and a refused
    /// type carries <c>reason: type-not-allowed</c> (the wire contract of <c>422 content-rejected</c>).</summary>
    private static IResult Problem(HttpContext http, Error error, DiskAdmission admission)
    {
        if (error.Code == ErrorCodes.InsufficientStorage)
            return new HeaderResult(ApiResults.Problem(error), h => h.RetryAfter = admission.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture));
        if (error.Code == ErrorCodes.ContentRejected)
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: error.Message,
                type: ApiResults.ProblemType(error.Code),
                extensions: new Dictionary<string, object?> { ["code"] = error.Code, ["reason"] = "type-not-allowed" });
        return ApiResults.Problem(error);
    }

    private sealed class HeaderResult(IResult inner, Action<IHeaderDictionary> headers) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            headers(httpContext.Response.Headers);
            return inner.ExecuteAsync(httpContext);
        }
    }
}
