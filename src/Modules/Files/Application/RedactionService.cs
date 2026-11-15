using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;
using Zaagl.Modules.Billing.Contracts;
using Zaagl.Modules.Files.Contracts;
using Zaagl.Modules.Files.Domain;
using Zaagl.Modules.Files.Infrastructure;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.SharedKernel;
using Zaagl.SharedKernel.Jobs;

namespace Zaagl.Modules.Files.Application;

/// <summary><c>Redaction:*</c> (docs/phase2/pii-redaction.md): the cost bounds of a server-side redaction render.</summary>
public sealed class RedactionOptions
{
    public const string Section = "Redaction";

    /// <summary>Largest image (header width x height) rendered: the whole image is decoded at full resolution, 4 bytes per pixel
    /// (default 16,777,216 = 64 MB of pixels, e.g. 4096 x 4096 or a 5K screenshot).</summary>
    public long MaxPixels { get; set; } = 16_777_216;

    /// <summary>Renders at once in this process; a request that cannot get a slot within 2 seconds answers 503.</summary>
    public int MaxParallel { get; set; } = 2;

    /// <summary>Wall-clock limit of one render.</summary>
    public int TimeoutSeconds { get; set; } = 20;
}

/// <summary>The process-wide render slots of <see cref="RedactionService"/>.</summary>
public sealed class RedactionGate(IOptions<RedactionOptions> options)
{
    public SemaphoreSlim Slots { get; } = new(Math.Max(1, options.Value.MaxParallel));
}

/// <summary>One rectangle to hide, normalised to the displayed (EXIF-oriented) image: 0..1 from the top-left.</summary>
public sealed record RedactionRegion(double X, double Y, double W, double H);

public static class RedactionStyles
{
    /// <summary>Opaque fill (default): nothing of the original pixels survives.</summary>
    public const string Solid = "solid";
    /// <summary>Coarse mosaic: blocks of at least a quarter of the region's short side (and 12 px), each its mean colour.</summary>
    public const string Pixelate = "pixelate";

    public static readonly IReadOnlyList<string> All = [Solid, Pixelate];
}

/// <summary>
/// Renders a redacted copy of an image: full-resolution decode through the same bounds as the thumbnailer (header pixel gate before any
/// decoder, the decoder's dimensions must equal the header's, complete decodes only), EXIF orientation applied so the regions match what
/// the owner saw, then every region painted over. The output is a fresh PNG with no metadata (no EXIF, GPS, ICC text, comments). An
/// animated image is redacted as its first frame (a still).
/// </summary>
public static class RedactionRenderer
{
    public sealed record Rendered(byte[] Png, int Width, int Height);

    public static Rendered? Render(string path, ImageHeader header, IReadOnlyList<RedactionRegion> regions, string style, long maxPixels, string? fill = null)
    {
        if (header.Width * header.Height > maxPixels) return null;
        using var codec = SKCodec.Create(path, out var created);
        if (codec is null || created is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;
        var info = codec.Info;
        if (info.Width != header.Width || info.Height != header.Height) return null;
        var origin = (int)codec.EncodedOrigin is >= 1 and <= 8 ? (int)codec.EncodedOrigin : 1;
        var alpha = header.HasAlpha ? SKAlphaType.Premul : SKAlphaType.Opaque;
        using var srgb = SKColorSpace.CreateSrgb();
        var decodeInfo = new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, alpha, srgb);
        using var decoded = new SKBitmap(decodeInfo);
        if (codec.GetPixels(decodeInfo, decoded.GetPixels()) != SKCodecResult.Success) return null;
        using var upright = ImageThumbnailer.Orient(decoded, origin, alpha, srgb);
        Apply(upright, regions, style, fill);
        using var image = SKImage.FromBitmap(upright);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data is null || data.Size == 0 ? null : new Rendered(data.ToArray(), upright.Width, upright.Height);
    }

