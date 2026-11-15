using SkiaSharp;
using SkiaSharp.HarfBuzz;
using Zaagl.Modules.Files.Application;

namespace Zaagl.Files.Tests;

/// <summary>
/// Phase 2 bug-fix (watermark text never drawn): the worker image has no system fonts, so <c>SKTypeface.Default</c> measured every
/// string as 0 and the text layer was silently skipped. The renderer now uses fonts embedded in the assembly, shapes with HarfBuzz
/// (Arabic joins, RTL runs in visual order) and throws instead of producing a text-less copy. These tests never touch system fonts.
/// </summary>
public sealed class WatermarkRendererTests
{
    private const int Width = 900;
    private const int Height = 600;

    private static byte[] Background(SKColor color)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static SKBitmap Decode(byte[] bytes) => SKBitmap.Decode(bytes) ?? throw new InvalidOperationException("undecodable render");

    private static double DifferingFraction(SKBitmap a, SKBitmap b, int threshold = 40)
    {
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        long differing = 0;
        for (var y = 0; y < a.Height; y += 2)
        for (var x = 0; x < a.Width; x += 2)
        {
            var p = a.GetPixel(x, y);
            var q = b.GetPixel(x, y);
            if (Math.Abs(p.Red - q.Red) + Math.Abs(p.Green - q.Green) + Math.Abs(p.Blue - q.Blue) >= threshold) differing++;
        }
        return differing / (double)((a.Width + 1) / 2 * ((a.Height + 1) / 2));
    }

    private static double NonBackgroundFraction(SKBitmap bitmap, SKColor background, int threshold = 40)
    {
        long differing = 0;
        for (var y = 0; y < bitmap.Height; y += 2)
        for (var x = 0; x < bitmap.Width; x += 2)
        {
            var p = bitmap.GetPixel(x, y);
            if (Math.Abs(p.Red - background.Red) + Math.Abs(p.Green - background.Green) + Math.Abs(p.Blue - background.Blue) >= threshold) differing++;
        }
        return differing / (double)((bitmap.Width + 1) / 2 * ((bitmap.Height + 1) / 2));
    }

    /// <summary>Set ZAAGL_WATERMARK_DUMP to a folder to look at the renders.</summary>
    private static void Dump(string name, byte[] bytes)
    {
        if (Environment.GetEnvironmentVariable("ZAAGL_WATERMARK_DUMP") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        var safe = string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        using var bitmap = Decode(bytes);
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(dir, safe + ".png"), png.ToArray());
    }

    [Fact]
    public void Embedded_fonts_load_without_any_system_font_and_cover_latin_and_arabic()
    {
        Assert.NotEqual(0, WatermarkFonts.Latin.GetGlyph('D'));
        Assert.NotEqual(0, WatermarkFonts.Latin.GetGlyph('é'));
        Assert.NotEqual(0, WatermarkFonts.Arabic.GetGlyph('م'));
        Assert.NotEqual(0, WatermarkFonts.Arabic.GetGlyph('ة'));
        Assert.Contains("Noto Sans", WatermarkFonts.Latin.FamilyName);
        Assert.Contains("Arabic", WatermarkFonts.Arabic.FamilyName);
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("مسودة")]
    [InlineData("مسودة DRAFT")]
    [InlineData("DRAFT - ACME 2026")]
    public void Text_layer_is_drawn_on_top_of_the_stripes(string text)
    {
        foreach (var color in new[] { new SKColor(128, 128, 128), SKColors.White, SKColors.Black })
        {
            var input = Background(color);
            var withText = WatermarkRenderer.Render(input, text) ?? throw new InvalidOperationException("no render");
            var stripesOnly = WatermarkRenderer.Render(input, text, drawText: false) ?? throw new InvalidOperationException("no render");
            Assert.Equal("image/webp", withText.Mime);
            Dump($"{text}-{color}", withText.Bytes);
            using var a = Decode(withText.Bytes);
            using var b = Decode(stripesOnly.Bytes);

            // The text adds a substantial number of pixels beyond the stripes (glyph fill + outline), on any background.
            var textPixels = DifferingFraction(a, b);
            Assert.True(textPixels > 0.04, $"text layer changed only {textPixels:P1} of the pixels for '{text}' on {color}");
            // And the whole mark (stripes + text) covers a large part of the copy.
            var marked = NonBackgroundFraction(a, color);
            Assert.True(marked > 0.15, $"only {marked:P1} of the preview is marked for '{text}' on {color}");
        }
    }

    [Fact]
    public void Stripe_layer_alone_is_clearly_visible()
    {
        foreach (var color in new[] { new SKColor(128, 128, 128), SKColors.White, SKColors.Black })
        {
            var stripes = WatermarkRenderer.Render(Background(color), "DRAFT", drawText: false) ?? throw new InvalidOperationException("no render");
            using var bitmap = Decode(stripes.Bytes);
            var marked = NonBackgroundFraction(bitmap, color);
            Assert.True(marked > 0.09, $"stripes mark only {marked:P1} of a {color} image");
        }
    }

    [Fact]
    public void Arabic_is_shaped_with_joined_contextual_forms()
    {
        const string word = "مسودة";
        using var font = new SKFont(WatermarkFonts.Arabic, 64);
        using var shaper = new SKShaper(WatermarkFonts.Arabic);
        var joined = shaper.Shape(word, font).Codepoints;
        // ZWNJ between the letters forces their non-joining (isolated/final) forms.
        var isolated = shaper.Shape(string.Join('‌', word.ToCharArray()), font).Codepoints.Where(g => g != WatermarkFonts.Arabic.GetGlyph(0x200C)).ToArray();
        Assert.DoesNotContain(0u, joined);
        // Initial/medial/final forms differ from the isolated glyphs: the letters connect.
        Assert.NotEqual(isolated, joined);

        var (blob, width) = WatermarkTextLayout.Shape(word, 64);
        using (blob) Assert.True(width > 0);
    }

    [Fact]
    public void Mixed_text_is_laid_out_in_visual_order()
    {
        // RTL paragraph (first strong character is Arabic): the Latin word is drawn to the left of the Arabic one.
        Assert.Equal(["DRAFT", "مسودة"], WatermarkTextLayout.VisualRuns("مسودة DRAFT").Select(r => r.Trim()).Where(r => r.Length > 0));
        // LTR paragraph: logical order is kept.
        Assert.Equal(["DRAFT", "مسودة"], WatermarkTextLayout.VisualRuns("DRAFT مسودة").Select(r => r.Trim()).Where(r => r.Length > 0));
        // Pure Arabic with a digit run: the digits stay left-to-right as one run.
        Assert.Contains("2026", WatermarkTextLayout.VisualRuns("مسودة 2026").Select(r => r.Trim()));
    }

    [Fact]
    public void Emoji_are_dropped_and_text_without_drawable_characters_fails_loudly()
    {
        Assert.True(WatermarkTextLayout.CanRender("DRAFT 🔒"));
        Assert.DoesNotContain(WatermarkTextLayout.VisualRuns("DRAFT 🔒"), r => r.Contains("🔒"));

        Assert.False(WatermarkTextLayout.CanRender("🔒🔒"));
        Assert.False(WatermarkTextLayout.CanRender("   "));
        Assert.False(WatermarkTextLayout.CanRender("​‎"));
        Assert.Throws<WatermarkTextException>(() => WatermarkRenderer.Render(Background(SKColors.White), "🔒🔒"));
    }
}
