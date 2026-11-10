namespace Zaagl.Modules.Files.Application;

/// <param name="Mime">The detected type: the only type any decision or response header ever uses.</param>
/// <param name="Extension">With the dot, lower case.</param>
public sealed record DetectedType(string Mime, string Extension);

/// <summary>
/// Type detection on the first 64 KB (ARC-009 section 9.1, addendum 7.2). The declared type and the file name are never
/// inputs. Slice 1 recognises exactly the media allowlist (PNG, JPEG, GIF, WebP) by magic numbers; every other content is
/// "unknown" (null), which <c>/complete</c> refuses for media. A polyglot (a valid PNG with a trailing ZIP) is its first
/// type: it is only ever served as that type, never as markup (ARC-008 section 7).
/// </summary>
public static class MediaSniffer
{
    /// <summary>Bytes the detector looks at.</summary>
    public const int HeadBytes = 64 * 1024;

    public static readonly IReadOnlyList<string> MediaAllowlist = ["image/png", "image/jpeg", "image/gif", "image/webp"];

    public static DetectedType? Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 8 && head[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            return new DetectedType("image/png", ".png");
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
            return new DetectedType("image/jpeg", ".jpg");
        if (head.Length >= 6 && (head[..6].SequenceEqual("GIF87a"u8) || head[..6].SequenceEqual("GIF89a"u8)))
            return new DetectedType("image/gif", ".gif");
        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head.Slice(8, 4).SequenceEqual("WEBP"u8))
            return new DetectedType("image/webp", ".webp");
        return null;
    }

    public static bool IsMediaAllowed(string? mime) =>
        mime is not null && MediaAllowlist.Contains(mime, StringComparer.Ordinal);
}