    /// <summary>Paints the regions on <paramref name="bitmap"/> (public for tests). Rectangles are rounded outwards to whole pixels.</summary>
    public static void Apply(SKBitmap bitmap, IReadOnlyList<RedactionRegion> regions, string style, string? fill = null)
    {
        var w = bitmap.Width;
        var h = bitmap.Height;
        var color = SKColor.TryParse(fill ?? "#000000", out var c) ? c.WithAlpha(255) : SKColors.Black;
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = false, BlendMode = SKBlendMode.Src };
        foreach (var r in regions)
        {
            var left = Math.Clamp((int)Math.Floor(r.X * w), 0, w);
            var top = Math.Clamp((int)Math.Floor(r.Y * h), 0, h);
            var right = Math.Clamp((int)Math.Ceiling((r.X + r.W) * w), 0, w);
            var bottom = Math.Clamp((int)Math.Ceiling((r.Y + r.H) * h), 0, h);
            if (right <= left || bottom <= top) continue;
            if (style == RedactionStyles.Pixelate)
            {
                var block = Math.Max(12, Math.Min(right - left, bottom - top) / 4);
                for (var by = top; by < bottom; by += block)
                    for (var bx = left; bx < right; bx += block)
                    {
                        var ex = Math.Min(bx + block, right);
                        var ey = Math.Min(by + block, bottom);
                        paint.Color = Mean(bitmap, bx, by, ex, ey);
                        canvas.DrawRect(SKRect.Create(bx, by, ex - bx, ey - by), paint);
                    }
            }
            else
            {
                paint.Color = color;
                canvas.DrawRect(SKRect.Create(left, top, right - left, bottom - top), paint);
            }
        }
        canvas.Flush();
    }

    private static SKColor Mean(SKBitmap bitmap, int x0, int y0, int x1, int y1)
    {
        long r = 0, g = 0, b = 0, n = 0;
        var step = Math.Max(1, (x1 - x0) / 16); // sampled: a mosaic block needs no exact mean
        for (var y = y0; y < y1; y += step)
            for (var x = x0; x < x1; x += step)
            {
                var p = bitmap.GetPixel(x, y);
                r += p.Red; g += p.Green; b += p.Blue; n++;
            }
        return n == 0 ? SKColors.Black : new SKColor((byte)(r / n), (byte)(g / n), (byte)(b / n), 255);
    }
}

public sealed record RedactCommand(
    Guid SourceResourceId, IReadOnlyList<RedactionRegion>? Regions, string? Style, string? Fill, string? Title, DateTime? ExpiresAt,
    string? Password, string? CustomSlug, bool Public);

/// <param name="Code">The redacted copy's own link (kind media, viewer <c>/i/</c>); nothing refers back to the original.</param>
public sealed record RedactedDto(Guid ResourceId, string Code, string ShortUrl, string? LinkKey, DateTime ExpiresAt, string Status, int Width,
    int Height, int Regions);

