using SkiaSharp;
using Zaagl.Modules.Files.Infrastructure;

namespace Zaagl.Modules.Files.Application;

public enum ThumbnailOutcome { Created, Skipped, Failed }

/// <param name="Orientation">The original EXIF orientation value 1-8 (1 when none or unknown).</param>
/// <param name="Reason">Short machine reason for Skipped and Failed (never shown to a visitor).</param>
public sealed record ThumbnailResult(
    ThumbnailOutcome Outcome, byte[]? Bytes, string? Mime, int Width, int Height, int Orientation, string? Reason)
{
    public static ThumbnailResult Skip(string reason, int orientation = 1) => new(ThumbnailOutcome.Skipped, null, null, 0, 0, orientation, reason);
    public static ThumbnailResult Fail(string reason, int orientation = 1) => new(ThumbnailOutcome.Failed, null, null, 0, 0, orientation, reason);
}

/// <summary>
/// The <c>thumb-320</c> derivative (ARC-011 sections 2, 3 and 6). Reads the original by path through a read-only native decoder and
/// returns encoded bytes in memory; it has no code path that writes to the original. Decompression-bomb bounds, in order:
/// the header pixel count against <see cref="MediaOptions.MaxPixels"/> (no decoder is created above it), the decoded pixel count
/// against <see cref="MediaOptions.MaxDecodePixels"/> (JPEG decodes at a reduced scale, so only the scaled size counts), and the
/// requirement that the decoder's own dimensions equal the header's. Anything unexpected is a "no thumbnail" result, never an
/// exception: the original stays served (invariant I4). Output carries no metadata, is converted to sRGB and rotated upright; it is
/// WebP at the configured quality, or PNG when the image has alpha.
/// </summary>
public static class ImageThumbnailer
{
    public static ThumbnailResult Create(string path, ImageHeader header, MediaOptions options)
    {
        try
        {
            // Gate 1: the declared size, from the header, before any decoder.
            if (header.Width * header.Height > options.MaxPixels) return ThumbnailResult.Skip("too-many-pixels");
            // Everything but JPEG has no reduced-scale decode: the full size is what memory will hold, known from the header already.
            if (header.Mime != "image/jpeg" && header.Width * header.Height > options.MaxDecodePixels) return ThumbnailResult.Skip("too-many-decoded-pixels");

            using var codec = SKCodec.Create(path, out var created);
            if (codec is null || created is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return ThumbnailResult.Fail("undecodable");
            var origin = (int)codec.EncodedOrigin is >= 1 and <= 8 ? (int)codec.EncodedOrigin : 1;
            var info = codec.Info;
            if (info.Width != header.Width || info.Height != header.Height) return ThumbnailResult.Fail("dimension-mismatch", origin);

            // Gate 2: what the decode will hold in memory. JPEG is decoded at 1/2, 1/4 or 1/8 when the target allows it.
            var width = info.Width;
            var height = info.Height;
            if (header.Mime == "image/jpeg")
            {
                foreach (var scale in new[] { 0.125f, 0.25f, 0.5f })
                {
                    if (Math.Max(info.Width, info.Height) * scale < options.ThumbnailEdge * 2) continue;
                    var scaled = codec.GetScaledDimensions(scale);
                    if (scaled.Width > 0 && scaled.Height > 0 && scaled.Width <= info.Width && scaled.Height <= info.Height)
                    {
                        width = scaled.Width;
                        height = scaled.Height;
                        break;
                    }
                }
            }
            if ((long)width * height > options.MaxDecodePixels) return ThumbnailResult.Skip("too-many-decoded-pixels", origin);

            var alphaType = header.HasAlpha ? SKAlphaType.Premul : SKAlphaType.Opaque;
            using var srgb = SKColorSpace.CreateSrgb();
            var decodeInfo = new SKImageInfo(width, height, SKColorType.Rgba8888, alphaType, srgb);
            using var decoded = new SKBitmap(decodeInfo);
            // Only a complete decode is accepted: a truncated image has no thumbnail rather than a half-grey one.
            if (codec.GetPixels(decodeInfo, decoded.GetPixels()) != SKCodecResult.Success) return ThumbnailResult.Fail("decode-failed", origin);

            // Scale (never up) in the stored orientation, then rotate upright.
            var longest = Math.Max(width, height);
            var factor = Math.Min(1d, (double)options.ThumbnailEdge / longest);
            var targetWidth = Math.Max(1, (int)Math.Round(width * factor));
            var targetHeight = Math.Max(1, (int)Math.Round(height * factor));
            SKBitmap scaledBitmap = decoded;
            SKBitmap? resized = null;
            if (targetWidth != width || targetHeight != height)
            {
                resized = decoded.Resize(new SKImageInfo(targetWidth, targetHeight, SKColorType.Rgba8888, alphaType, srgb),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                if (resized is null) return ThumbnailResult.Fail("resize-failed", origin);
                scaledBitmap = resized;
            }
            using var upright = Orient(scaledBitmap, origin, alphaType, srgb);
            resized?.Dispose();

            using var image = SKImage.FromBitmap(upright);
            var format = header.HasAlpha ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Webp;
            using var data = image.Encode(format, header.HasAlpha ? 100 : Math.Clamp(options.WebpQuality, 1, 100));
            if (data is null || data.Size == 0) return ThumbnailResult.Fail("encode-failed", origin);
            return new ThumbnailResult(ThumbnailOutcome.Created, data.ToArray(), header.HasAlpha ? "image/png" : "image/webp",
                upright.Width, upright.Height, origin, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ThumbnailResult.Fail("exception:" + ex.GetType().Name);
        }
    }

    /// <summary>Applies the EXIF orientation (1-8) with the same matrices Skia uses; 1 returns a copy of the pixels.</summary>
    internal static SKBitmap Orient(SKBitmap source, int origin, SKAlphaType alphaType, SKColorSpace colorSpace)
    {
        float w = source.Width, h = source.Height;
        var swap = origin >= 5;
        var target = new SKBitmap(new SKImageInfo(swap ? source.Height : source.Width, swap ? source.Width : source.Height,
            SKColorType.Rgba8888, alphaType, colorSpace));
        using var canvas = new SKCanvas(target);
        canvas.Clear(alphaType == SKAlphaType.Opaque ? SKColors.Black : SKColors.Transparent);
        // x' = a*x + b*y + tx; y' = c*x + d*y + ty.
        var (a, b, tx, c, d, ty) = origin switch
        {
            2 => (-1f, 0f, w, 0f, 1f, 0f),     // mirrored horizontally
            3 => (-1f, 0f, w, 0f, -1f, h),     // rotated 180
            4 => (1f, 0f, 0f, 0f, -1f, h),     // mirrored vertically
            5 => (0f, 1f, 0f, 1f, 0f, 0f),     // transposed
            6 => (0f, -1f, h, 1f, 0f, 0f),     // rotated 90 clockwise
            7 => (0f, -1f, h, -1f, 0f, w),     // transversed
            8 => (0f, 1f, 0f, -1f, 0f, w),     // rotated 90 counter-clockwise
            _ => (1f, 0f, 0f, 0f, 1f, 0f),
        };
        canvas.SetMatrix(new SKMatrix { ScaleX = a, SkewX = b, TransX = tx, SkewY = c, ScaleY = d, TransY = ty, Persp2 = 1 });
        canvas.DrawBitmap(source, 0, 0);
        return target;
    }

}
