using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using Zaagl.Modules.Files.Contracts;
using Zaagl.Modules.Files.Domain;
using Zaagl.Modules.Files.Infrastructure;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.SharedKernel.Jobs;

namespace Zaagl.Modules.Files.Application;

/// <summary>The watermark text cannot be drawn (no embedded font covers any of its characters, or the fonts failed to load).</summary>
public sealed class WatermarkTextException(string message) : Exception(message);

/// <summary>
/// The watermark fonts, embedded in this assembly (Fonts/*.ttf, SIL OFL 1.1): Noto Sans Bold (Latin, Greek, Cyrillic, common
/// punctuation and symbols) and Noto Sans Arabic Bold. Loading them from the assembly instead of the system makes the render identical
/// on Windows dev machines, in tests and in the worker image, which has no fonts and no fontconfig at all (SkiaSharp
/// NativeAssets.Linux.NoDependencies). <see cref="SKTypeface.Default"/> is never used: on a font-less host it measures every string as 0.
/// </summary>
public static class WatermarkFonts
{
    private static readonly Lazy<(SKTypeface? Latin, SKTypeface? Arabic)> Faces = new(() => (Load("NotoSans-Bold.ttf"), Load("NotoSansArabic-Bold.ttf")));

    public static SKTypeface Latin => Faces.Value.Latin ?? throw new WatermarkTextException("The embedded Latin watermark font failed to load.");
    public static SKTypeface Arabic => Faces.Value.Arabic ?? throw new WatermarkTextException("The embedded Arabic watermark font failed to load.");

    private static SKTypeface? Load(string file)
    {
        using var stream = typeof(WatermarkFonts).Assembly.GetManifestResourceStream("Zaagl.Fonts." + file);
        if (stream is null) return null;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        using var data = SKData.CreateCopy(buffer.ToArray());
        return SKTypeface.FromData(data);
    }
}

/// <summary>
/// Lays a single line of watermark text out as one <see cref="SKTextBlob"/>: per-character font fallback (Arabic letters to Noto Sans
/// Arabic, everything else to Noto Sans when it has the glyph), HarfBuzz shaping per run (so Arabic letters join and RTL runs come out in
/// visual order) and a small bidi pass (paragraph direction from the first strong character; neutrals between two runs of the same
/// direction take it, otherwise the paragraph direction; digits stay left-to-right). Characters neither font covers (emoji, CJK, ...)
/// are dropped; text with nothing drawable left is a <see cref="WatermarkTextException"/>, never a silent text-less render.
/// </summary>
public static class WatermarkTextLayout
{
    private enum Dir { L, R, N }

    private sealed record Item(string Text, SKTypeface Face, Dir Dir);

    private sealed record Run(string Text, SKTypeface Face, bool Rtl);

    /// <summary>True when at least one visible character of <paramref name="text"/> can be drawn with the embedded fonts.</summary>
    public static bool CanRender(string text)
    {
        try
        {
            return Runs(text).Count > 0;
        }
        catch (WatermarkTextException)
        {
            return false;
        }
    }

    /// <summary>The runs' text in visual order (for tests and diagnostics).</summary>
    public static IReadOnlyList<string> VisualRuns(string text) => Runs(text).Select(r => r.Text).ToList();

    /// <summary>The shaped line at <paramref name="size"/> px and its advance width.</summary>
    public static (SKTextBlob Blob, float Width) Shape(string text, float size)
    {
        var runs = Runs(text);
        if (runs.Count == 0) throw new WatermarkTextException("The watermark text has no character the watermark fonts can draw.");
        using var builder = new SKTextBlobBuilder();
        var shapers = new Dictionary<SKTypeface, SKShaper>();
        var fonts = new Dictionary<SKTypeface, SKFont>();
        try
        {
            var x = 0f;
            var glyphs = 0;
            foreach (var run in runs)
            {
                if (!shapers.TryGetValue(run.Face, out var shaper)) shapers[run.Face] = shaper = new SKShaper(run.Face);
                if (!fonts.TryGetValue(run.Face, out var font)) fonts[run.Face] = font = new SKFont(run.Face, size) { Subpixel = true };
                var result = shaper.Shape(run.Text, x, 0, font);
                if (result.Codepoints.Length > 0)
                {
                    var ids = new ushort[result.Codepoints.Length];
                    for (var i = 0; i < ids.Length; i++) ids[i] = (ushort)result.Codepoints[i];
                    builder.AddPositionedRun(ids, font, result.Points);
                    glyphs += ids.Count(g => g != 0);
                }
                x += result.Width;
            }
            var blob = glyphs > 0 && x > 0 ? builder.Build() : null;
            if (blob is null) throw new WatermarkTextException("The watermark text shaped to no visible glyphs.");
            return (blob, x);
        }
        finally
        {
            foreach (var s in shapers.Values) s.Dispose();
            foreach (var f in fonts.Values) f.Dispose();
        }
    }

