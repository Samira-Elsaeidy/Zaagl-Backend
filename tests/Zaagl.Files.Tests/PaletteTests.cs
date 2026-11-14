using SkiaSharp;
using Zaagl.Modules.Files.Application;

namespace Zaagl.Files.Tests;

/// <summary>Phase 2 E2.2 (docs/phase2/palette-and-comments.md): deterministic, bounded palette extraction.</summary>
public sealed class PaletteTests
{
    private static byte[] Halves(SKColor left, SKColor right, int w = 200, int h = 100, SKColor? stripe = null)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var l = new SKPaint { Color = left };
            using var r = new SKPaint { Color = right };
            canvas.DrawRect(0, 0, w / 2f, h, l);
            canvas.DrawRect(w / 2f, 0, w / 2f, h, r);
            if (stripe is { } s)
            {
                using var p = new SKPaint { Color = s };
                canvas.DrawRect(0, 0, w, h / 10f, p);
            }
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public void Two_flat_halves_give_two_colours_with_half_the_share_each()
    {
        var palette = PaletteExtractor.FromEncoded(Halves(new SKColor(255, 0, 0), new SKColor(0, 0, 255)))!;
        Assert.Equal(2, palette.Count);
        Assert.Contains(palette, c => c.Hex == "#FF0000" && Math.Abs(c.Share - 0.5) < 0.05);
        Assert.Contains(palette, c => c.Hex == "#0000FF" && Math.Abs(c.Share - 0.5) < 0.05);
        Assert.Equal(1.0, palette.Sum(c => c.Share), 2);
    }

    [Fact]
    public void The_palette_is_deterministic_and_ordered_by_share()
    {
        var bytes = Halves(new SKColor(20, 160, 90), new SKColor(240, 230, 200), 333, 211, new SKColor(10, 10, 10));
        var a = PaletteExtractor.FromEncoded(bytes)!;
        var b = PaletteExtractor.FromEncoded(bytes)!;
        Assert.Equal(a, b);
        Assert.True(a.Count is >= 3 and <= PaletteExtractor.MaxColors);
        Assert.Equal(a.OrderByDescending(c => c.Share).Select(c => c.Hex), a.Select(c => c.Hex));
        Assert.All(a, c => Assert.Matches("^#[0-9A-F]{6}$", c.Hex));
    }

    [Fact]
    public void Transparent_or_undecodable_images_have_no_palette()
    {
        Assert.Null(PaletteExtractor.FromEncoded(Halves(SKColors.Transparent, SKColors.Transparent)));
        Assert.Null(PaletteExtractor.FromEncoded("not an image"u8.ToArray()));
    }
}
