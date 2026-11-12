using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Zaagl.Modules.Files.Contracts;
using Zaagl.Modules.Files.Infrastructure;
using Zaagl.Modules.Resources.Application;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.SharedKernel;

namespace Zaagl.Api.Infrastructure;

/// <summary>
/// The authorize hop for stored objects (ARC-008 sections 4, 5, 7; ARC-009 section 11): after the ticket and the resource state
/// were accepted, the bytes are served only from <c>objects/</c>, only when the scan recorded Clean, and only when the file is
/// there with the stored size. Then the size and Range rule (response cap), then the answer: <c>204 + X-Accel-Redirect</c> for
/// Nginx, or the bytes themselves in <c>Content:DirectObjectServing</c> mode (development and tests, where no Nginx exists).
/// Nothing from a request ever becomes a path: the key is rebuilt from the stored object id.
/// </summary>
public sealed partial class ObjectDelivery(
    IStoredObjects objects,
    IStoredObjectSummaries summaries,
    IMediaMetadata media,
    IObjectStorage storage,
    IOptions<ContentOptions> options,
    IDownloadGrants grants,
    ILogger<ObjectDelivery> logger)
{
    public async Task<IResult> ServeAsync(ResourceDescriptor resource, DeliveryDecision decision, HttpContext http, bool isHead, CancellationToken ct)
    {
        var o = options.Value;
        var stored = await objects.GetForResourceAsync(resource.ResourceId, ct);
        // Fail closed: ready means scanned clean and promoted. Anything else is not served, whatever the resource row says.
        if (stored is null || stored.Area != StorageArea.Objects || stored.ScanVerdict != ScanVerdict.Clean)
        {
            logger.LogError("files_object_not_servable: resource {ResourceId} is ready but its object is not clean in objects/", resource.ResourceId);
            return ApiResults.Problem(Error.Of(ErrorCodes.Gone, "This content is no longer available."));
        }
        var size = await storage.StatAsync(stored.StorageKey, ct);
        if (size is null || size.Value != stored.SizeBytes)
        {
            logger.LogError("files_object_missing: resource {ResourceId} has no matching file in objects/", resource.ResourceId);
            return ApiResults.Problem(Error.Of(ErrorCodes.Gone, "This content is no longer available."));
        }

        // A Preview ticket names a derivative (S3): its own file, type and size. It is served only here, after the original passed the
        // clean-in-objects check above, and only when the derivative row is ready and its file has the recorded size. Never the original.
        var serveKey = stored.StorageKey;
        var serveSize = size.Value;
        var serveMime = string.IsNullOrWhiteSpace(stored.DetectedMime) ? "application/octet-stream" : stored.DetectedMime;
        string? serveName = null;
        string? serveEtag = stored.Sha256.Length > 0 ? $"\"{Convert.ToHexStringLower(stored.Sha256)}\"" : null;
        if (decision.Purpose == DeliveryPurpose.Preview)
        {
            var derivative = decision.Derivative is null ? null : await media.GetReadyDerivativeAsync(resource.ResourceId, decision.Derivative, ct);
            var derivativeSize = derivative is null ? null : await storage.StatAsync(StorageKeys.Create(DerivativeArea(derivative.Kind), derivative.Id), ct);
            if (derivative is null || derivative.Mime is not { Length: > 0 } derivativeMime || derivativeSize is null || derivativeSize.Value != derivative.SizeBytes)
            {
                logger.LogWarning("files_derivative_not_servable: resource {ResourceId} derivative {Kind}", resource.ResourceId, decision.Derivative);
                return ApiResults.Problem(Error.Of(ErrorCodes.Gone, "This content is no longer available."));
            }
            serveKey = StorageKeys.Create(DerivativeArea(derivative.Kind), derivative.Id);
            serveSize = derivativeSize.Value;
            serveMime = derivativeMime;
            serveName = derivative.Kind + (derivativeMime == "image/png" ? ".png" : ".webp");
            serveEtag = $"\"{derivative.Id:N}\"";
        }

        // Size and Range (response cap). Done before anything is counted or claimed, so a refusal consumes nothing. HEAD has no body
        // and is always allowed: it reports the size and Accept-Ranges so a client can plan its parts (ARC-008 section 5).
        if (!isHead && serveSize > o.MaxResponseBytes && !RangeAcceptable(http.Request.Headers.Range.ToString(), serveSize, o.MaxResponseBytes))
        {
            var refusal = ApiResults.Problem(Error.Of(ErrorCodes.RangeRequired, "This file needs a byte range of at most the per-response limit."));
            return new HeaderResult(refusal, h =>
            {
                h.AcceptRanges = "bytes";
                h.ContentRange = "bytes */" + serveSize.ToString(CultureInfo.InvariantCulture);
            });
        }

        // ARC-019 section 5 step 7: the claim (or the count) happens only now, after the object, size and Range checks.
        if (!isHead && decision.Consume != DeliveryConsume.None)
        {
            var consumed = await grants.ConsumeAsync(decision, ct);
            if (consumed.Outcome != DeliveryOutcome.Allowed) return Refusal(consumed.Outcome, http);
        }

        var name = serveName ?? (await summaries.GetAsync([resource.ResourceId], ct)).GetValueOrDefault(resource.ResourceId)?.DisplayName ?? "file";
        var mime = serveMime;
        var isInlinePurpose = decision.Purpose is DeliveryPurpose.Inline or DeliveryPurpose.Preview;
        var inline = isInlinePurpose && o.InlineMimeAllowlist.Contains(mime, StringComparer.OrdinalIgnoreCase);
        var disposition = ContentDispositionFor(inline, name);
        var etag = serveEtag;

        var headers = http.Response.Headers;
        headers.ContentType = mime;
        headers.ContentDisposition = disposition;
        headers.AcceptRanges = "bytes";
        if (isInlinePurpose) headers["X-Zaagl-Acao"] = o.AppOrigin; // ARC-008 section 9: Inline and Preview only
        headers["X-Accel-Buffering"] = "no";
        if (decision.Purpose == DeliveryPurpose.Download) headers.CacheControl = "private, no-store";

        if (!o.DirectObjectServing)
        {
            var internalPath = storage.GetInternalPath(serveKey); // refuses temp and quarantine
            if (!InternalPathPattern().IsMatch(internalPath)) throw new InvalidOperationException("The internal path is not in the allowed shape.");
            if (etag is not null) headers.ETag = etag;
            headers["X-Accel-Redirect"] = internalPath;
            return Results.StatusCode(StatusCodes.Status204NoContent);
        }

        if (isHead)
        {
            headers.ContentLength = serveSize;
            if (etag is not null) headers.ETag = etag;
            return Results.Empty;
        }
        var stream = await storage.OpenReadAsync(serveKey, 0, null, ct);
        return Results.Stream(stream, mime, fileDownloadName: null, lastModified: null,
            entityTag: etag is null ? null : new EntityTagHeaderValue(etag), enableRangeProcessing: true);
    }

    /// <summary>Derivative kinds and where their files live (ARC-011 section 3): thumbnails in <c>thumbnails/</c>, the rest in <c>previews/</c>.</summary>
    private static StorageArea DerivativeArea(string kind) => kind is "thumb-320" or "poster" ? StorageArea.Thumbnails : StorageArea.Previews;

    /// <summary>The refusal of a consume step. Nothing was consumed for any of them.</summary>
    internal static IResult Refusal(DeliveryOutcome outcome, HttpContext http)
    {
        switch (outcome)
        {
            case DeliveryOutcome.Burned: return ApiResults.Problem(Error.Of(ErrorCodes.Burned, "This content is gone."));
            case DeliveryOutcome.Busy:
                http.Response.Headers.RetryAfter = "1";
                return ApiResults.Problem(Error.Of(ErrorCodes.Busy, "Busy. Try again in a second."));
            case DeliveryOutcome.Processing: return ApiResults.Problem(Error.Of(ErrorCodes.Processing, "This content is not ready."));
            case DeliveryOutcome.Rejected: return ApiResults.Problem(Error.Of(ErrorCodes.ContentRejected, "This content is not available."));
            case DeliveryOutcome.TicketInvalid: return ApiResults.Problem(Error.Of(ErrorCodes.TicketInvalid, "The link is no longer valid."));
            default: return ApiResults.Problem(Error.Of(ErrorCodes.Gone, "This content is no longer available."));
        }
    }

    /// <summary>ARC-008 section 5: one byte range whose served length is at most the cap (<c>a-b</c>, <c>-n</c>, or <c>a-</c> when the
    /// rest fits). Several ranges, a malformed header or a longer range are refused.</summary>
    public static bool RangeAcceptable(string header, long size, long cap)
    {
        if (!RangeHeaderValue.TryParse(header, out var range) || !string.Equals(range.Unit.Value, "bytes", StringComparison.OrdinalIgnoreCase)
            || range.Ranges.Count != 1) return false;
        var r = range.Ranges.Single();
        long from, to;
        if (r.From is null)
        {
            if (r.To is not { } suffix || suffix <= 0) return false;
            from = Math.Max(0, size - suffix);
            to = size - 1;
        }
        else
        {
            from = r.From.Value;
            to = Math.Min(r.To ?? size - 1, size - 1);
            if (from >= size || to < from) return false;
        }
        return to - from + 1 <= cap;
    }

    /// <summary>RFC 6266 / 8187: <c>attachment; filename="&lt;ascii&gt;"; filename*=UTF-8''&lt;pct&gt;</c>, the ascii part at most 150 bytes
    /// with every non-ASCII, control and <c>" \ / ;</c> character replaced by an underscore (ARC-008 section 7).</summary>
    internal static string ContentDispositionFor(bool inline, string displayName)
    {
        var ascii = new StringBuilder();
        foreach (var c in displayName)
            ascii.Append(c is < ' ' or > '~' or '"' or '\\' or '/' or ';' ? '_' : c);
        var asciiName = ascii.Length > 150 ? KeepExtension(ascii.ToString(), 150) : ascii.ToString();
        return $"{(inline ? "inline" : "attachment")}; filename=\"{asciiName}\"; filename*=UTF-8''{Uri.EscapeDataString(displayName)}";
    }

    private static string KeepExtension(string name, int max)
    {
        var dot = name.LastIndexOf('.');
        var ext = dot > 0 && name.Length - dot <= 16 ? name[dot..] : "";
        return name[..(max - ext.Length)] + ext;
    }

    [GeneratedRegex(@"\A/_protected/(objects|previews|thumbnails)/[0-9a-f]{2}/[0-9a-f]{2}/[0-9a-f-]{36}\z", RegexOptions.CultureInvariant)]
    private static partial Regex InternalPathPattern();

    private sealed class HeaderResult(IResult inner, Action<IHeaderDictionary> headers) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            headers(httpContext.Response.Headers);
            return inner.ExecuteAsync(httpContext);
        }
    }
}
