using System.Text;
using Zaagl.Modules.Files.Application;

namespace Zaagl.Files.Tests;

/// <summary>ARC-009 section 9: type detection and the file name sanitiser (QA-021, QA-026 vectors).</summary>
public sealed class UploadPrimitivesTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D];

    [Fact]
    public void The_four_allowed_image_types_are_detected_by_their_bytes()
    {
        Assert.Equal("image/png", MediaSniffer.Detect(Png)!.Mime);
        Assert.Equal("image/jpeg", MediaSniffer.Detect([0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10])!.Mime);
        Assert.Equal("image/gif", MediaSniffer.Detect(Encoding.Latin1.GetBytes("GIF89a\u0001\0\u0001\0"))!.Mime);
        Assert.Equal("image/gif", MediaSniffer.Detect(Encoding.Latin1.GetBytes("GIF87a\u0001\0\u0001\0"))!.Mime);
        Assert.Equal("image/webp", MediaSniffer.Detect(Encoding.Latin1.GetBytes("RIFF$\0\0\0WEBPVP8 "))!.Mime);
        Assert.All(MediaSniffer.MediaAllowlist, mime => Assert.True(MediaSniffer.IsMediaAllowed(mime)));
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("<html><body>hi</body></html>")]
    [InlineData("MZ\u0090\0\u0003\0\0\0")]
    [InlineData("PK\u0003\u0004\u0014\0\0\0")]
    [InlineData("%PDF-1.7")]
    [InlineData("RIFF$\0\0\0WAVEfmt ")] // RIFF but not WebP
    [InlineData("")]
    public void Everything_else_is_unknown_and_never_media(string content)
    {
        var detected = MediaSniffer.Detect(Encoding.Latin1.GetBytes(content));
        Assert.Null(detected);
        Assert.False(MediaSniffer.IsMediaAllowed(detected?.Mime));
    }

    [Fact]
    public void A_png_with_a_trailing_zip_or_html_is_still_a_png()
    {
        var polyglot = Png.Concat(Encoding.ASCII.GetBytes("<html><script>alert(1)</script></html>PK\u0003\u0004")).ToArray();
        Assert.Equal("image/png", MediaSniffer.Detect(polyglot)!.Mime);
    }

    [Theory]
    [InlineData(@"..\..\evil.png", ".png", "evil.png")]
    [InlineData("../../etc/passwd", ".png", "passwd.png")]
    [InlineData("CON.png", ".png", "_CON.png")]
    [InlineData("nul", ".png", "_nul.png")]
    [InlineData("name. .", ".png", "name.png")]
    [InlineData("", ".png", "file.png")]
    [InlineData(null, ".png", "file.png")]
    [InlineData("a:b*c?.png", ".png", "a_b_c_.png")]
    [InlineData("photo.exe", ".png", "photo.exe.png")]
    [InlineData("photo.JPEG", ".jpg", "photo.JPEG")]
    [InlineData(".htaccess", ".png", "htaccess.png")]
    public void File_names_are_sanitised_for_display(string? declared, string ext, string expected) =>
        Assert.Equal(expected, FileNames.Sanitize(declared, ext));

    [Fact]
    public void Bidi_overrides_and_control_characters_are_removed()
    {
        // U+202E (right-to-left override) used to disguise "exe.jpg" as "gpj.exe".
        var name = FileNames.Sanitize("a‮gpj.exe\u0000", ".png");
        Assert.DoesNotContain('‮', name);
        Assert.DoesNotContain('\0', name);
        Assert.Equal("agpj.exe.png", name);
    }

    [Fact]
    public void A_very_long_name_is_cut_to_255_utf8_bytes_on_a_character_boundary_keeping_the_extension()
    {
        var name = FileNames.Sanitize(new string('é', 400) + ".png", ".png");
        Assert.True(Encoding.UTF8.GetByteCount(name) <= 255);
        Assert.EndsWith(".png", name);
        Assert.DoesNotContain('�', name);
    }

    [Fact]
    public void Tus_metadata_decodes_base64_values_and_refuses_malformed_headers()
    {
        var header = $"filename {Convert.ToBase64String(Encoding.UTF8.GetBytes("pic é.png"))},kind {Convert.ToBase64String("media"u8.ToArray())},empty";
        var parsed = FileNames.ParseTusMetadata(header)!;
        Assert.Equal("pic é.png", parsed["filename"]);
        Assert.Equal("media", parsed["kind"]);
        Assert.Equal("", parsed["empty"]);
        Assert.Null(FileNames.ParseTusMetadata("filename !!!notbase64"));
        Assert.Null(FileNames.ParseTusMetadata("kind bWVkaWE=,kind bWVkaWE="));
    }
}
