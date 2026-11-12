using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using SkiaSharp;
using Zaagl.Modules.Files.Application;
using Zaagl.Modules.Files.Infrastructure;

namespace Zaagl.Files.Tests;

/// <summary>
/// ARC-011 sections 2, 3.1 and 6 (E1.2 slice S3): the header probe and the thumbnailer on real PNG, JPEG, GIF and WebP bytes, the
/// decompression-bomb corpus, and the "the original is never touched" invariant (I1).
/// </summary>
public sealed class ImageProcessingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zaagl-image-tests-" + Guid.NewGuid().ToString("N"));
    private readonly MediaOptions _options = new();

    public ImageProcessingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ---- fixtures --------------------------------------------------------------------------------------------------

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>A gradient with a distinct red corner, so an orientation mistake is visible in the pixels.</summary>
    private static byte[] Encode(int width, int height, SKEncodedImageFormat format, bool alpha = false, int quality = 90)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, alpha ? SKAlphaType.Premul : SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(alpha ? new SKColor(0, 0, 255, 128) : SKColors.CornflowerBlue);
        using var red = new SKPaint { Color = SKColors.Red };
        canvas.DrawRect(0, 0, Math.Max(1, width / 4), Math.Max(1, height / 4), red);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);
        return data.ToArray();
    }

    /// <summary>Inserts an EXIF APP1 segment with the given orientation right after the JPEG start-of-image marker.</summary>
    private static byte[] WithExifOrientation(byte[] jpeg, ushort orientation)
    {
        byte[] app1 =
        [
            0xFF, 0xE1, 0x00, 0x22, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'M', (byte)'M', 0x00, 0x2A, 0, 0, 0, 8,
            0x00, 0x01, 0x01, 0x12, 0x00, 0x03, 0, 0, 0, 1, (byte)(orientation >> 8), (byte)orientation, 0, 0,
            0, 0, 0, 0,
        ];
        return [.. jpeg[..2], .. app1, .. jpeg[2..]];
    }

    private static byte[] Gif(int frames, bool transparent, int width = 2, int height = 2)
    {
        var bytes = new List<byte>("GIF89a"u8.ToArray());
        bytes.AddRange([(byte)width, 0, (byte)height, 0, 0x80, 0, 0]);
        bytes.AddRange([0, 0, 0, 255, 255, 255]); // global colour table: two entries
        for (var i = 0; i < frames; i++)
        {
            bytes.AddRange([0x21, 0xF9, 0x04, (byte)(transparent ? 0x01 : 0x00), 10, 0, 0, 0]);
            bytes.AddRange([0x2C, 0, 0, 0, 0, (byte)width, 0, (byte)height, 0, 0]);
            bytes.Add(2);
            // LZW with only literal codes: clear, one zero per pixel, end. The code width grows exactly as a decoder grows it.
            long acc = 0;
            var bits = 0;
            var packed = new List<byte>();
            var codeWidth = 3;
            var next = 6;
            void Emit(int code)
            {
                acc |= (long)code << bits;
                bits += codeWidth;
                while (bits >= 8) { packed.Add((byte)(acc & 0xFF)); acc >>= 8; bits -= 8; }
            }
            Emit(4);
            for (var p = 0; p < width * height; p++)
            {
                Emit(0);
                if (p > 0 && ++next == (1 << codeWidth) && codeWidth < 12) codeWidth++;
            }
            Emit(5);
            if (bits > 0) packed.Add((byte)(acc & 0xFF));
            bytes.Add((byte)packed.Count);
            bytes.AddRange(packed);
            bytes.Add(0);
        }
        bytes.Add(0x3B);
        return [.. bytes];
    }

    private static byte[] AnimatedWebP(int frames, int width, int height)
    {
        var body = new List<byte>("WEBP"u8.ToArray());
        byte[] vp8x = [0x12, 0, 0, 0, (byte)(width - 1), (byte)((width - 1) >> 8), (byte)((width - 1) >> 16), (byte)(height - 1), (byte)((height - 1) >> 8), (byte)((height - 1) >> 16)];
        body.AddRange("VP8X"u8.ToArray()); body.AddRange(BitConverter.GetBytes(10)); body.AddRange(vp8x);
        body.AddRange("ANIM"u8.ToArray()); body.AddRange(BitConverter.GetBytes(6)); body.AddRange(new byte[6]);
        for (var i = 0; i < frames; i++)
        {
            body.AddRange("ANMF"u8.ToArray()); body.AddRange(BitConverter.GetBytes(16)); body.AddRange(new byte[16]);
        }
        return [.. "RIFF"u8.ToArray(), .. BitConverter.GetBytes(body.Count), .. body];
    }

    /// <summary>A structurally valid PNG whose IHDR declares the given size and whose single IDAT holds zeros: a real decompression bomb
    /// (a few hundred KB on disk, hundreds of MB when inflated). 8-bit grey, no filter.</summary>
    private string BombPng(string name, int width, int height)
    {
        var path = Path.Combine(_dir, name);
        using var file = File.Create(path);
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8; ihdr[9] = 0;
        Chunk(file, "IHDR", ihdr);
        using var deflated = new MemoryStream();
        using (var z = new ZLibStream(deflated, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var row = new byte[width + 1];
            for (var y = 0; y < height; y++) z.Write(row);
        }
        Chunk(file, "IDAT", deflated.ToArray());
        Chunk(file, "IEND", []);
        return path;
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        s.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = 0xFFFFFFFFu;
        foreach (var b in typeBytes.Concat(data))
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        Span<byte> check = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(check, ~crc);
        s.Write(check);
    }

    /// <summary>A PNG header (and nothing else) that claims an absurd size: the probe must not need a body at all.</summary>
    private string HeaderOnlyPng(string name, uint width, uint height)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; ihdr[9] = 6;
        bytes.AddRange(ihdr);
        bytes.AddRange([0, 0, 0, 0]);
        return Write(name, [.. bytes]);
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    // ---- probe -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Probe_reads_a_png_without_alpha()
    {
        var h = ImageProbe.Read(Write("a.png", Encode(120, 80, SKEncodedImageFormat.Png)));
        Assert.NotNull(h);
        Assert.Equal(("image/png", 120L, 80L), (h.Mime, h.Width, h.Height));
        Assert.False(h.Animated);
    }

    [Fact]
    public void Probe_sees_png_alpha_from_the_colour_type()
    {
        var h = ImageProbe.Read(Write("a.png", Encode(64, 64, SKEncodedImageFormat.Png, alpha: true)));
        Assert.True(h!.HasAlpha);
    }

    [Fact]
    public void Probe_reads_a_jpeg_and_a_jpeg_never_has_alpha()
    {
        var h = ImageProbe.Read(Write("a.jpg", Encode(300, 200, SKEncodedImageFormat.Jpeg)));
        Assert.Equal(("image/jpeg", 300L, 200L, false), (h!.Mime, h.Width, h.Height, h.HasAlpha));
    }

    [Fact]
    public void Probe_reads_webp_with_and_without_alpha()
    {
        var plain = ImageProbe.Read(Write("a.webp", Encode(90, 60, SKEncodedImageFormat.Webp, quality: 80)));
        var alpha = ImageProbe.Read(Write("b.webp", Encode(90, 60, SKEncodedImageFormat.Webp, alpha: true, quality: 100)));
        Assert.Equal(("image/webp", 90L, 60L, false), (plain!.Mime, plain.Width, plain.Height, plain.HasAlpha));
        Assert.True(alpha!.HasAlpha);
        Assert.Equal((90L, 60L), (alpha.Width, alpha.Height));
    }

    [Fact]
    public void Probe_counts_gif_frames_and_transparency()
    {
        var still = ImageProbe.Read(Write("s.gif", Gif(1, transparent: false)));
        var moving = ImageProbe.Read(Write("m.gif", Gif(3, transparent: true)));
        Assert.Equal(("image/gif", 2L, 2L, false, false), (still!.Mime, still.Width, still.Height, still.Animated, still.HasAlpha));
        Assert.True(moving!.Animated);
        Assert.Equal(3, moving.FrameCount);
        Assert.True(moving.HasAlpha);
    }

    [Fact]
    public void Probe_detects_animated_webp_and_apng()
    {
        var webp = ImageProbe.Read(Write("m.webp", AnimatedWebP(4, 32, 16)));
        Assert.True(webp!.Animated);
        Assert.Equal(4, webp.FrameCount);
        Assert.Equal((32L, 16L), (webp.Width, webp.Height));

        // APNG: a PNG with an acTL chunk before the first IDAT.
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, 8); BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), 8); ihdr[8] = 8; ihdr[9] = 6;
        Chunk(ms, "IHDR", ihdr);
        var actl = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(actl, 5);
        Chunk(ms, "acTL", actl);
        Chunk(ms, "IDAT", [0]);
        var apng = ImageProbe.Read(Write("m.png", ms.ToArray()));
        Assert.True(apng!.Animated);
        Assert.Equal(5, apng.FrameCount);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF })]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 1 })]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 })]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 })]
    public void Probe_returns_null_for_truncated_or_foreign_bytes(byte[] bytes)
    {
        Assert.Null(ImageProbe.Read(Write("bad.bin", bytes)));
    }

    [Fact]
    public void Probe_survives_a_png_whose_chunk_lengths_point_past_the_end()
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, 10); BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), 10); ihdr[8] = 8; ihdr[9] = 2;
        bytes.AddRange(ihdr);
        bytes.AddRange([0, 0, 0, 0]);
        bytes.AddRange([0xFF, 0xFF, 0xFF, 0xF0, (byte)'z', (byte)'T', (byte)'X', (byte)'t']); // a 4 GB chunk
        // The declared size is still known; nothing throws and nothing loops.
        var header = ImageProbe.Read(Write("long.png", [.. bytes]));
        Assert.Equal((10L, 10L), (header!.Width, header.Height));
    }

    // ---- thumbnails ------------------------------------------------------------------------------------------------

    private ThumbnailResult Thumb(string path)
    {
        var header = ImageProbe.Read(path) ?? throw new InvalidOperationException("not probeable");
        return ImageThumbnailer.Create(path, header, _options);
    }

    private static (int Width, int Height, SKEncodedImageFormat Format) Decode(byte[] bytes)
    {
        using var codec = SKCodec.Create(new MemoryStream(bytes));
        Assert.NotNull(codec);
        return (codec.Info.Width, codec.Info.Height, codec.EncodedFormat);
    }

    [Fact]
    public void A_large_png_gets_a_webp_thumbnail_that_fits_in_320_and_keeps_the_aspect()
    {
        var result = Thumb(Write("big.png", Encode(1600, 800, SKEncodedImageFormat.Png)));
        Assert.Equal(ThumbnailOutcome.Created, result.Outcome);
        Assert.Equal("image/webp", result.Mime);
        Assert.Equal((320, 160), (result.Width, result.Height));
        Assert.Equal((320, 160, SKEncodedImageFormat.Webp), Decode(result.Bytes!));
    }

    [Fact]
    public void An_image_with_alpha_gets_a_png_thumbnail_with_its_transparency()
    {
        var result = Thumb(Write("alpha.png", Encode(640, 640, SKEncodedImageFormat.Png, alpha: true)));
        Assert.Equal("image/png", result.Mime);
        Assert.Equal((320, 320, SKEncodedImageFormat.Png), Decode(result.Bytes!));
        using var bitmap = SKBitmap.Decode(result.Bytes!);
        Assert.InRange(bitmap.GetPixel(300, 300).Alpha, 100, 160); // the half-transparent fill survived
    }

    [Fact]
    public void A_small_image_is_not_upscaled()
    {
        var result = Thumb(Write("small.png", Encode(100, 50, SKEncodedImageFormat.Png)));
        Assert.Equal((100, 50), (result.Width, result.Height));
    }

    [Fact]
    public void A_jpeg_is_thumbnailed_through_a_reduced_scale_decode()
    {
        var result = Thumb(Write("photo.jpg", Encode(4000, 3000, SKEncodedImageFormat.Jpeg)));
        Assert.Equal(ThumbnailOutcome.Created, result.Outcome);
        Assert.Equal((320, 240), (result.Width, result.Height));
    }

    [Theory]
    [InlineData(1, 200, 100)]
    [InlineData(3, 200, 100)]
    [InlineData(6, 100, 200)]
    [InlineData(8, 100, 200)]
    public void Exif_orientation_is_applied_to_the_derivative_only(ushort orientation, int expectedWidth, int expectedHeight)
    {
        var original = WithExifOrientation(Encode(200, 100, SKEncodedImageFormat.Jpeg), orientation);
        var path = Write("rot.jpg", original);
        var before = Sha(path);
        var result = Thumb(path);
        Assert.Equal(ThumbnailOutcome.Created, result.Outcome);
        Assert.Equal(orientation, result.Orientation);
        Assert.Equal((expectedWidth, expectedHeight), (result.Width, result.Height));
        Assert.Equal(before, Sha(path));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void Exif_orientation_6_puts_the_marked_corner_where_a_viewer_would_show_it()
    {
        // The source has a red block top-left. Orientation 6 (rotate 90 clockwise) moves it to the top-right.
        var result = Thumb(Write("rot6.jpg", WithExifOrientation(Encode(200, 100, SKEncodedImageFormat.Jpeg, quality: 100), 6)));
        using var bitmap = SKBitmap.Decode(result.Bytes!);
        var topRight = bitmap.GetPixel(bitmap.Width - 5, 5);
        var topLeft = bitmap.GetPixel(5, 5);
        Assert.True(topRight.Red > 200 && topRight.Blue < 100, $"top-right was {topRight}");
        Assert.True(topLeft.Blue > 150, $"top-left was {topLeft}");
    }

    [Fact]
    public void The_derivative_carries_no_metadata()
    {
        var jpeg = WithExifOrientation(Encode(500, 300, SKEncodedImageFormat.Jpeg), 1);
        var result = Thumb(Write("exif.jpg", jpeg));
        Assert.DoesNotContain("Exif", System.Text.Encoding.Latin1.GetString(result.Bytes!));
        Assert.DoesNotContain("EXIF", System.Text.Encoding.Latin1.GetString(result.Bytes!));
    }

    [Fact]
    public void A_gif_thumbnail_is_the_first_frame_and_the_original_is_untouched()
    {
        var path = Write("m.gif", Gif(2, transparent: false));
        var before = Sha(path);
        var result = Thumb(path);
        Assert.Equal(ThumbnailOutcome.Created, result.Outcome);
        Assert.Equal((2, 2), (result.Width, result.Height));
        Assert.Equal(before, Sha(path));
    }

    [Fact]
    public void Creating_a_thumbnail_never_changes_the_original_bytes_or_timestamp()
    {
        var path = Write("keep.png", Encode(900, 600, SKEncodedImageFormat.Png));
        var before = (Sha(path), File.GetLastWriteTimeUtc(path), new FileInfo(path).Length);
        Assert.Equal(ThumbnailOutcome.Created, Thumb(path).Outcome);
        Assert.Equal(before, (Sha(path), File.GetLastWriteTimeUtc(path), new FileInfo(path).Length));
    }

    // ---- bombs and damaged files: always "no thumbnail", never an exception -------------------------------------------

    [Fact]
    public void A_header_claiming_900_megapixels_is_skipped_from_the_header_in_under_two_seconds()
    {
        var path = HeaderOnlyPng("bomb-900mp.png", 30_000, 30_000);
        var rss = Process.GetCurrentProcess().WorkingSet64;
        var clock = Stopwatch.StartNew();
        var header = ImageProbe.Read(path);
        Assert.Equal((30_000L, 30_000L), (header!.Width, header.Height));
        var result = ImageThumbnailer.Create(path, header, _options);
        clock.Stop();
        Assert.Equal(ThumbnailOutcome.Skipped, result.Outcome);
        Assert.Equal("too-many-pixels", result.Reason);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"took {clock.Elapsed}");
        Assert.True(Process.GetCurrentProcess().WorkingSet64 - rss < 200L * 1024 * 1024, "the bomb must not be decoded");
    }

    [Fact]
    public void A_real_inflating_png_over_the_limit_is_skipped_without_decoding()
    {
        var path = BombPng("bomb-real.png", 12_000, 12_000); // 144 MP of zeros in about 150 KB
        Assert.True(new FileInfo(path).Length < 1_000_000);
        var clock = Stopwatch.StartNew();
        var result = Thumb(path);
        clock.Stop();
        Assert.Equal(ThumbnailOutcome.Skipped, result.Outcome);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"took {clock.Elapsed}");
    }

    [Fact]
    public void An_image_under_the_header_limit_but_over_the_decode_limit_is_skipped()
    {
        var path = HeaderOnlyPng("mid.png", 9_000, 9_000); // 81 MP: allowed to exist, too large to hold decoded
        var result = Thumb(path);
        Assert.Equal(ThumbnailOutcome.Skipped, result.Outcome);
        Assert.Equal("too-many-decoded-pixels", result.Reason);
    }

    [Fact]
    public void The_pixel_limits_come_from_configuration()
    {
        var path = Write("limit.png", Encode(200, 200, SKEncodedImageFormat.Png));
        var header = ImageProbe.Read(path)!;
        Assert.Equal(ThumbnailOutcome.Skipped, ImageThumbnailer.Create(path, header, new MediaOptions { MaxPixels = 39_999 }).Outcome);
        Assert.Equal(ThumbnailOutcome.Skipped, ImageThumbnailer.Create(path, header, new MediaOptions { MaxDecodePixels = 39_999 }).Outcome);
        Assert.Equal(ThumbnailOutcome.Created, ImageThumbnailer.Create(path, header, new MediaOptions { MaxPixels = 40_000, MaxDecodePixels = 40_000 }).Outcome);
    }

    [Fact]
    public void A_jpeg_declaring_a_huge_size_in_its_header_is_skipped_before_any_decoder()
    {
        // SOI, SOF0 for 60000 x 60000, then nothing.
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0B, 0x08, 0xEA, 0x60, 0xEA, 0x60, 0x01, 0x01, 0x11, 0x00];
        var path = Write("huge.jpg", jpeg);
        var header = ImageProbe.Read(path);
        Assert.Equal((60_000L, 60_000L), (header!.Width, header.Height));
        Assert.Equal("too-many-pixels", ImageThumbnailer.Create(path, header, _options).Reason);
    }

    [Fact]
    public void A_truncated_png_has_no_thumbnail_and_no_exception()
    {
        var full = Encode(800, 800, SKEncodedImageFormat.Png);
        var result = Thumb(Write("cut.png", full[..(full.Length / 2)]));
        Assert.NotEqual(ThumbnailOutcome.Created, result.Outcome);
        Assert.Null(result.Bytes);
    }

    [Fact]
    public void A_corrupt_body_after_a_valid_header_has_no_thumbnail_and_no_exception()
    {
        var full = Encode(300, 300, SKEncodedImageFormat.Png);
        for (var i = 60; i < full.Length - 20; i++) full[i] ^= 0x5A;
        var result = Thumb(Write("rot.png", full));
        Assert.NotEqual(ThumbnailOutcome.Created, result.Outcome);
    }

    [Fact]
    public void A_missing_file_is_a_failed_thumbnail_not_an_exception()
    {
        var header = new ImageHeader("image/png", 10, 10, false, false, null);
        Assert.Equal(ThumbnailOutcome.Failed, ImageThumbnailer.Create(Path.Combine(_dir, "nope.png"), header, _options).Outcome);
    }

    [Fact]
    public void Probe_never_opens_the_file_for_writing()
    {
        var path = Write("ro.png", Encode(50, 50, SKEncodedImageFormat.Png));
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.NotNull(ImageProbe.Read(path));
            Assert.Equal(ThumbnailOutcome.Created, Thumb(path).Outcome);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }
}