/// <summary>
/// E2.3 smart PII redaction guard, server side (docs/phase2/pii-redaction.md). Detection runs in the browser (OCR) plus the server text check
/// (<see cref="PiiDetector"/>); this renders the owner's reviewed rectangles into a <b>new</b> image resource of the owner with its own link,
/// stored and scanned through exactly the upload pipeline (temp object, scan task, thumbnail/palette). The original is never modified;
/// deleting it is the caller's choice (the endpoint does it right after this returns).
/// </summary>
public sealed class RedactionService(
    FilesDbContext db,
    IResourceRegistry resources,
    ILinkIssuer issuer,
    IContentPlanProvider plans,
    LocalObjectStorage storage,
    RedactionGate gate,
    IOptions<RedactionOptions> options,
    IOptions<ScanOptions> scan,
    [FromKeyedServices(FilesDbContext.Schema)] TaskQueue tasks,
    TimeProvider time,
    ILogger<RedactionService> logger)
{
    public static readonly IReadOnlySet<string> RasterTypes =
        new HashSet<string>(StringComparer.Ordinal) { "image/png", "image/jpeg", "image/gif", "image/webp" };

    public async Task<Result<RedactedDto>> CreateAsync(Guid userId, CreatedVia via, RedactCommand command, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var plan = await plans.GetForUserAsync(userId, ct);
        var tools = plan.ToolsOrNone;
        if (!tools.RedactEnabled || tools.RedactMaxRegions < 1) return Error.PlanRequired("The redaction guard is not available on your plan.");

        // 1. Regions and style (no I/O).
        if (command.Regions is not { Count: > 0 } regions) return Error.Validation("regions must list at least one rectangle.");
        if (regions.Count > tools.RedactMaxRegions) return Error.Validation($"At most {tools.RedactMaxRegions} regions on your plan.");
        foreach (var r in regions)
        {
            if (!double.IsFinite(r.X) || !double.IsFinite(r.Y) || !double.IsFinite(r.W) || !double.IsFinite(r.H)
                || r.X < 0 || r.Y < 0 || r.W <= 0 || r.H <= 0 || r.X + r.W > 1.000001 || r.Y + r.H > 1.000001)
                return Error.Validation("Every region is {x, y, w, h} within 0..1 of the displayed image, with w and h above 0.");
        }
        var style = (command.Style ?? RedactionStyles.Solid).Trim().ToLowerInvariant();
        if (!RedactionStyles.All.Contains(style)) return Error.Validation("style must be solid or pixelate.");
        if (command.Fill is { } fill && (fill.Length != 7 || fill[0] != '#' || !SKColor.TryParse(fill, out _)))
            return Error.Validation("fill must be a colour like #000000.");
        var title = CleanTitle(command.Title);
        if (command.Title is { Length: > 200 }) return Error.Validation("The title is at most 200 characters.");
        if (command.Password is { } pw)
        {
            if (pw.Length < 4 || pw.Length > 128) return Error.Validation("The password must be 4-128 characters.");
            if (!plan.Capabilities.PasswordOnContent) return Error.PlanRequired("Password protection is available on the Starter plan.");
        }
        if (command.CustomSlug is { } slug && (slug.Trim().Length is < 4 or > 64)) return Error.Validation("The alias must be 4-64 characters.");
        var maxExpiry = now.AddDays(plan.Limits.RetentionDays);
        var expiresAt = maxExpiry;
        if (command.ExpiresAt is { } requested)
        {
            expiresAt = requested.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(requested, DateTimeKind.Utc) : requested.ToUniversalTime();
            if (expiresAt <= now.AddMinutes(1)) return Error.Validation("The expiry must be at least a minute in the future.");
            if (expiresAt > maxExpiry) return Error.Of(ErrorCodes.ExpiryBeyondPlan, $"Your plan allows at most {plan.Limits.RetentionDays} days.");
        }

        // 2. The source: the owner's own ready, clean raster image (someone else's or a missing one is the same 422).
        var source = await resources.GetAsync(command.SourceResourceId, ct);
        var unusable = Error.Of(ErrorCodes.ResourceUnusable, "The image cannot be redacted: it must be one of your images (PNG, JPEG, GIF, WebP), scanned and ready.");
        if (source is null || source.Kind != ResourceKind.Media || source.Owner.UserId != userId || source.BurnAfterDownload
            || source.State != ResourceState.Ready) return unusable;
        var obj = await db.StorageObjects.AsNoTracking().FirstOrDefaultAsync(o => o.ResourceId == source.ResourceId, ct);
        if (obj is not { Area: StorageArea.Objects, ScanStatus: ScanVerdict.Clean } || obj.DetectedMime is not { } mime || !RasterTypes.Contains(mime))
            return unusable;
        var path = storage.ResolvePath(StorageKeys.Create(StorageArea.Objects, obj.Id));
        if (!File.Exists(path)) return unusable;

        // 3. Render under the process slots and a time limit.
        var o = options.Value;
        if (!await gate.Slots.WaitAsync(TimeSpan.FromSeconds(2), ct))
            return Error.ServiceUnavailable("The redaction service is busy. Try again in a few seconds.");
        RedactionRenderer.Rendered? rendered;
        try
        {
            var work = Task.Run(() =>
            {
                var header = ImageProbe.Read(path);
                return header is null ? null : RedactionRenderer.Render(path, header, regions, style, o.MaxPixels, command.Fill);
            }, CancellationToken.None);
            rendered = await work.WaitAsync(TimeSpan.FromSeconds(Math.Max(1, o.TimeoutSeconds)), ct);
        }
        catch (TimeoutException)
        {
            rendered = null;
        }
        finally
        {
            gate.Slots.Release();
        }
        if (rendered is null)
            return Error.Of(ErrorCodes.ResourceUnusable, $"This image cannot be redacted here (larger than {o.MaxPixels} pixels, or undecodable).");
        if (rendered.Png.LongLength > plan.Limits.MaxFileBytes)
            return Error.Of(ErrorCodes.FileTooLarge, "The redacted image is larger than a file can be on your plan.");

        // 4. A new Media resource (quota reserved for the exact bytes), its bytes as a temp object, the link, then the normal scan task.
        var registered = await resources.RegisterAsync(new RegisterResourceCommand(
            ResourceKind.Media, new ContentOwner(userId, null), rendered.Png.LongLength, expiresAt, title, BurnAfterDownload: false, via), ct);
        if (!registered.IsSuccess) return registered.Error!;
        var resourceId = registered.Value!.ResourceId;
        string? key = null;
        try
        {
            using (var bytes = new MemoryStream(rendered.Png, writable: false))
                key = (await storage.PutAsync(StorageArea.Temp, bytes, ct)).StorageKey;
            db.StorageObjects.Add(new StorageObjectRow
            {
                Id = StorageKeys.Parse(key).Id,
                ResourceId = resourceId,
                Area = StorageArea.Temp,
                SizeBytes = rendered.Png.LongLength,
                DetectedMime = "image/png",
                DetectedExt = ".png",
                DisplayName = RedactedName(obj.DisplayName),
                ScanStatus = ScanVerdict.Pending,
                CreatedAt = now,
            });
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
        catch
        {
            if (key is not null) await storage.DeleteAsync(key, CancellationToken.None);
            await resources.MarkRejectedAsync(resourceId, RejectedReasons.CreateFailed, CancellationToken.None);
            throw;
        }

        var issued = await issuer.CreateContentLinkAsync(new ContentLinkRequest(
            resourceId, LinkTargetKind.Media, userId, null, expiresAt, command.CustomSlug, title, command.Password,
            RequireLinkKey: !command.Public), ct);
        if (!issued.IsSuccess)
        {
            await resources.MarkRejectedAsync(resourceId, RejectedReasons.CreateFailed, CancellationToken.None);
            return issued.Error!;
        }
        var link = issued.Value!;
        var bound = await resources.BindLinkAsync(resourceId, link.LinkId, ct);
        if (!bound.IsSuccess) return bound.Error!;
        await tasks.EnqueueAsync(db, "scan", $"scan:{resourceId}", JsonSerializer.Serialize(new { resourceId }),
            Math.Max(1, scan.Value.MaxAttempts), now, now, ct);
        logger.LogInformation("Redacted copy {ResourceId} rendered ({Regions} regions)", resourceId, regions.Count);
        return new RedactedDto(resourceId, link.Code, link.ShortUrl, link.LinkKey, link.ExpiresAt, "processing", rendered.Width, rendered.Height, regions.Count);
    }

    /// <summary><c>name-redacted.png</c> from the original's display name (its extension dropped).</summary>
    public static string RedactedName(string? original)
    {
        var stem = Path.GetFileNameWithoutExtension(original ?? "");
        if (string.IsNullOrWhiteSpace(stem)) stem = "image";
        return FileNames.Sanitize($"{stem[..Math.Min(stem.Length, 100)]}-redacted.png", ".png");
    }

    private static string? CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var s = new string(title.Trim().Where(c => !char.IsControl(c)).ToArray()).Trim();
        return s.Length == 0 ? null : s[..Math.Min(s.Length, 200)];
    }
}
