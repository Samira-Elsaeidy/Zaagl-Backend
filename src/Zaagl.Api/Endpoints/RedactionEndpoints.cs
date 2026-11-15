using System.Security.Claims;
using Zaagl.Api.Infrastructure;
using Zaagl.Modules.Files.Application;
using Zaagl.Modules.Resources.Application;
using Zaagl.SharedKernel;

namespace Zaagl.Api.Endpoints;

/// <summary>openapi RedactionRegionDto: normalised to the displayed (EXIF-oriented) image.</summary>
public sealed record RedactionRegionDto(double X, double Y, double W, double H);

/// <summary>openapi RedactionCreateRequest (docs/phase2/pii-redaction.md).</summary>
/// <param name="DeleteOriginal">Default true ("share redacted only"): the original is deleted right after the copy is rendered.</param>
public sealed record RedactionCreateRequest(
    Guid SourceResourceId,
    RedactionRegionDto[]? Regions,
    string? Style = null,
    string? Fill = null,
    bool DeleteOriginal = true,
    string? Title = null,
    DateTime? ExpiresAt = null,
    string? Password = null,
    string? CustomSlug = null,
    bool Public = false);

/// <summary>openapi OcrWordDto: one word the browser's OCR recognised (box normalised 0..1; <c>line</c> groups words of one text line).</summary>
public sealed record OcrWordDto(string? Text, double X, double Y, double W, double H, int Line = 0);

/// <summary>openapi RedactionDetectRequest: <c>text</c> (plain text) or <c>words</c> (OCR words with boxes), not both.</summary>
public sealed record RedactionDetectRequest(string? Text = null, OcrWordDto[]? Words = null);

public sealed record DetectedSpanDto(string Kind, int Start, int Length);
public sealed record DetectedBoxDto(string Kind, int Word, double X, double Y, double W, double H);

/// <param name="Spans">For <c>text</c>: positions only, the matched text is never echoed.</param>
/// <param name="Boxes">For <c>words</c>: one box per word to cover, ready to send as <c>regions</c> after the owner's review.</param>
public sealed record RedactionDetectResponse(IReadOnlyList<DetectedSpanDto> Spans, IReadOnlyList<DetectedBoxDto> Boxes);

public sealed record RedactionCreatedDto(Guid ResourceId, string Code, string ShortUrl, string? LinkKey, DateTime ExpiresAt, string Status,
    int Width, int Height, int Regions, bool OriginalDeleted);

/// <summary>
/// Phase 2 E2.3 smart PII redaction guard (docs/phase2/pii-redaction.md). Detection runs in the browser (OCR) and through the server text
/// check (<c>/detect</c>); the server renders the reviewed rectangles into a new image with its own link and, by default, deletes the
/// original ("share redacted only"). Accounts only, owner's own images only; <c>private, no-store</c>; nothing of the text is logged.
/// </summary>
public static class RedactionEndpoints
{
    public const int MaxTextChars = 64 * 1024;
    public const int MaxWords = 5000;
    public const int MaxWordChars = 200;

    public static void MapRedactions(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/redactions").WithTags("PII redaction guard");

        group.MapPost("", async (RedactionCreateRequest request, ClaimsPrincipal user, HttpContext http, RedactionService service,
                OwnerResources owner, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                var userId = user.GetUserId();
                var created = await service.CreateAsync(userId, SnippetsEndpoints.CreatedViaFor(user), new RedactCommand(
                    request.SourceResourceId, request.Regions?.Select(r => new RedactionRegion(r.X, r.Y, r.W, r.H)).ToList(), request.Style,
                    request.Fill, request.Title, request.ExpiresAt, request.Password, request.CustomSlug, request.Public), ct);
                if (!created.IsSuccess) return ApiResults.Problem(created.Error!);
                var deleted = false;
                if (request.DeleteOriginal)
                {
                    // The copy is safely stored (temp object + scan task); the original goes now: its link answers 410 and the Phase 1
                    // deletion path removes its bytes.
                    var result = await owner.DeleteAsync(request.SourceResourceId, userId, CancellationToken.None);
                    deleted = result.IsSuccess;
                }
                var c = created.Value!;
                return Results.Created($"/api/resources/{c.ResourceId}", new RedactionCreatedDto(c.ResourceId, c.Code, c.ShortUrl, c.LinkKey,
                    c.ExpiresAt, c.Status, c.Width, c.Height, c.Regions, deleted));
            })
            .RequireScope(ApiScopes.ToolsWrite)
            .RequireRateLimiting("content-write")
            .WithSummary("Render a redacted copy of one of your images (solid or pixelated rectangles); by default delete the original.")
            .Produces<RedactionCreatedDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status402PaymentRequired)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity).ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/detect", (RedactionDetectRequest request, HttpContext http) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                if ((request.Text is null) == (request.Words is null)) return ApiResults.Problem(Error.Validation("Send text or words (one of them)."));
                if (request.Text is { } text)
                {
                    if (text.Length > MaxTextChars) return ApiResults.Problem(Error.Of(ErrorCodes.InputTooLarge, $"text is at most {MaxTextChars} characters."));
                    return Results.Ok(new RedactionDetectResponse(
                        PiiDetector.Scan(text).Select(s => new DetectedSpanDto(s.Kind, s.Start, s.Length)).ToList(), []));
                }
                var words = request.Words!;
                if (words.Length > MaxWords) return ApiResults.Problem(Error.Of(ErrorCodes.InputTooLarge, $"At most {MaxWords} words."));
                var list = new List<OcrWord>(words.Length);
                foreach (var w in words)
                {
                    if (w.Text is null || w.Text.Length is 0 or > MaxWordChars) return ApiResults.Problem(Error.Validation($"Every word has 1-{MaxWordChars} characters."));
                    if (!double.IsFinite(w.X) || !double.IsFinite(w.Y) || !double.IsFinite(w.W) || !double.IsFinite(w.H)
                        || w.X < 0 || w.Y < 0 || w.W <= 0 || w.H <= 0 || w.X + w.W > 1.000001 || w.Y + w.H > 1.000001)
                        return ApiResults.Problem(Error.Validation("Every word box is {x, y, w, h} within 0..1."));
                    list.Add(new OcrWord(w.Text, w.X, w.Y, w.W, w.H, w.Line));
                }
                return Results.Ok(new RedactionDetectResponse([],
                    PiiDetector.Boxes(list).Select(b => new DetectedBoxDto(b.Kind, b.Word, b.X, b.Y, b.W, b.H)).ToList()));
            })
            .RequireScope(ApiScopes.ToolsWrite)
            .RequireRateLimiting("content-write")
            .WithSummary("Server-side PII text check on text or OCR words: emails, Luhn-valid card numbers, phone numbers, API keys and tokens.")
            .Produces<RedactionDetectResponse>().ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status413PayloadTooLarge);
    }
}
