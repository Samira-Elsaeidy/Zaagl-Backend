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

    // ---- Phase 2 E2.2 viewer types (docs/phase2/viewers-3d-lottie-audio.md) ----------------------------------------------------

    public const string GlbMime = "model/gltf-binary";
    public const string GltfJsonMime = "model/gltf+json";
    public const string LottieMime = "application/json";

    /// <summary>Audio types recognised by magic number (the browser decoder is the parser; nothing executes).</summary>
    public static readonly IReadOnlySet<string> AudioTypes =
        new HashSet<string>(StringComparer.Ordinal) { "audio/mpeg", "audio/wav", "audio/flac", "audio/ogg", "audio/aac", "audio/mp4" };

    /// <summary>Video containers recognised by their header (screen recordings of bug capsules). Addendum 4.4 allows video inline.</summary>
    public static readonly IReadOnlySet<string> VideoTypes = new HashSet<string>(StringComparer.Ordinal) { "video/mp4", "video/webm" };

    private static readonly string[] Mp4Brands = ["isom", "iso2", "iso4", "iso5", "iso6", "mp41", "mp42", "avc1", "dash", "M4V ", "MSNV"];

    /// <summary>
    /// Candidate viewer types from the first 64 KB. A candidate is not trusted yet: the scan task runs <see cref="ViewerValidator"/>
    /// on the whole file and rejects or downgrades it. The declared type and the name are never inputs.
    /// </summary>
    public static DetectedType? DetectViewerType(ReadOnlySpan<byte> head)
    {
        // glTF 2.0 binary: "glTF", version 2, total length, then the first chunk (length, type "JSON").
        if (head.Length >= 20 && head[..4].SequenceEqual("glTF"u8)
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(head[4..]) == 2
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(head[8..]) >= 20
            && head.Slice(16, 4).SequenceEqual("JSON"u8))
            return new DetectedType(GlbMime, ".glb");

        if (head.Length >= 3 && head[..3].SequenceEqual("ID3"u8)) return new DetectedType("audio/mpeg", ".mp3");
        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head.Slice(8, 4).SequenceEqual("WAVE"u8)) return new DetectedType("audio/wav", ".wav");
        if (head.Length >= 4 && head[..4].SequenceEqual("fLaC"u8)) return new DetectedType("audio/flac", ".flac");
        if (head.Length >= 4 && head[..4].SequenceEqual("OggS"u8)) return new DetectedType("audio/ogg", ".ogg");
        if (head.Length >= 12 && head.Slice(4, 4).SequenceEqual("ftyp"u8)
            && (head.Slice(8, 4).SequenceEqual("M4A "u8) || head.Slice(8, 4).SequenceEqual("M4B "u8)))
            return new DetectedType("audio/mp4", ".m4a");
        if (head.Length >= 12 && head.Slice(4, 4).SequenceEqual("ftyp"u8)
            && Mp4Brands.Contains(System.Text.Encoding.ASCII.GetString(head.Slice(8, 4)), StringComparer.Ordinal))
            return new DetectedType("video/mp4", ".mp4");
        // WebM: an EBML header whose DocType is "webm" (Matroska proper is not accepted).
        if (head.Length >= 4 && head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3
            && head[..Math.Min(head.Length, 64)].IndexOf("webm"u8) > 0)
            return new DetectedType("video/webm", ".webm");
        if (head.Length >= 7 && head[0] == 0xFF && (head[1] & 0xF6) == 0xF0) return new DetectedType("audio/aac", ".aac"); // ADTS
        if (IsMpegAudioFrame(head)) return new DetectedType("audio/mpeg", ".mp3");

        // JSON text: an object whose head names the fields of a glTF document or a Lottie animation.
        var text = JsonObjectHead(head);
        if (text is not null)
        {
            if (text.Contains("\"asset\"", StringComparison.Ordinal) && text.Contains("\"version\"", StringComparison.Ordinal)
                && !text.Contains("\"layers\"", StringComparison.Ordinal))
                return new DetectedType(GltfJsonMime, ".gltf");
            if (text.Contains("\"layers\"", StringComparison.Ordinal) && text.Contains("\"fr\"", StringComparison.Ordinal)
                && text.Contains("\"ip\"", StringComparison.Ordinal) && text.Contains("\"op\"", StringComparison.Ordinal))
                return new DetectedType(LottieMime, ".json");
        }
        return null;
    }

    /// <summary>An MPEG-1/2 audio frame header: sync, a defined version and layer, a valid bitrate and sample-rate index.</summary>
    private static bool IsMpegAudioFrame(ReadOnlySpan<byte> h)
    {
        if (h.Length < 4 || h[0] != 0xFF || (h[1] & 0xE0) != 0xE0) return false;
        var version = (h[1] >> 3) & 0x03;
        var layer = (h[1] >> 1) & 0x03;
        var bitrate = (h[2] >> 4) & 0x0F;
        var sampleRate = (h[2] >> 2) & 0x03;
        return version != 0x01 && layer != 0x00 && bitrate is not (0x00 or 0x0F) && sampleRate != 0x03;
    }

    /// <summary>The head as text when it is a JSON object (optional UTF-8 BOM and whitespace, then '{'), else null.</summary>
    private static string? JsonObjectHead(ReadOnlySpan<byte> head)
    {
        var p = head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF ? 3 : 0;
        while (p < head.Length && head[p] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') p++;
        if (p >= head.Length || head[p] != (byte)'{') return null;
        try
        {
            return new System.Text.UTF8Encoding(false, true).GetString(head[p..]);
        }
        catch (System.Text.DecoderFallbackException)
        {
            // The 64 KB cut may split a multi-byte character; the ASCII field names are all we need.
            return System.Text.Encoding.ASCII.GetString(head[p..]);
        }
    }
}