    /// <summary>The runs in visual (left-to-right drawing) order.</summary>
    private static List<Run> Runs(string text)
    {
        var latin = WatermarkFonts.Latin;
        var arabic = WatermarkFonts.Arabic;
        var items = new List<Item>();
        foreach (var rune in text.Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            var cp = rune.Value;
            if (cp is 0x200C or 0x200D) // ZWNJ / ZWJ steer Arabic joining: keep them inside the current run
            {
                if (items.Count > 0 && Has(items[^1].Face, cp)) items.Add(items[^1] with { Text = rune.ToString() });
                continue;
            }
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
                or UnicodeCategory.OtherNotAssigned)
                continue;
            var mark = category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.SpacingCombiningMark;
            var previous = items.Count > 0 ? items[^1] : null;
            if (mark && previous is null) continue;
            SKTypeface? face;
            Dir dir;
            if (mark)
            {
                // A combining mark (harakat, accents) stays with its base character's run.
                face = Has(previous!.Face, cp) ? previous.Face : null;
                dir = previous.Dir;
            }
            else if (IsArabic(cp))
            {
                face = Has(arabic, cp) ? arabic : null;
                dir = Rune.IsDigit(rune) ? Dir.L : Dir.R;
            }
            else
            {
                face = !Rune.IsLetter(rune) && previous is not null && Has(previous.Face, cp) ? previous.Face
                    : Has(latin, cp) ? latin
                    : Has(arabic, cp) ? arabic
                    : null;
                dir = Rune.IsLetter(rune) || Rune.IsDigit(rune) ? Dir.L : Dir.N;
            }
            if (face is null) continue; // emoji, CJK, ...: not covered by the embedded fonts, dropped (docs/phase2/approval-gate.md)
            items.Add(new Item(rune.ToString(), face, dir));
        }
        if (items.All(i => string.IsNullOrWhiteSpace(i.Text))) return [];

        var paragraph = items.FirstOrDefault(i => i.Dir != Dir.N)?.Dir ?? Dir.L;
        var resolved = new Dir[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Dir != Dir.N) { resolved[i] = items[i].Dir; continue; }
            Dir? before = null, after = null;
            for (var j = i - 1; j >= 0 && before is null; j--) if (items[j].Dir != Dir.N) before = items[j].Dir;
            for (var j = i + 1; j < items.Count && after is null; j++) if (items[j].Dir != Dir.N) after = items[j].Dir;
            resolved[i] = before is { } b && after is { } a && a == b ? a : paragraph;
        }

        // Logical runs: same resolved direction and font.
        var logical = new List<Run>();
        var sb = new StringBuilder();
        for (var i = 0; i < items.Count; i++)
        {
            sb.Append(items[i].Text);
            if (i == items.Count - 1 || resolved[i + 1] != resolved[i] || !ReferenceEquals(items[i + 1].Face, items[i].Face))
            {
                logical.Add(new Run(sb.ToString(), items[i].Face, resolved[i] == Dir.R));
                sb.Clear();
            }
        }

        // Visual order: direction segments in paragraph order (reversed for an RTL paragraph); runs inside an RTL segment reversed.
        // HarfBuzz already returns each RTL run's glyphs in visual order.
        var segments = new List<List<Run>>();
        foreach (var run in logical)
        {
            if (segments.Count > 0 && segments[^1][0].Rtl == run.Rtl) segments[^1].Add(run);
            else segments.Add([run]);
        }
        if (paragraph == Dir.R) segments.Reverse();
        var visual = new List<Run>();
        foreach (var segment in segments)
        {
            if (segment[0].Rtl) segment.Reverse();
            visual.AddRange(segment);
        }
        return visual;
    }

    private static bool IsArabic(int cp) =>
        cp is >= 0x0600 and <= 0x06FF or >= 0x0750 and <= 0x077F or >= 0x0870 and <= 0x08FF or >= 0xFB50 and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF;

    private static bool Has(SKTypeface face, int cp) => face.GetGlyph(cp) != 0;
}

