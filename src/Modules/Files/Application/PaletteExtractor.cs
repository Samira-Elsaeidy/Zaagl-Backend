using SkiaSharp;
using Zaagl.Modules.Files.Contracts;

namespace Zaagl.Modules.Files.Application;

/// <summary>
/// Phase 2 E2.2 (docs/phase2/palette-and-comments.md): the dominant colours of an image. Runs in the media-preview task on the
/// thumbnail it just made (at most 320 px a side, so the decode is small and bomb-safe: the original is never decoded here), samples at
/// most <see cref="MaxSamples"/> opaque pixels on a fixed grid, and clusters them with weighted k-means over a 12-bit colour histogram.
/// Deterministic: the same image always gives the same palette (fixed sampling grid, farthest-point seeding with index tie-breaks, a
/// fixed number of iterations). Pure CPU, bounded by the task's wall-clock limit.
/// </summary>
public static class PaletteExtractor
{
    public const int MaxColors = 6;
    public const int MaxSamples = 4096;
    private const int Iterations = 12;

    public static IReadOnlyList<PaletteColor>? FromEncoded(byte[] encoded, int colors = MaxColors)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(encoded));
        if (codec is null) return null;
        using var bitmap = SKBitmap.Decode(codec);
        if (bitmap is null || bitmap.Width < 1 || bitmap.Height < 1 || bitmap.Width > 4096 || bitmap.Height > 4096) return null;
        return FromBitmap(bitmap, colors);
    }

    public static IReadOnlyList<PaletteColor>? FromBitmap(SKBitmap bitmap, int colors = MaxColors)
    {
        colors = Math.Clamp(colors, 1, MaxColors);
        var total = (long)bitmap.Width * bitmap.Height;
        var step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)total / MaxSamples)));

        // 12-bit histogram (4 bits per channel), keeping the channel sums for an exact bucket mean.
        var count = new long[4096];
        var sumR = new long[4096];
        var sumG = new long[4096];
        var sumB = new long[4096];
        long samples = 0;
        for (var y = step / 2; y < bitmap.Height; y += step)
        {
            for (var x = step / 2; x < bitmap.Width; x += step)
            {
                var c = bitmap.GetPixel(x, y);
                if (c.Alpha < 128) continue;
                var bucket = (c.Red >> 4) << 8 | (c.Green >> 4) << 4 | (c.Blue >> 4);
                count[bucket]++;
                sumR[bucket] += c.Red;
                sumG[bucket] += c.Green;
                sumB[bucket] += c.Blue;
                samples++;
            }
        }
        if (samples == 0) return null;

        var points = new List<(double R, double G, double B, long W)>();
        for (var i = 0; i < 4096; i++)
            if (count[i] > 0) points.Add(((double)sumR[i] / count[i], (double)sumG[i] / count[i], (double)sumB[i] / count[i], count[i]));
        var k = Math.Min(colors, points.Count);

        // Seeding: the heaviest bucket, then repeatedly the point farthest (weighted) from every chosen centre.
        var centres = new List<(double R, double G, double B)>();
        var first = 0;
        for (var i = 1; i < points.Count; i++) if (points[i].W > points[first].W) first = i;
        centres.Add((points[first].R, points[first].G, points[first].B));
        while (centres.Count < k)
        {
            var best = -1;
            var bestScore = -1.0;
            for (var i = 0; i < points.Count; i++)
            {
                var d = centres.Min(c => Distance(points[i], c));
                var score = d * Math.Sqrt(points[i].W);
                if (score > bestScore) { bestScore = score; best = i; }
            }
            if (best < 0 || bestScore <= 0) break;
            centres.Add((points[best].R, points[best].G, points[best].B));
        }

        var assignment = new int[points.Count];
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            for (var i = 0; i < points.Count; i++)
            {
                var nearest = 0;
                var nearestD = double.MaxValue;
                for (var c = 0; c < centres.Count; c++)
                {
                    var d = Distance(points[i], centres[c]);
                    if (d < nearestD) { nearestD = d; nearest = c; }
                }
                assignment[i] = nearest;
            }
            for (var c = 0; c < centres.Count; c++)
            {
                double r = 0, g = 0, b = 0;
                long w = 0;
                for (var i = 0; i < points.Count; i++)
                {
                    if (assignment[i] != c) continue;
                    r += points[i].R * points[i].W; g += points[i].G * points[i].W; b += points[i].B * points[i].W; w += points[i].W;
                }
                if (w > 0) centres[c] = (r / w, g / w, b / w);
            }
        }

        var weights = new long[centres.Count];
        for (var i = 0; i < points.Count; i++) weights[assignment[i]] += points[i].W;
        return centres
            .Select((c, i) => (Colour: c, Weight: weights[i], Index: i))
            .Where(x => x.Weight > 0)
            .OrderByDescending(x => x.Weight).ThenBy(x => x.Index)
            .Select(x =>
            {
                var r = (int)Math.Round(x.Colour.R);
                var g = (int)Math.Round(x.Colour.G);
                var b = (int)Math.Round(x.Colour.B);
                return new PaletteColor($"#{r:X2}{g:X2}{b:X2}", r, g, b, Math.Round((double)x.Weight / samples, 4));
            })
            .ToList();
    }

    private static double Distance((double R, double G, double B, long W) p, (double R, double G, double B) c)
    {
        // Weighted RGB distance ("redmean" approximation of perceived difference), squared.
        var rMean = (p.R + c.R) / 2;
        var dr = p.R - c.R;
        var dg = p.G - c.G;
        var db = p.B - c.B;
        return (2 + rMean / 256) * dr * dr + 4 * dg * dg + (2 + (255 - rMean) / 256) * db * db;
    }
}
