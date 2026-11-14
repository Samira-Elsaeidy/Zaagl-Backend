using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;
using Zaagl.Modules.Files.Contracts;
using Zaagl.Modules.Files.Domain;
using Zaagl.Modules.Files.Infrastructure;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.SharedKernel.Jobs;

namespace Zaagl.Modules.Files.Application;

/// <summary>
/// Phase 2 E2.2 (docs/phase2/approval-gate.md): burns a watermark into a downscaled copy of an image. Input is the bomb-safe output of
/// <see cref="ImageThumbnailer"/> at <see cref="WatermarkEdge"/> (the original is only ever read, through the same decode limits);
/// the drawing is two layers: a diagonal stripe lattice that needs no font (so the copy is marked even on a host without fonts) and
/// the repeated text when a font is available.
/// </summary>
public static class WatermarkRenderer
{
    /// <summary>Longest edge of the watermarked preview: big enough to judge a design, small enough not to replace the original.</summary>
    public const int WatermarkEdge = 1600;

    public static (byte[] Bytes, string Mime, int Width, int Height)? Render(byte[] baseImage, string text)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(baseImage));
        if (codec is null) return null;
        using var bitmap = SKBitmap.Decode(codec);
        if (bitmap is null) return null;
        using var surface = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(surface))
        {
            canvas.DrawBitmap(bitmap, 0, 0);
            var w = bitmap.Width;
            var h = bitmap.Height;
            var diagonal = (float)Math.Sqrt((double)w * w + (double)h * h);
            var unit = Math.Max(24f, Math.Min(w, h) / 6f);

            // Layer 1: font-free diagonal stripes (dark and light, so they show on any background).
            canvas.Save();
            canvas.Translate(w / 2f, h / 2f);
            canvas.RotateDegrees(-30);
            using (var dark = new SKPaint { Color = new SKColor(0, 0, 0, 70), IsAntialias = true, StrokeWidth = Math.Max(2f, unit / 10f), Style = SKPaintStyle.Stroke })
            using (var light = new SKPaint { Color = new SKColor(255, 255, 255, 90), IsAntialias = true, StrokeWidth = Math.Max(1f, unit / 20f), Style = SKPaintStyle.Stroke })
            {
                for (var y = -diagonal; y <= diagonal; y += unit)
                {
                    canvas.DrawLine(-diagonal, y, diagonal, y, dark);
                    canvas.DrawLine(-diagonal, y + unit / 8f, diagonal, y + unit / 8f, light);
                }
            }

            // Layer 2: the text, tiled along the stripes, when the platform has a usable font.
            using var typeface = SKTypeface.Default;
            using var font = new SKFont(typeface, Math.Max(14f, unit / 2.5f));
            using var textPaint = new SKPaint { Color = new SKColor(255, 255, 255, 150), IsAntialias = true };
            using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 120), IsAntialias = true };
            var width = font.MeasureText(text);
            if (width > 0)
            {
                for (var y = -diagonal; y <= diagonal; y += unit * 1.5f)
                {
                    for (var x = -diagonal; x <= diagonal; x += width + unit)
                    {
                        canvas.DrawText(text, x + 1.5f, y + unit / 2f + 1.5f, SKTextAlign.Left, font, shadow);
                        canvas.DrawText(text, x, y + unit / 2f, SKTextAlign.Left, font, textPaint);
                    }
                }
            }
            canvas.Restore();
        }

        using var image = SKImage.FromBitmap(surface);
        using var data = image.Encode(SKEncodedImageFormat.Webp, 82);
        if (data is null || data.Size == 0) return null;
        return (data.ToArray(), "image/webp", surface.Width, surface.Height);
    }
}

/// <summary><see cref="IWatermarkDerivatives"/>: queues the <c>watermark</c> task on <c>files.tasks</c> (deduplicated per resource and kind).</summary>
public sealed class WatermarkRequests(FilesDbContext db, [FromKeyedServices(FilesDbContext.Schema)] TaskQueue tasks, TimeProvider time) : IWatermarkDerivatives
{
    public string KindFor(string text) =>
        "wm-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    public async Task<string> RequestAsync(Guid resourceId, string text, CancellationToken cancellationToken)
    {
        var kind = KindFor(text);
        var now = time.GetUtcNow().UtcDateTime;
        if (!await db.MediaDerivatives.AsNoTracking().AnyAsync(d => d.ResourceId == resourceId && d.Kind == kind, cancellationToken))
            await tasks.EnqueueAsync(db, WatermarkTaskHandler.TaskKind, $"watermark:{resourceId}:{kind}",
                JsonSerializer.Serialize(new { resourceId, kind, text }), 3, now, now, cancellationToken);
        return kind;
    }
}