/// <summary>
/// Phase 2 E2.2 (docs/phase2/approval-gate.md): burns a watermark into a downscaled copy of an image. Input is the bomb-safe output of
/// <see cref="ImageThumbnailer"/> at <see cref="WatermarkEdge"/> (the original is only ever read, through the same decode limits);
/// the drawing is two layers: a diagonal stripe lattice (dark and light, so it shows on any background) and the repeated text, shaped
/// with the embedded fonts (<see cref="WatermarkTextLayout"/>). Text that cannot be drawn throws <see cref="WatermarkTextException"/>:
/// the task then records a failed derivative and the approval preview stays <c>unavailable</c> instead of serving a text-less copy.
/// </summary>
public static class WatermarkRenderer
{
    /// <summary>Longest edge of the watermarked preview: big enough to judge a design, small enough not to replace the original.</summary>
    public const int WatermarkEdge = 1600;

    /// <param name="baseImage">The downscaled input image.</param>
    /// <param name="text">The watermark text.</param>
    /// <param name="drawText">False draws the stripe layer only (tests compare against it); production always draws the text.</param>
    public static (byte[] Bytes, string Mime, int Width, int Height)? Render(byte[] baseImage, string text, bool drawText = true)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(baseImage));
        if (codec is null) return null;
        using var bitmap = SKBitmap.Decode(codec);
        if (bitmap is null) return null;
        var w = bitmap.Width;
        var h = bitmap.Height;
        var unit = Math.Max(24f, Math.Min(w, h) / 6f);
        var fontSize = Math.Max(14f, unit / 2.2f);
        // Shape first: a text that cannot be drawn fails before any pixel is produced.
        (SKTextBlob? Blob, float Width) shaped = drawText ? WatermarkTextLayout.Shape(text, fontSize) : (null, 0f);
        using var surface = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        try
        {
            using var canvas = new SKCanvas(surface);
            canvas.DrawBitmap(bitmap, 0, 0);
            var diagonal = (float)Math.Sqrt((double)w * w + (double)h * h);

            canvas.Save();
            canvas.Translate(w / 2f, h / 2f);
            canvas.RotateDegrees(-30);

            // Layer 1: diagonal stripes, dark and light, strong enough to read on white, black and photos alike.
            using (var dark = new SKPaint { Color = new SKColor(0, 0, 0, 115), IsAntialias = true, StrokeWidth = Math.Max(3f, unit / 7f), Style = SKPaintStyle.Stroke })
            using (var light = new SKPaint { Color = new SKColor(255, 255, 255, 130), IsAntialias = true, StrokeWidth = Math.Max(2f, unit / 9f), Style = SKPaintStyle.Stroke })
            {
                for (var y = -diagonal; y <= diagonal; y += unit)
                {
                    canvas.DrawLine(-diagonal, y, diagonal, y, dark);
                    canvas.DrawLine(-diagonal, y + unit / 5f, diagonal, y + unit / 5f, light);
                }
            }

            // Layer 2: the text between the stripes, dark outline under a light fill, rows staggered by half a tile.
            if (shaped.Blob is { } blob)
            {
                using var outline = new SKPaint { Color = new SKColor(0, 0, 0, 170), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(2f, fontSize / 9f), StrokeJoin = SKStrokeJoin.Round };
                using var fill = new SKPaint { Color = new SKColor(255, 255, 255, 210), IsAntialias = true, Style = SKPaintStyle.Fill };
                var step = shaped.Width + unit;
                var row = 0;
                for (var y = -diagonal; y <= diagonal; y += unit, row++)
                {
                    var baseline = y + unit * 0.6f + fontSize * 0.35f;
                    for (var x = -diagonal - (row % 2) * step / 2f; x <= diagonal; x += step)
                    {
                        canvas.DrawText(blob, x, baseline, outline);
                        canvas.DrawText(blob, x, baseline, fill);
                    }
                }
            }
            canvas.Restore();
        }
        finally
        {
            shaped.Blob?.Dispose();
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
    public bool CanRender(string text) => WatermarkTextLayout.CanRender(text);

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
        catch (WatermarkTextException ex)
        {
            // Never fall back to a stripes-only copy: the approval preview stays unavailable and the failure is loud.
            reason = "text-unrenderable";
            logger.LogError("watermark_text_unrenderable: resource {ResourceId}: {Message}", resourceId, ex.Message);
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