/// <summary>
/// The worker task <c>watermark</c>: for a ready, clean, non-burn image, renders the watermarked preview and stores it as a derivative
/// (<c>previews/</c>, kind <c>wm-…</c>). Idempotent: an existing derivative row ends the task; a lost race deletes its own file.
/// </summary>
public sealed class WatermarkTaskHandler(
    FilesDbContext db,
    IResourceRegistry resources,
    LocalObjectStorage storage,
    MediaConcurrencyGate gate,
    IOptions<MediaOptions> options,
    TimeProvider time,
    ILogger<WatermarkTaskHandler> logger) : ITaskHandler<FilesDbContext>
{
    public const string TaskKind = "watermark";
    public string Kind => TaskKind;

    public TimeSpan RetryDelay(int attempt) => TimeSpan.FromMinutes(Math.Min(15, attempt * 2));

    public async Task HandleAsync(ClaimedTask task, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(task.Payload);
        var resourceId = doc.RootElement.GetProperty("resourceId").GetGuid();
        var kind = doc.RootElement.GetProperty("kind").GetString() ?? "";
        var text = doc.RootElement.GetProperty("text").GetString() ?? "";
        if (!kind.StartsWith("wm-", StringComparison.Ordinal) || kind.Length > 32 || text.Length is 0 or > 60) return;

        var resource = await resources.GetAsync(resourceId, ct);
        if (resource is not { Kind: ResourceKind.Media, BurnAfterDownload: false, Processing: ProcessingStatus.Ready }) return;
        var obj = await db.StorageObjects.AsNoTracking().FirstOrDefaultAsync(o => o.ResourceId == resourceId, ct);
        if (obj is not { Area: StorageArea.Objects, ScanStatus: ScanVerdict.Clean } || !MediaSniffer.IsMediaAllowed(obj.DetectedMime)) return;
        if (await db.MediaDerivatives.AnyAsync(d => d.ResourceId == resourceId && d.Kind == kind, ct)) return;
        var path = storage.ResolvePath(StorageKeys.Create(StorageArea.Objects, obj.Id));
        if (!File.Exists(path)) return;

        var o = options.Value;
        var edgeOptions = new MediaOptions
        {
            MaxPixels = o.MaxPixels, MaxDecodePixels = o.MaxDecodePixels, MaxInlinePixels = o.MaxInlinePixels,
            ThumbnailEdge = WatermarkRenderer.WatermarkEdge, WebpQuality = 90, MaxParallel = o.MaxParallel, TimeoutSeconds = o.TimeoutSeconds,
        };
        (byte[] Bytes, string Mime, int Width, int Height)? rendered = null;
        string? reason = null;
        await gate.Slots.WaitAsync(ct);
        try
        {
            var work = Task.Run(() =>
            {
                var header = ImageProbe.Read(path);
                if (header is null) return ((byte[], string, int, int)?)null;
                var scaled = ImageThumbnailer.Create(path, header, edgeOptions);
                return scaled.Outcome == ThumbnailOutcome.Created && scaled.Bytes is { } b ? WatermarkRenderer.Render(b, text) : null;
            }, CancellationToken.None);
            rendered = await work.WaitAsync(TimeSpan.FromSeconds(Math.Max(1, o.TimeoutSeconds)), ct);
            if (rendered is null) reason = "unrenderable";
        }
        catch (TimeoutException)
        {
            reason = "timeout";
        }
        finally
        {
            gate.Slots.Release();
        }

        var now = time.GetUtcNow().UtcDateTime;
        var row = new MediaDerivativeRow { Id = Guid.NewGuid(), ResourceId = resourceId, Kind = kind, Area = StorageArea.Previews, CreatedAt = now };
        string? writtenKey = null;
        if (rendered is { } r)
        {
            using var bytes = new MemoryStream(r.Bytes, writable: false);
            var put = await storage.PutAsync(StorageArea.Previews, bytes, ct);
            writtenKey = put.StorageKey;
            row.Id = StorageKeys.Parse(put.StorageKey).Id;
            row.Status = (short)DerivativeState.Ready;
            row.Width = r.Width;
            row.Height = r.Height;
            row.SizeBytes = put.SizeBytes;
            row.Mime = r.Mime;
        }
        else
        {
            row.Status = (short)DerivativeState.Failed;
            row.Reason = reason;
            logger.LogWarning("watermark_failed: resource {ResourceId} ({Reason})", resourceId, reason);
        }
        db.MediaDerivatives.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            if (writtenKey is not null) await storage.DeleteAsync(writtenKey, CancellationToken.None);
            db.ChangeTracker.Clear();
            if (await db.MediaDerivatives.AsNoTracking().AnyAsync(d => d.ResourceId == resourceId && d.Kind == kind, CancellationToken.None)) return;
            throw;
        }
    }
}
